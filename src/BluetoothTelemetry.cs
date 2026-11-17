// Apple proximity decoding adapted from BT-Charge-Indicator (MIT),
// Copyright (c) 2026 L1GHTSHAPER. See THIRD-PARTY-NOTICES.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using Windows.Foundation;

namespace Karmac
{
    internal static class AirPodsModels
    {
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string> {
            { 0x2002, "AirPods (1st generation)" }, { 0x200F, "AirPods (2nd generation)" },
            { 0x2013, "AirPods (3rd generation)" }, { 0x2019, "AirPods 4" },
            { 0x201B, "AirPods 4 with ANC" }, { 0x200E, "AirPods Pro" },
            { 0x2014, "AirPods Pro 2 (Lightning)" }, { 0x2024, "AirPods Pro 2 (USB-C)" },
            { 0x2027, "AirPods Pro 3" }, { 0x200A, "AirPods Max (Lightning)" },
            { 0x201F, "AirPods Max (USB-C)" }, { 0x202D, "AirPods Max 2" },
            { 0x2036, "AirPods 5" }, { 0x2030, "AirPods 5 with Wireless Charging Case" }
        };
        // Beats share Apple's Bluetooth vendor ID. Karmac is AirPods-only, so these known
        // Beats products are excluded even when Windows exposes audio endpoints for them.
        private static readonly HashSet<int> Beats = new HashSet<int> {
            0x2003, 0x2005, 0x2006, 0x2009, 0x200B, 0x200C, 0x200D, 0x2010, 0x2011, 0x2012,
            0x2016, 0x2017, 0x201D, 0x2025, 0x2026, 0x2038
        };
        // Product identifiers are identity facts, not permission to decode their battery
        // payloads. Keep the validated BLE format allowlist in BatteryMonitor separate.
        private static readonly Dictionary<int, string> Artwork = new Dictionary<int, string> {
            { 0x2002, "airpods-1" }, { 0x200F, "airpods-2" }, { 0x2013, "airpods-3" },
            { 0x2019, "airpods-4" }, { 0x201B, "airpods-4-anc" },
            { 0x2036, "airpods-5" }, { 0x2030, "airpods-5-wireless" },
            { 0x200E, "airpods-pro" }, { 0x2014, "airpods-pro-2-lightning" },
            { 0x2024, "airpods-pro-2-usbc" }, { 0x2027, "airpods-pro-3" },
            { 0x200A, "airpods-max-lightning" }, { 0x201F, "airpods-max-usbc" }, { 0x202D, "airpods-max-2" }
        };
        internal static bool IsKnown(int model) { return Names.ContainsKey(model); }
        internal static int[] KnownIds { get { return Names.Keys.OrderBy(id => id).ToArray(); } }
        internal static string Name(int model)
        {
            string name; return Names.TryGetValue(model, out name) ? name : "Apple audio";
        }
        internal static string ArtworkKey(int model)
        {
            string key; return Artwork.TryGetValue(model, out key) ? key : "audio-unknown";
        }
        // How the UI refers to a pair: "AirPods", or generic "headphones" for an unknown ID.
        internal static string Term(int model)
        {
            return model == 0 || !IsKnown(model) ? "headphones" : "AirPods";
        }
        internal static bool IsBeats(int model) { return Beats.Contains(model); }
        internal static bool IsOverEar(int model)
        {
            return model == 0x200A || model == 0x201F || model == 0x202D;
        }
        internal static bool IsTrueWireless(int model) { return IsKnown(model) && !IsOverEar(model); }
        internal static bool HasCase(int model) { return IsTrueWireless(model); }
        internal static string ComponentLayout(int model) { return HasCase(model) ? "dual-case" : IsTrueWireless(model) ? "dual" : "single"; }
        internal static string Family(int model)
        {
            if (model == 0x2027) return "pro3";
            if (model == 0x200E || model == 0x2014 || model == 0x2024) return "pro";
            if (model == 0x200A || model == 0x201F || model == 0x202D) return "max";
            return IsKnown(model) ? "pods" : "unknown";
        }
    }

    internal sealed class BatteryReading
    {
        public ulong Address;
        public ushort Model;
        public int? Left, Right, Case;
        public bool LeftCharging, RightCharging, CaseCharging;
        public bool LeftInEar, RightInEar;
        public short Rssi;
        public DateTime Seen, FirstSeen;
        public string Source;
        public bool UserSelected;
        // Matched automatically by elimination rather than proven identity.
        public bool Inferred;
        public bool Fresh { get { return DateTime.UtcNow - Seen < TimeSpan.FromSeconds(90); } }
        public bool Worn { get { return LeftInEar || RightInEar; } }
        public string AddressLabel { get { return Address.ToString("X12"); } }
        public override string ToString()
        {
            return "AirPods beacon \u00B7 " + Rssi + " dBm \u00B7 " + AddressLabel.Substring(6) +
                "   L " + Percent(Left) + " / R " + Percent(Right) + " / Case " + Percent(Case);
        }
        internal static string Percent(int? value) { return value.HasValue ? value.Value + "%" : "\u2014"; }
        internal BatteryReading WithSource(string source)
        {
            var copy = (BatteryReading)MemberwiseClone(); copy.Source = source; return copy;
        }
    }

    internal sealed class DeviceBatteryReading
    {
        public int Percent;
        public DateTime Seen;
        public string Source;
        public bool Fresh { get { return DateTime.UtcNow - Seen < TimeSpan.FromMinutes(3); } }
    }

    internal sealed class AirPodsIdentity
    {
        public string Address;
        public ushort Model;
    }

    internal sealed class DiscoveryResult
    {
        public List<AudioTarget> Targets;
        public string Radio = "Unknown";
        public string Warning;
    }

    internal interface IBluetoothRadio
    {
        RadioState State { get; }
        Task<RadioAccessStatus> TurnOnAsync();
    }

    internal static class BluetoothPower
    {
        private sealed class Adapter : IBluetoothRadio
        {
            private readonly Radio radio;
            internal Adapter(Radio value) { radio = value; }
            public RadioState State { get { return radio.State; } }
            public Task<RadioAccessStatus> TurnOnAsync() { return WinRtTask.Wait(radio.SetStateAsync(RadioState.On), 8000); }
        }

        // One rule for every caller: no adapter, any On, all Off/Disabled, otherwise Unknown.
        internal static string Describe(IList<Radio> radios)
        {
            var bluetooth = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();
            if (bluetooth.Count == 0) return "No adapter";
            if (bluetooth.Any(r => r.State == RadioState.On)) return "On";
            return bluetooth.All(r => r.State == RadioState.Off || r.State == RadioState.Disabled) ? "Off" : "Unknown";
        }

        internal static async Task<string> DescribeAsync()
        {
            return Describe((await WinRtTask.Wait(Radio.GetRadiosAsync(), 4000)).ToList());
        }

        // Call from the user's connect action: Windows can require first-use consent.
        internal static async Task<bool> EnsureOnAsync(Action starting)
        {
            var radios = await WinRtTask.Wait(Radio.GetRadiosAsync(), 4000);
            return await EnsureOnAsync(radios.Where(r => r.Kind == RadioKind.Bluetooth)
                .Select(r => (IBluetoothRadio)new Adapter(r)).ToList(),
                () => WinRtTask.Wait(Radio.RequestAccessAsync()), starting, 8000);
        }

        // Returns true only when this operation enabled an adapter. Never cycles
        // an already-on adapter or touches Wi-Fi, other radios, or pairing records.
        internal static async Task<bool> EnsureOnAsync(IList<IBluetoothRadio> radios,
            Func<Task<RadioAccessStatus>> requestAccess, Action starting, int timeoutMs)
        {
            if (radios.Any(r => r.State == RadioState.On)) return false;
            if (radios.Count == 0) throw new InvalidOperationException("No Bluetooth adapter was found.");
            if (!radios.Any(r => r.State == RadioState.Off))
                throw new InvalidOperationException("Bluetooth is disabled by Windows or a hardware switch. Enable the adapter to connect.");
            if (starting != null) starting();
            var access = await requestAccess();
            if (access != RadioAccessStatus.Allowed)
                throw new InvalidOperationException("Windows did not allow Bluetooth to turn on (" + access + "). Check radio access or your administrator's policy.");
            Exception last = null;
            foreach (var radio in radios)
            {
                if (radio.State == RadioState.On) return true;
                if (radio.State != RadioState.Off) continue;
                try
                {
                    var result = await radio.TurnOnAsync();
                    if (result != RadioAccessStatus.Allowed)
                        throw new InvalidOperationException("Windows refused to enable the Bluetooth adapter (" + result + ").");
                    var wait = Stopwatch.StartNew();
                    while (radio.State != RadioState.On && wait.ElapsedMilliseconds < timeoutMs)
                        await Task.Delay(50);
                    if (radio.State == RadioState.On) return true;
                    throw new TimeoutException("The Bluetooth adapter did not turn on in time. Check its hardware switch or Windows radio settings.");
                }
                catch (Exception error) { last = error; }
            }
            throw last ?? new InvalidOperationException("No Bluetooth adapter could be enabled.");
        }
    }

    internal static class DeviceDiscovery
    {
        internal static async Task<DiscoveryResult> DiscoverAsync()
        {
            var result = new DiscoveryResult { Targets = AudioService.Enumerate() };
            var pairedTargets = new HashSet<AudioTarget>();
            try
            {
                try { result.Radio = await BluetoothPower.DescribeAsync(); }
                catch (Exception error) { Log.Write("Radio state: " + error.Message); }
                string[] props = { "System.Devices.Aep.ContainerId", "System.Devices.Aep.DeviceAddress" };
                DeviceInformationCollection paired = null;
                try { paired = await WinRtTask.Wait(DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), props), 4000); }
                catch (TimeoutException) { }
                if (paired != null)
                {
                    foreach (var device in paired)
                    {
                        string container = Property(device, props[0]);
                        var target = result.Targets.FirstOrDefault(t => SameGuid(t.ContainerId, container));
                        // Audio topology may disappear while the adapter is off.
                        // Keep a confirmed saved pair available for one-click power-on.
                        if (target == null)
                        {
                            Guid id;
                            if (!Guid.TryParse(container, out id) || id == Guid.Empty) continue;
                            target = new AudioTarget { ContainerId = id.ToString(), Name = device.Name };
                            result.Targets.Add(target);
                        }
                        pairedTargets.Add(target);
                        target.BluetoothId = device.Id;
                        if (!String.IsNullOrWhiteSpace(device.Name)) target.Name = device.Name;
                        target.Address = NormalizeAddress(Property(device, props[1]));
                        // A friendly name alone cannot establish the manufacturer.
                        // The same-container hardware query below supplies identity.
                        target.IsApple = false;
                    }
                }
                else result.Warning = "Windows has not confirmed the saved Bluetooth devices yet. Refresh to try again.";
                // Recognize renamed AirPods by their verified product ID.
                // Future Apple IDs can connect generically only when Windows already
                // exposes Bluetooth audio endpoints in this same paired container.
                // Query only the paired containers, never every PnP device on the PC.
                string[] pnpProps = { "System.Devices.ContainerId", "System.Devices.HardwareIds",
                    "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", "System.Devices.BatteryLife" };
                var lookups = pairedTargets.Select(async target => {
                    try
                    {
                        var devices = await WinRtTask.Wait(DeviceInformation.FindAllAsync(ContainerQuery(target.ContainerId),
                            pnpProps, DeviceInformationKind.Device), 3000);
                        return new { Target = target, Devices = devices.ToList() };
                    }
                    catch (Exception error) { Log.Write("Device details " + target.Name + ": " + error.Message); return null; }
                }).ToList();
                foreach (var lookup in await Task.WhenAll(lookups))
                {
                    if (lookup == null) continue;
                    var target = lookup.Target;
                    foreach (var device in lookup.Devices)
                    {
                        object hardware;
                        if (device.Properties.TryGetValue(pnpProps[1], out hardware))
                        {
                            var ids = hardware as string[];
                            if (ids != null)
                            {
                                foreach (var id in ids)
                                    if (IsAppleAudioHardwareId(id, target.Endpoints.Count != 0))
                                    {
                                        target.IsApple = true;
                                        int offset = id.IndexOf("PID&", StringComparison.OrdinalIgnoreCase);
                                        int model;
                                        if (offset >= 0 && offset + 8 <= id.Length && Int32.TryParse(id.Substring(offset + 4, 4),
                                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out model) && AirPodsModels.IsKnown(model))
                                            target.ModelId = model;
                                        break;
                                    }
                            }
                        }
                        for (int i = 2; i < pnpProps.Length; i++)
                        {
                            int battery;
                            if (Int32.TryParse(Property(device, pnpProps[i]), out battery) && battery >= 0 && battery <= 100)
                                target.Battery = battery;
                        }
                    }
                }
            }
            catch (Exception ex) { result.Warning = ex.Message; Log.Write("Bluetooth metadata: " + ex.Message); }
            // Old audio endpoints can survive unpairing. Only devices confirmed by the
            // paired-device query may become reconnect targets; proximity is not identity.
            result.Targets = result.Targets.Where(t => pairedTargets.Contains(t) &&
                t.IsApple).OrderBy(t => t.Name).ToList();
            return result;
        }

        internal static string ContainerQuery(string containerId)
        {
            Guid container;
            if (!Guid.TryParse(containerId, out container)) throw new ArgumentException("Invalid device container.");
            return "System.Devices.ContainerId:=\"{" + container + "}\"";
        }

        internal static async Task VerifyPairAsync(AudioTarget target, CancellationToken token = default(CancellationToken))
        {
            var clock = Stopwatch.StartNew();
            string[] properties = { "System.Devices.Aep.ContainerId", "System.Devices.Aep.DeviceAddress" };
            do
            {
                token.ThrowIfCancellationRequested();
                DeviceInformation match = null;
                if (!String.IsNullOrWhiteSpace(target.BluetoothId))
                {
                    try
                    {
                        var info = await WinRtTask.Wait(DeviceInformation.CreateFromIdAsync(target.BluetoothId, properties,
                            DeviceInformationKind.AssociationEndpoint), 1500, token);
                        if (info != null && info.Pairing.IsPaired && SameGuid(Property(info, properties[0]), target.ContainerId)) match = info;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Log.Write("Saved pair lookup: " + error.Message); }
                }
                if (match == null)
                {
                    try
                    {
                        var paired = await WinRtTask.Wait(DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), properties), 2500, token);
                        match = paired.FirstOrDefault(info => SameGuid(Property(info, properties[0]), target.ContainerId));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { Log.Write("Saved pair confirmation: " + error.Message); }
                }
                if (match != null)
                {
                    string address = NormalizeAddress(Property(match, properties[1]));
                    if (target.Address != null && address != null && target.Address != address)
                        throw new InvalidOperationException("Windows reported a different identity for this saved pair. Choose your AirPods again.");
                    target.Address = address ?? target.Address; target.BluetoothId = match.Id;
                    target.RequiresPairingCheck = false;
                    return;
                }
                await Task.Delay(150, token);
            } while (clock.ElapsedMilliseconds < 4500);
            throw new InvalidOperationException("Windows hasn't confirmed this saved pair. It may still be restoring Bluetooth, or the pair was removed. Try again, or pair again in Windows Settings if it was removed.");
        }

        internal static bool SameGuid(string left, string right)
        {
            Guid a, b;
            return Guid.TryParse(left, out a) && Guid.TryParse(right, out b) && a == b;
        }
        internal static string Property(DeviceInformation info, string key)
        {
            object value;
            return info.Properties.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }
        internal static string NormalizeAddress(string address)
        {
            if (address == null) return null;
            string normalized = address.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
            ulong value;
            return normalized.Length == 12 && UInt64.TryParse(normalized, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out value) && value != 0 ? normalized : null;
        }
        internal static bool IsAirPodsHardwareId(string id)
        {
            return IsAppleAudioHardwareId(id, false);
        }
        internal static bool IsAppleAudioHardwareId(string id, bool hasBluetoothAudioEndpoint)
        {
            // 0001 denotes a Bluetooth SIG company identifier, 004C is Apple.
            // Do not confuse USB vendor 05AC or arbitrary substring matches with it.
            if (id == null || !id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)) return false;
            int vendor = id.IndexOf("VID&0001004C", StringComparison.OrdinalIgnoreCase);
            if (vendor < 0 || (vendor + 12 < id.Length && Uri.IsHexDigit(id[vendor + 12]))) return false;
            int offset = id.IndexOf("PID&", StringComparison.OrdinalIgnoreCase);
            if (offset < 0 || offset + 8 > id.Length) return false;
            int model;
            return Int32.TryParse(id.Substring(offset + 4, 4), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out model) && (offset + 8 == id.Length || !Uri.IsHexDigit(id[offset + 8])) &&
                !AirPodsModels.IsBeats(model) && (AirPodsModels.IsKnown(model) || hasBluetoothAudioEndpoint);
        }
    }

    // Framework's AsTask facade expects an SDK union Windows.winmd. Bridging the native
    // Completed callback lets builds use the split metadata already shipped with Windows,
    // without polling. A timeout or cancellation cancels the underlying operation too.
    internal static class WinRtTask
    {
        internal static Task<T> Wait<T>(IAsyncOperation<T> operation, int timeoutMilliseconds = 10000,
            CancellationToken token = default(CancellationToken))
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout = new CancellationTokenSource(timeoutMilliseconds);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
            var registration = linked.Token.Register(() => {
                bool set = token.IsCancellationRequested ? completion.TrySetCanceled(token) :
                    completion.TrySetException(new TimeoutException("Windows took too long to respond."));
                if (set) { try { operation.Cancel(); } catch { } }
            });
            try
            {
                // Assigning Completed on an already-finished operation invokes it immediately.
                operation.Completed = (finished, status) => {
                    try
                    {
                        if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                        else completion.TrySetResult(finished.GetResults());
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally
                    {
                        registration.Dispose(); linked.Dispose(); timeout.Dispose();
                        try { finished.Close(); } catch { }
                    }
                };
            }
            catch (Exception error)
            {
                registration.Dispose(); linked.Dispose(); timeout.Dispose();
                completion.TrySetException(error);
            }
            return completion.Task;
        }
    }

    internal sealed class BatteryMonitor : IDisposable
    {
        // A proximity hint, not a distance measurement or an identity check.
        internal const short NearbyRssiThreshold = -65;
        // "Right next to this PC": used for automatic matching and the case-open card.
        internal const short CloseRssi = -60;
        // Weakest signal accepted for automatic matching of a connected pair.
        internal const short InferRssi = -80;
        private readonly object gate = new object();
        private readonly Dictionary<ulong, BatteryReading> readings = new Dictionary<ulong, BatteryReading>();
        private readonly Dictionary<ulong, ResolvedIdentity> identities = new Dictionary<ulong, ResolvedIdentity>();
        private readonly Dictionary<ulong, DateTime> identityAttempts = new Dictionary<ulong, DateTime>();
        private readonly HashSet<ulong> identityInFlight = new HashSet<ulong>();
        private readonly Dictionary<string, AuthorizedTarget> targets = new Dictionary<string, AuthorizedTarget>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DeviceBatteryReading> deviceBatteries = new Dictionary<string, DeviceBatteryReading>(StringComparer.OrdinalIgnoreCase);
        // A beacon accepted for a pair, either chosen by the user or inferred. It follows
        // the pair across BLE address rotations instead of expiring on a timer.
        private readonly Dictionary<string, SourceLock> locks = new Dictionary<string, SourceLock>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> connectedSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> autoSuppressed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DeviceWatcher> batteryWatchers = new Dictionary<string, DeviceWatcher>(StringComparer.OrdinalIgnoreCase);
        private DateTime nextBatteryRefresh;
        private bool batteryRefreshInFlight, disposed;
        private BluetoothLEAdvertisementWatcher watcher;
        internal string Status = "Starting battery listener";
        // Raised (on a background thread) when a supported model starts advertising
        // close by, typically because its case was just opened.
        internal event Action<BatteryReading> Appeared;

        private sealed class SourceLock
        {
            internal ulong Address;
            internal string TargetAddress;
            internal int Model;
            internal bool UserSelected;
        }

        private sealed class AuthorizedTarget
        {
            internal string ContainerId, Address;
            internal int Model;
            internal bool Connected;
        }
        private sealed class ResolvedIdentity
        {
            internal string ContainerId, ClassicAddress;
            internal int Model;
            internal DateTime Seen;
        }

        // Pass only the confirmed paired-device discovery snapshot. This authorizes
        // metadata associations and optional battery reads for those exact containers.
        internal void RegisterTargets(IList<AudioTarget> snapshot)
        {
            Revive();
            List<AuthorizedTarget> connected;
            lock (gate)
            {
                if (disposed) return;
                targets.Clear();
                foreach (var target in snapshot ?? new List<AudioTarget>())
                {
                    Guid container;
                    if (!Guid.TryParse(target.ContainerId, out container) || container == Guid.Empty) continue;
                    string key = container.ToString();
                    targets[key] = new AuthorizedTarget { ContainerId = key, Address = DeviceDiscovery.NormalizeAddress(target.Address),
                        Model = target.ModelId, Connected = target.Connected };
                    if (target.Battery.HasValue && target.Connected)
                        deviceBatteries[key] = new DeviceBatteryReading { Percent = target.Battery.Value,
                            Seen = DateTime.UtcNow, Source = "Windows last reported battery (may be cached)" };
                    if (!target.PlaybackReady) connectedSince.Remove(key);
                    else if (!connectedSince.ContainsKey(key)) connectedSince[key] = DateTime.UtcNow;
                }
                foreach (string key in deviceBatteries.Keys.Where(k => !targets.ContainsKey(k)).ToArray()) deviceBatteries.Remove(key);
                foreach (string key in connectedSince.Keys.Where(k => !targets.ContainsKey(k)).ToArray()) connectedSince.Remove(key);
                foreach (var pair in locks.Where(p => { AuthorizedTarget t; return !targets.TryGetValue(p.Key, out t) ||
                    !String.Equals(p.Value.TargetAddress, t.Address, StringComparison.OrdinalIgnoreCase); }).ToArray())
                    locks.Remove(pair.Key);
                connected = targets.Values.Where(t => t.Connected).ToList();
                UpdateBatteryWatchers(connected);
                if (connected.Count == 0 || batteryRefreshInFlight || DateTime.UtcNow < nextBatteryRefresh) return;
                batteryRefreshInFlight = true;
                nextBatteryRefresh = DateTime.UtcNow.AddSeconds(30);
            }
            RefreshDeviceBatteriesAsync(connected);
        }

        private static readonly string[] BatteryProperties = { "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", "System.Devices.BatteryLife" };

        // Windows' own Hands-Free battery (DEVPKEY_Bluetooth_Battery), pushed as it changes
        // and scoped to each connected pair's container. Call with the gate held.
        private void UpdateBatteryWatchers(List<AuthorizedTarget> connected)
        {
            foreach (string key in batteryWatchers.Keys.Where(k => !connected.Any(t => t.ContainerId == k)).ToArray())
            { StopWatcher(batteryWatchers[key]); batteryWatchers.Remove(key); }
            foreach (var target in connected)
            {
                if (batteryWatchers.ContainsKey(target.ContainerId)) continue;
                string container = target.ContainerId;
                try
                {
                    var deviceWatcher = DeviceInformation.CreateWatcher(DeviceDiscovery.ContainerQuery(container), BatteryProperties, DeviceInformationKind.Device);
                    deviceWatcher.Added += (sender, info) => ReadBatteryProperties(container, info.Properties);
                    deviceWatcher.Updated += (sender, update) => ReadBatteryProperties(container, update.Properties);
                    // Windows only delivers updates once Added, Updated and Removed all have handlers.
                    deviceWatcher.Removed += (sender, update) => { };
                    deviceWatcher.Start();
                    batteryWatchers[container] = deviceWatcher;
                }
                catch (Exception error) { Log.Write("Battery watcher: " + error.Message); }
            }
        }

        private void ReadBatteryProperties(string container, IReadOnlyDictionary<string, object> properties)
        {
            foreach (string key in BatteryProperties)
            {
                object value; int percent;
                if (properties.TryGetValue(key, out value) && value != null &&
                    Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out percent) && percent >= 0 && percent <= 100)
                    StoreDeviceBattery(container, percent, "Windows reported battery");
            }
        }

        private static void StopWatcher(DeviceWatcher deviceWatcher)
        {
            try
            {
                if (deviceWatcher.Status == DeviceWatcherStatus.Started || deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    deviceWatcher.Stop();
            }
            catch { }
        }

        internal DeviceBatteryReading GetDeviceBattery(AudioTarget target)
        {
            if (target == null || !target.Connected) return null;
            Guid container;
            if (!Guid.TryParse(target.ContainerId, out container)) return null;
            lock (gate)
            {
                DeviceBatteryReading value;
                return deviceBatteries.TryGetValue(container.ToString(), out value) && value.Fresh ? value : null;
            }
        }

        // Starts (or restarts) the Bluetooth LE listener. Safe to call at any time: with
        // Bluetooth off the start fails, and a later call retries from scratch.
        internal void Start()
        {
            lock (gate) { if (disposed) return; }
            wanted = true;
            try
            {
                if (watcher != null)
                {
                    var state = watcher.Status;
                    if (state == BluetoothLEAdvertisementWatcherStatus.Started) { Status = "Listening for AirPods battery"; return; }
                    // A watcher that never started (Created), or was stopped or aborted by
                    // the radio going off, can be started again.
                    if (state == BluetoothLEAdvertisementWatcherStatus.Created ||
                        state == BluetoothLEAdvertisementWatcherStatus.Stopped ||
                        state == BluetoothLEAdvertisementWatcherStatus.Aborted)
                    {
                        watcher.Start();
                        Status = "Listening for AirPods battery";
                    }
                    return;
                }
                var created = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                created.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = 0x004C });
                created.Received += Received;
                created.Stopped += (sender, args) => { Status = "Battery listener stopped: " + args.Error; };
                watcher = created;
                created.Start();
                Status = "Listening for AirPods battery";
            }
            catch (Exception ex)
            {
                Status = "Battery listener unavailable";
                Log.Write("BLE: " + ex.Message);
                // Drop the failed watcher so the next Start() builds a fresh one.
                var failed = watcher; watcher = null;
                if (failed != null) { try { failed.Received -= Received; } catch { } }
            }
        }

        // Called whenever device state is refreshed: if the listener is not running (for
        // example Bluetooth was off at launch), try again, at most every 10 seconds.
        private DateTime nextRevive;
        private bool wanted;
        private void Revive()
        {
            if (!wanted) return;
            if (watcher != null && watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) return;
            if (DateTime.UtcNow < nextRevive) return;
            nextRevive = DateTime.UtcNow.AddSeconds(10);
            Start();
        }

        private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                foreach (var data in args.Advertisement.ManufacturerData)
                {
                    if (data.CompanyId != 0x004C) continue;
                    var bytes = new byte[data.Data.Length];
                    using (var reader = DataReader.FromBuffer(data.Data)) reader.ReadBytes(bytes);
                    var identity = ParseIdentity(bytes);
                    if (identity != null)
                    {
                        lock (gate)
                        {
                            if (disposed) return;
                            foreach (var old in identities.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromMinutes(3)).Select(p => p.Key).ToArray())
                                identities.Remove(old);
                            if (identities.Count < 128 || identities.ContainsKey(args.BluetoothAddress))
                                identities[args.BluetoothAddress] = new ResolvedIdentity {
                                    ClassicAddress = identity.Address, Model = identity.Model, Seen = DateTime.UtcNow };
                        }
                    }
                    var reading = Parse(bytes);
                    if (reading != null)
                    {
                        reading.Address = args.BluetoothAddress; reading.Rssi = args.RawSignalStrengthInDBm;
                        reading.Seen = DateTime.UtcNow;
                        bool appeared;
                        lock (gate)
                        {
                            if (disposed) return;
                            BatteryReading previous;
                            appeared = !readings.TryGetValue(reading.Address, out previous) || !previous.Fresh;
                            reading.FirstSeen = appeared ? reading.Seen : previous.FirstSeen;
                            readings[reading.Address] = reading;
                            foreach (var address in readings.Where(p => DateTime.UtcNow - p.Value.Seen > TimeSpan.FromMinutes(3)).Select(p => p.Key).ToArray())
                            {
                                readings.Remove(address); identities.Remove(address); identityAttempts.Remove(address);
                            }
                        }
                        var handler = Appeared;
                        if (appeared && handler != null && reading.Rssi >= CloseRssi) handler(reading);
                    }
                    if (reading != null || identity != null) QueueIdentityLookup(args.BluetoothAddress, args.BluetoothAddressType);
                }
            }
            catch (Exception ex) { Log.Write("BLE packet: " + ex.Message); }
        }

        // The short 07/0F pairing-mode-00 variant carries a classic address at
        // offsets 5..10, not battery nibbles. Layout: Andropods and RBS BAET research.
        internal static AirPodsIdentity ParseIdentity(byte[] data)
        {
            if (data == null) return null;
            for (int offset = 0; offset + 2 <= data.Length; )
            {
                int length = data[offset + 1];
                if (length > data.Length - offset - 2) return null;
                if (data[offset] == 7 && length == 15 && data[offset + 2] == 0)
                {
                    int model = data[offset + 3] | (data[offset + 4] << 8);
                    if (!IsSupportedModel(model)) return null;
                    string address = String.Concat(data.Skip(offset + 5).Take(6).Select(b => b.ToString("X2")));
                    address = DeviceDiscovery.NormalizeAddress(address);
                    return address == null ? null : new AirPodsIdentity { Address = address, Model = (ushort)model };
                }
                offset += length + 2;
            }
            return null;
        }

        private void QueueIdentityLookup(ulong address, BluetoothAddressType addressType)
        {
            lock (gate)
            {
                if (disposed || targets.Count == 0 || identityInFlight.Count >= 2 || identityInFlight.Contains(address)) return;
                DateTime attempted;
                if (identityAttempts.TryGetValue(address, out attempted) && DateTime.UtcNow - attempted < TimeSpan.FromSeconds(60)) return;
                // Bound cache growth in crowded places. Removing old attempts never
                // authorizes an address; it merely allows a later metadata retry.
                foreach (var old in identityAttempts.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMinutes(3)).Select(p => p.Key).ToArray())
                    identityAttempts.Remove(old);
                if (identityAttempts.Count >= 64) return;
                identityAttempts[address] = DateTime.UtcNow; identityInFlight.Add(address);
            }
            ResolveIdentityAsync(address, addressType);
        }

        private async void ResolveIdentityAsync(ulong address, BluetoothAddressType addressType)
        {
            BluetoothLEDevice device = null;
            try
            {
                // This is a cache/identity lookup only. Never enumerate GATT services,
                // request pairing/access or maintain a connection to an unknown beacon.
                device = await WinRtTask.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType), 2500).ConfigureAwait(false);
                if (device == null) return;
                var info = await IdentityInformationAsync(device).ConfigureAwait(false);
                string container = IdentityContainer(info);
                Guid value;
                if (!Guid.TryParse(container, out value) || value == Guid.Empty) return;
                container = value.ToString();
                lock (gate)
                {
                    if (!disposed && targets.ContainsKey(container))
                        identities[address] = new ResolvedIdentity { ContainerId = container, Seen = DateTime.UtcNow };
                }
            }
            catch (Exception error) { Log.Write("BLE identity metadata: " + error.Message); }
            finally
            {
                if (device != null) { try { device.Dispose(); } catch { } }
                lock (gate) identityInFlight.Remove(address);
            }
        }

        private static async Task<DeviceInformation> IdentityInformationAsync(BluetoothLEDevice device)
        {
            var info = device.DeviceInformation;
            string[] properties = { "System.Devices.Aep.ContainerId", "System.Devices.ContainerId", "System.Devices.Aep.IsPaired" };
            return await WinRtTask.Wait(DeviceInformation.CreateFromIdAsync(info.Id, properties, info.Kind), 2000).ConfigureAwait(false);
        }

        private static string IdentityContainer(DeviceInformation info)
        {
            if (info == null) return null;
            string container = DeviceDiscovery.Property(info, "System.Devices.Aep.ContainerId");
            Guid value;
            return Guid.TryParse(container, out value) && value != Guid.Empty ? value.ToString() :
                DeviceDiscovery.Property(info, "System.Devices.ContainerId");
        }

        private async void RefreshDeviceBatteriesAsync(List<AuthorizedTarget> connected)
        {
            try
            {
                string[] properties = { "System.Devices.ContainerId", "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", "System.Devices.BatteryLife" };
                foreach (var target in connected)
                {
                    // Some drivers attach battery properties to an interface instead
                    // of its PnP device. Read only interfaces in the authorized container.
                    string query = "System.Devices.ContainerId:=\"{" + target.ContainerId + "}\"";
                    var interfaces = await WinRtTask.Wait(DeviceInformation.FindAllAsync(query, properties, DeviceInformationKind.DeviceInterface), 2000).ConfigureAwait(false);
                    foreach (var item in interfaces)
                        for (int i = 1; i < properties.Length; i++)
                        {
                            int percent;
                            if (Int32.TryParse(DeviceDiscovery.Property(item, properties[i]), out percent) && percent >= 0 && percent <= 100)
                                StoreDeviceBattery(target.ContainerId, percent, "Windows last reported battery (may be cached)");
                        }
                }
                string[] leProperties = { "System.Devices.Aep.ContainerId", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsPaired" };
                var paired = await WinRtTask.Wait(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), leProperties), 2500).ConfigureAwait(false);
                foreach (var info in paired)
                {
                    var target = connected.FirstOrDefault(t => DeviceDiscovery.SameGuid(t.ContainerId, IdentityContainer(info)));
                    if (target == null || !info.Pairing.IsPaired) continue;
                    string address = DeviceDiscovery.NormalizeAddress(DeviceDiscovery.Property(info, leProperties[1]));
                    ulong numeric;
                    if (address == null || !UInt64.TryParse(address, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out numeric)) continue;
                    await ReadGattBatteryAsync(target.ContainerId, numeric).ConfigureAwait(false);
                }
            }
            catch (Exception error) { Log.Write("Automatic device battery: " + error.Message); }
            finally { lock (gate) batteryRefreshInFlight = false; }
        }

        private async Task ReadGattBatteryAsync(string containerId, ulong address)
        {
            BluetoothLEDevice device = null;
            GattDeviceServicesResult services = null;
            try
            {
                lock (gate) { if (disposed || !targets.ContainsKey(containerId) || !targets[containerId].Connected) return; }
                device = await WinRtTask.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(address), 2500).ConfigureAwait(false);
                if (device == null || !device.DeviceInformation.Pairing.IsPaired) return;
                var identity = await IdentityInformationAsync(device).ConfigureAwait(false);
                if (!DeviceDiscovery.SameGuid(containerId, IdentityContainer(identity))) return;
                services = await WinRtTask.Wait(device.GetGattServicesForUuidAsync(new Guid("0000180F-0000-1000-8000-00805F9B34FB"), BluetoothCacheMode.Cached), 3000).ConfigureAwait(false);
                if (services.Status != GattCommunicationStatus.Success) return;
                int? lowest = null;
                foreach (var service in services.Services)
                {
                    var characteristics = await WinRtTask.Wait(service.GetCharacteristicsForUuidAsync(new Guid("00002A19-0000-1000-8000-00805F9B34FB"), BluetoothCacheMode.Cached), 2500).ConfigureAwait(false);
                    if (characteristics.Status != GattCommunicationStatus.Success) continue;
                    foreach (var characteristic in characteristics.Characteristics)
                    {
                        if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0) continue;
                        var value = await WinRtTask.Wait(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), 3000).ConfigureAwait(false);
                        if (value.Status != GattCommunicationStatus.Success || value.Value == null || value.Value.Length == 0) continue;
                        int percent;
                        using (var reader = DataReader.FromBuffer(value.Value)) percent = reader.ReadByte();
                        if (percent <= 100) lowest = lowest.HasValue ? Math.Min(lowest.Value, percent) : percent;
                    }
                }
                if (lowest.HasValue) StoreDeviceBattery(containerId, lowest.Value, "Standard Bluetooth battery service");
            }
            finally
            {
                if (services != null) foreach (var service in services.Services) { try { service.Dispose(); } catch { } }
                if (device != null) { try { device.Dispose(); } catch { } }
            }
        }

        private void StoreDeviceBattery(string container, int percent, string source)
        {
            lock (gate)
            {
                AuthorizedTarget target;
                if (!disposed && targets.TryGetValue(container, out target) && target.Connected)
                    deviceBatteries[container] = new DeviceBatteryReading { Percent = percent, Seen = DateTime.UtcNow, Source = source };
            }
        }

        // ManufacturerData excludes Apple's 0x004C company bytes. Walk TLVs instead of
        // trusting fixed offsets into unrelated Apple advertisements. Unknown nibbles stay unknown.
        internal static BatteryReading Parse(byte[] data)
        {
            if (data == null) return null;
            for (int offset = 0; offset + 2 <= data.Length; )
            {
                int type = data[offset], length = data[offset + 1];
                if (length > data.Length - offset - 2) return null;
                if (type == 7 && length == 25)
                {
                    int model = data[offset + 3] | (data[offset + 4] << 8);
                    // Recognized AirPods model identifiers; do not parse Beats or future encrypted formats as battery.
                    if (!IsSupportedModel(model)) return null;
                    int state = data[offset + 5], batteries = data[offset + 6], charge = data[offset + 7];
                    bool swapped = (state & 0x20) == 0;
                    int? high = Nibble(batteries >> 4), low = Nibble(batteries & 15);
                    var value = new BatteryReading {
                        Model = (ushort)model, Left = swapped ? high : low, Right = swapped ? low : high,
                        Case = Nibble(charge & 15),
                        LeftCharging = (charge & (swapped ? 0x20 : 0x10)) != 0,
                        RightCharging = (charge & (swapped ? 0x10 : 0x20)) != 0,
                        CaseCharging = (charge & 0x40) != 0,
                        LeftInEar = (state & (swapped ? 0x08 : 0x02)) != 0,
                        RightInEar = (state & (swapped ? 0x02 : 0x08)) != 0,
                        Seen = DateTime.UtcNow
                    };
                    return value.Left.HasValue || value.Right.HasValue || value.Case.HasValue ? value : null;
                }
                offset += 2 + length;
            }
            return null;
        }

        internal static bool IsSupportedModel(int model)
        {
            // These are the models whose legacy 07/19 battery layout was already
            // supported. A new catalog identity must never opt into this decoder.
            switch (model)
            {
                case 0x2002: case 0x200F: case 0x2013: case 0x2019: case 0x201B:
                case 0x200E: case 0x2014: case 0x2024: case 0x2027: case 0x200A: case 0x201F:
                    return true;
                default: return false;
            }
        }
        private static int? Nibble(int value) { return value >= 0 && value <= 10 ? (int?)(value * 10) : null; }
        internal List<BatteryReading> Candidates()
        {
            lock (gate) return readings.Values.Where(r => r.Fresh && r.Rssi >= NearbyRssiThreshold).OrderByDescending(r => r.Rssi).ToList();
        }
        // "Not my AirPods": drops an inferred source and keeps automatic inference off for
        // this pair for a while, so only proven identity supplies its battery.
        internal void ClearSource(AudioTarget target)
        {
            Guid container;
            if (target == null || !Guid.TryParse(target.ContainerId, out container)) return;
            lock (gate)
            {
                locks.Remove(container.ToString());
                autoSuppressed[container.ToString()] = DateTime.UtcNow.AddMinutes(30);
            }
        }

        // Called with the gate held once a locked address has gone quiet. A pair rotates its
        // private address by stopping on the old one and starting on a new one at about the
        // same moment (within ~2 s), with the same model, charge and signal. Move the lock
        // only when exactly one newcomer fits; ambiguity leaves it to go stale.
        private void FollowRotation(SourceLock held, BatteryReading old)
        {
            if (DateTime.UtcNow - old.Seen < TimeSpan.FromSeconds(3)) return;
            var matches = readings.Values.Where(r => r.Address != held.Address && r.Model == held.Model && r.Fresh &&
                r.FirstSeen >= old.Seen.AddSeconds(-2) && r.FirstSeen <= old.Seen.AddSeconds(2) &&
                SameCharge(old, r) && Math.Abs(old.Rssi - r.Rssi) <= 15).ToList();
            if (matches.Count == 1) held.Address = matches[0].Address;
        }

        private static bool SameCharge(BatteryReading a, BatteryReading b)
        {
            return Close(a.Left, b.Left) && Close(a.Right, b.Right) && Close(a.Case, b.Case);
        }
        private static bool Close(int? a, int? b) { return !a.HasValue || !b.HasValue || Math.Abs(a.Value - b.Value) <= 10; }

        // Called with the gate held. While our pair is connected and playing, accept a
        // beacon automatically when:
        //  - it's the one and only fresh beacon of that model in range at all (a second
        //    one of the same model makes it ambiguous, so nothing is shown),
        //  - it has been heard steadily for a few seconds,
        //  - it's reasonably close. Earbuds in your ears are shadowed by your head and
        //    typically read -60 to -75 dBm, so the limit is deliberately loose.
        // This is a match by elimination, not proof, so the display says so. Proven
        // identity (exact address or a Windows match) always takes precedence.
        private BatteryReading InferSource(AuthorizedTarget authorized)
        {
            DateTime since, suppressed;
            var now = DateTime.UtcNow;
            if (!IsSupportedModel(authorized.Model) || !connectedSince.TryGetValue(authorized.ContainerId, out since) ||
                now - since < TimeSpan.FromSeconds(4)) return null;
            if (autoSuppressed.TryGetValue(authorized.ContainerId, out suppressed) && now < suppressed) return null;
            var sameModel = readings.Values.Where(r => r.Model == authorized.Model && now - r.Seen < TimeSpan.FromSeconds(20)).ToList();
            if (sameModel.Count != 1) return null;
            var only = sameModel[0];
            if (only.Rssi < InferRssi || now - only.FirstSeen < TimeSpan.FromSeconds(3)) return null;
            locks[authorized.ContainerId] = new SourceLock { Address = only.Address, TargetAddress = authorized.Address,
                Model = only.Model, UserSelected = false };
            return only;
        }

        // What nearby beacons of the saved model suggest, for connection messages only.
        internal NearbyHint Nearby(AudioTarget target)
        {
            if (target == null || !IsSupportedModel(target.ModelId)) return NearbyHint.Unknown;
            lock (gate)
            {
                if (disposed || watcher == null || watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started) return NearbyHint.Unknown;
                var own = ForTarget(target, false);
                var near = own != null ? new List<BatteryReading> { own } : readings.Values.Where(r => r.Model == target.ModelId &&
                    DateTime.UtcNow - r.Seen < TimeSpan.FromSeconds(15) && r.Rssi >= -75).ToList();
                if (near.Count == 0) return NearbyHint.NotSeen;
                return near.Any(r => r.Worn) ? NearbyHint.Worn : NearbyHint.Nearby;
            }
        }
        internal BatteryReading ForTarget(AudioTarget target) { return ForTarget(target, true); }

        // infer: allow creating an automatic lock. Hint-only callers pass false so a
        // query never changes what the battery display trusts.
        private BatteryReading ForTarget(AudioTarget target, bool infer)
        {
            if (target == null) return null;
            Guid container;
            if (!Guid.TryParse(target.ContainerId, out container) || container == Guid.Empty) return null;
            lock (gate)
            {
                AuthorizedTarget authorized;
                if (disposed || !targets.TryGetValue(container.ToString(), out authorized)) return null;
                var candidates = readings.Values.Where(r => r.Fresh && (authorized.Model == 0 || r.Model == authorized.Model))
                    .OrderByDescending(r => r.Seen).ToList();
                // A newer neighbor frame must never displace an exact identity.
                var exact = candidates.FirstOrDefault(r => authorized.Address != null && r.AddressLabel == authorized.Address);
                if (exact != null) return exact.WithSource("Paired AirPods Bluetooth address");
                foreach (var reading in candidates)
                {
                    ResolvedIdentity identity;
                    if (!identities.TryGetValue(reading.Address, out identity) || DateTime.UtcNow - identity.Seen > TimeSpan.FromMinutes(3)) continue;
                    if (DeviceDiscovery.SameGuid(authorized.ContainerId, identity.ContainerId))
                        return reading.WithSource("Windows matched AirPods device identity");
                }
                foreach (var reading in candidates)
                {
                    ResolvedIdentity identity;
                    if (!identities.TryGetValue(reading.Address, out identity) || DateTime.UtcNow - identity.Seen > TimeSpan.FromMinutes(3)) continue;
                    if (authorized.Address != null && identity.ClassicAddress == authorized.Address && authorized.Model == identity.Model && reading.Model == identity.Model)
                        return reading.WithSource("AirPods advertised paired device identity");
                }
                SourceLock held;
                BatteryReading chosen;
                if (locks.TryGetValue(container.ToString(), out held))
                {
                    if (readings.TryGetValue(held.Address, out chosen)) FollowRotation(held, chosen);
                    if (String.Equals(held.TargetAddress, authorized.Address, StringComparison.OrdinalIgnoreCase) &&
                        readings.TryGetValue(held.Address, out chosen) && chosen.Fresh && chosen.Model == held.Model &&
                        (authorized.Model == 0 || authorized.Model == chosen.Model))
                        return Locked(chosen, held.UserSelected);
                    // A lock survives brief gaps; drop it only once its broadcast has been
                    // gone long enough (3 min) to be unrecoverable.
                    if (!readings.ContainsKey(held.Address)) locks.Remove(container.ToString());
                    else return null;
                }
                var inferred = infer && authorized.Connected ? InferSource(authorized) : null;
                return inferred == null ? null : Locked(inferred, false);
            }
        }
        private static BatteryReading Locked(BatteryReading reading, bool userSelected)
        {
            var value = reading.WithSource(userSelected ? "User-selected BLE broadcast; device identity is not verified" :
                "Matched automatically: the only nearby broadcast of this model while your pair was connected");
            value.UserSelected = userSelected; value.Inferred = !userSelected;
            return value;
        }
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                foreach (var deviceWatcher in batteryWatchers.Values) StopWatcher(deviceWatcher);
                batteryWatchers.Clear();
            }
            if (watcher != null) { try { watcher.Stop(); } catch { } watcher.Received -= Received; }
        }
    }
}
