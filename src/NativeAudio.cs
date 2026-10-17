// KS Bluetooth reconnection adapted from ToothTray, Copyright (c) 2023 Jing Xia.
// BSD-2-Clause; full notice in THIRD-PARTY-NOTICES.txt and embedded in the EXE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Karmac
{
    internal sealed class AudioEndpoint
    {
        public string Id;
        public string Name;
        public string ControllerId;
        public int State;
        public int Flow;
        public bool Active { get { return State == 1; } }
    }

    internal sealed class AudioTarget
    {
        public string ContainerId;
        public string Name { get; set; }
        public string Address;
        public string BluetoothId;
        public bool RequiresPairingCheck;
        public int ModelId;
        public string ModelName { get { return AirPodsModels.Name(ModelId); } }
        public bool IsApple;
        public int? Battery;
        public List<AudioEndpoint> Endpoints = new List<AudioEndpoint>();
        public bool Connected { get { return Endpoints.Any(e => e.Flow == 0 && e.Active); } }
        public bool IsDefault;
        public override string ToString() { return Name; }
    }

    internal static class AudioService
    {
        private static readonly Guid TopologyId = new Guid("2A07407E-6497-4A18-9787-32F79BD0D98F");
        private static readonly Guid KsId = new Guid("28F54685-06FD-11D2-B27A-00A0C9223196");
        private static readonly Guid BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        private static readonly PropertyKey Container = new PropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        private static readonly PropertyKey FriendlyName = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);

        internal static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        internal static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        internal static List<AudioTarget> Enumerate()
        {
            var result = new Dictionary<string, AudioTarget>(StringComparer.OrdinalIgnoreCase);
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection collection = null;
            try
            {
                Check(enumerator.EnumAudioEndpoints(2, 15, out collection));
                uint count; Check(collection.GetCount(out count));
                string defaultId = DefaultId(enumerator, 1);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore store = null;
                    try
                    {
                        Check(collection.Item(i, out device));
                        var controller = ControllerFor(device);
                        if (controller == null) continue;
                        string id; int state; int flow;
                        Check(device.GetId(out id)); Check(device.GetState(out state));
                        Check(((IMMEndpoint)device).GetDataFlow(out flow));
                        Check(device.OpenPropertyStore(0, out store));
                        string containerId = ReadProperty(store, Container);
                        // A missing container cannot safely group different physical headphones.
                        Guid physicalContainer;
                        if (!Guid.TryParse(containerId, out physicalContainer) || physicalContainer == Guid.Empty) continue;
                        containerId = physicalContainer.ToString();
                        var name = ReadProperty(store, FriendlyName);
                        AudioTarget target;
                        if (!result.TryGetValue(containerId, out target))
                        {
                            target = new AudioTarget { ContainerId = containerId, Name = name };
                            result.Add(containerId, target);
                        }
                        target.Endpoints.Add(new AudioEndpoint { Id = id, Name = name,
                            State = state, Flow = flow, ControllerId = controller });
                        if (id == defaultId && flow == 0 && state == 1) target.IsDefault = true;
                    }
                    catch (Exception ex) { Log.Write("Endpoint discovery: " + ex.Message); }
                    finally { Release(store); Release(device); }
                }
            }
            finally { Release(collection); Release(enumerator); }
            return result.Values.Where(t => t.Endpoints.Any(e => e.Flow == 0)).ToList();
        }

        private static string ReadProperty(IPropertyStore store, PropertyKey key)
        {
            PropVariant value;
            Check(store.GetValue(ref key, out value));
            try
            {
                if (value.Type == 31) return Marshal.PtrToStringUni(value.Pointer);
                if (value.Type == 72 && value.Pointer != IntPtr.Zero)
                    return ((Guid)Marshal.PtrToStructure(value.Pointer, typeof(Guid))).ToString();
                return null;
            }
            finally { PropVariantClear(ref value); }
        }

        private static string ControllerFor(IMMDevice device)
        {
            object topologyObject = null;
            try
            {
                var iid = TopologyId;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out topologyObject));
                var topology = (IDeviceTopology)topologyObject;
                uint count; Check(topology.GetConnectorCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IConnector connector = null;
                    try
                    {
                        Check(topology.GetConnector(i, out connector));
                        string id;
                        if (connector.GetDeviceIdConnectedTo(out id) >= 0 && IsBluetoothController(id))
                            return id;
                    }
                    finally { Release(connector); }
                }
                return null;
            }
            finally { Release(topologyObject); }
        }

        internal static bool IsBluetoothController(string id)
        {
            return id != null && id.StartsWith(@"{2}.\\?\bth", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTransientConnectionError(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
                if (current.HResult == unchecked((int)0x80070003) || current.HResult == unchecked((int)0x80070002) ||
                    current.HResult == unchecked((int)0x80070490)) return true;
            return false;
        }

        internal static void RequestConnection(AudioTarget target, bool connect)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            int attempted = 0;
            Exception lastError = null;
            try
            {
                // Render controllers first; no Bluetooth inquiry, service cycling or driver reinstall.
                foreach (string controllerId in target.Endpoints.OrderBy(e => e.Flow)
                    .Select(e => e.ControllerId).Where(IsBluetoothController).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    IMMDevice controller = null;
                    object control = null;
                    try
                    {
                        Check(enumerator.GetDevice(controllerId, out controller));
                        var iid = KsId;
                        Check(controller.Activate(ref iid, 23, IntPtr.Zero, out control));
                        var request = new KsProperty { Set = BtAudio, Id = connect ? 0u : 1u, Flags = 1 };
                        uint returned;
                        Check(((IKsControl)control).KsProperty(ref request, 24, IntPtr.Zero, 0, out returned));
                        attempted++;
                    }
                    catch (Exception ex) { lastError = ex; Log.Write("KS request: " + ex.Message); }
                    finally { Release(control); Release(controller); }
                }
                if (attempted == 0)
                    throw new InvalidOperationException("The Bluetooth audio driver did not accept the request. " +
                        (lastError == null ? "Refresh devices or pair your AirPods in Windows first." : lastError.Message), lastError);
                if (!connect && lastError != null)
                    throw new InvalidOperationException("Windows did not accept every headphone audio disconnect request. Some audio profiles may still be connected. " + lastError.Message, lastError);
            }
            finally { Release(enumerator); }
        }

        internal static List<AudioEndpoint> ReadStates(AudioTarget target)
        {
            var result = new List<AudioEndpoint>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                foreach (var endpoint in target.Endpoints)
                {
                    IMMDevice device = null;
                    try
                    {
                        Check(enumerator.GetDevice(endpoint.Id, out device));
                        int state; Check(device.GetState(out state));
                        result.Add(new AudioEndpoint { Id = endpoint.Id, Name = endpoint.Name,
                            ControllerId = endpoint.ControllerId, Flow = endpoint.Flow, State = state });
                    }
                    catch (COMException)
                    {
                        // Preserve the cached identity through a transient driver re-enumeration.
                        // Dropping it here would make later readiness polls unable to recover.
                        result.Add(new AudioEndpoint { Id = endpoint.Id, Name = endpoint.Name,
                            ControllerId = endpoint.ControllerId, Flow = endpoint.Flow, State = 4 });
                    }
                    finally { Release(device); }
                }
            }
            finally { Release(enumerator); }
            return result;
        }

        internal static bool IsDefaultOutput(IEnumerable<AudioEndpoint> endpoints)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                string current = DefaultId(enumerator, 1);
                return endpoints.Any(e => e.Flow == 0 && e.Active && String.Equals(e.Id, current, StringComparison.OrdinalIgnoreCase));
            }
            finally { Release(enumerator); }
        }

        // IPolicyConfig is a longstanding, undocumented Windows desktop interface.
        // Always verify the resulting role assignments; report routing failures separately.
        internal static string Route(AudioTarget target)
        {
            var render = target.Endpoints.Where(e => e.Flow == 0 && e.Active)
                .OrderBy(e => IsHandsFree(e.Name) ? 1 : 0).FirstOrDefault();
            if (render == null) throw new InvalidOperationException("The AirPods playback endpoint is not active yet.");
            object policyObject = new PolicyConfigClient();
            try
            {
                var policy = (IPolicyConfig)policyObject;
                Check(policy.SetDefaultEndpoint(render.Id, 0));
                Check(policy.SetDefaultEndpoint(render.Id, 1));
                Check(policy.SetDefaultEndpoint(render.Id, 2));
            }
            finally { Release(policyObject); }
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                for (int role = 0; role <= 2; role++)
                    if (!String.Equals(DefaultId(enumerator, role), render.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Windows has not confirmed the default audio route. Try Route audio again.");
            }
            finally { Release(enumerator); }
            return render.Id;
        }

        private static bool IsHandsFree(string name)
        {
            return name != null && (name.IndexOf("hands-free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("handsfree", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string DefaultId(IMMDeviceEnumerator enumerator, int role)
        {
            IMMDevice device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0, role, out device) < 0) return null;
                string id; Check(device.GetId(out id)); return id;
            }
            finally { Release(device); }
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey
    {
        public Guid Format; public uint Id;
        public PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KsProperty
    {
        public Guid Set; public uint Id; public uint Flags;
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }
    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMEndpoint { [PreserveSig] int GetDataFlow(out int flow); }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connector);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint length, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsMethod(IntPtr method, uint length, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsEvent(IntPtr events, uint length, IntPtr data, uint dataLength, out uint returned);
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] internal class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int defaults, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int defaults, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
    }
}
