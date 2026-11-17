using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace Karmac
{
    // The baseband (ACL) link of one paired classic Bluetooth device. Lets the app tell
    // "unreachable" apart from "linked, profiles still loading", and page a sleeping device.
    internal sealed class BluetoothLink : IDisposable
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Task<BluetoothLink>> cache =
            new Dictionary<string, Task<BluetoothLink>>(StringComparer.OrdinalIgnoreCase);
        private readonly BluetoothDevice device;
        internal readonly string Id;
        internal event Action<bool> Changed;

        private BluetoothLink(string id, BluetoothDevice value)
        {
            Id = id; device = value;
            device.ConnectionStatusChanged += OnStatusChanged;
        }

        internal bool Connected { get { try { return device.ConnectionStatus == BluetoothConnectionStatus.Connected; } catch { return false; } } }

        // One shared instance per paired device. Returns null when Windows can't open it.
        internal static async Task<BluetoothLink> GetAsync(string bluetoothId, CancellationToken token = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(bluetoothId)) return null;
            Task<BluetoothLink> pending;
            lock (gate)
            {
                if (!cache.TryGetValue(bluetoothId, out pending) || pending.IsFaulted || pending.IsCanceled ||
                    (pending.IsCompleted && pending.Result == null))
                    cache[bluetoothId] = pending = OpenAsync(bluetoothId);
            }
            // Only wait cancellably while the device is still opening; an already-open
            // link returns at once without registering anything on the token.
            if (!pending.IsCompleted && token.CanBeCanceled)
            {
                await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, token));
                token.ThrowIfCancellationRequested();
            }
            try { return await pending; }
            catch (Exception error) { Log.Write("Bluetooth link: " + error.Message); return null; }
        }

        private static async Task<BluetoothLink> OpenAsync(string bluetoothId)
        {
            var device = await WinRtTask.Wait(BluetoothDevice.FromIdAsync(bluetoothId), 4000).ConfigureAwait(false);
            return device == null ? null : new BluetoothLink(bluetoothId, device);
        }

        // An uncached SDP query pages the device: it wakes sleeping earbuds and asks
        // ones held by a phone to come over. Its result is irrelevant; errors are expected.
        internal async Task PageAsync(CancellationToken token)
        {
            try
            {
                var result = await WinRtTask.Wait(device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached), 6000, token).ConfigureAwait(false);
                if (result != null) foreach (var service in result.Services) { try { service.Dispose(); } catch { } }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Log.Write("Bluetooth page: " + error.Message); }
        }

        private void OnStatusChanged(BluetoothDevice sender, object args)
        {
            var changed = Changed;
            if (changed != null) changed(Connected);
        }

        internal static void ForgetAll()
        {
            List<Task<BluetoothLink>> links;
            lock (gate) { links = cache.Values.ToList(); cache.Clear(); }
            foreach (var link in links)
                if (link.Status == TaskStatus.RanToCompletion && link.Result != null) link.Result.Dispose();
        }

        public void Dispose()
        {
            try { device.ConnectionStatusChanged -= OnStatusChanged; device.Dispose(); } catch { }
        }
    }

    // Pushes Windows audio, radio and link changes to the app instead of polling.
    // Events fire on background threads; subscribers marshal to the UI themselves.
    internal sealed class DeviceEvents : IDisposable
    {
        private sealed class EndpointClient : IMMNotificationClient
        {
            private readonly DeviceEvents owner;
            internal EndpointClient(DeviceEvents value) { owner = value; }
            public int OnDeviceStateChanged(string id, int state) { owner.Raise(owner.AudioChanged); return 0; }
            public int OnDeviceAdded(string id) { owner.Raise(owner.DevicesChanged); return 0; }
            public int OnDeviceRemoved(string id) { owner.Raise(owner.DevicesChanged); return 0; }
            public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == 0) owner.Raise(owner.AudioChanged); return 0; }
            public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
        }

        private IMMDeviceEnumerator enumerator;
        private EndpointClient client;
        private readonly List<Radio> radios = new List<Radio>();
        private BluetoothLink link;
        private bool disposed;
        internal event Action AudioChanged, DevicesChanged, RadioChanged, LinkChanged;

        internal void Start()
        {
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                client = new EndpointClient(this);
                AudioService.Check(enumerator.RegisterEndpointNotificationCallback(client));
            }
            catch (Exception error) { client = null; Log.Write("Audio notifications: " + error.Message); }
            WatchRadiosAsync();
        }

        private async void WatchRadiosAsync()
        {
            try
            {
                var all = await WinRtTask.Wait(Radio.GetRadiosAsync(), 4000);
                lock (radios)
                {
                    if (disposed) return;
                    foreach (var radio in all.Where(r => r.Kind == RadioKind.Bluetooth))
                    { radio.StateChanged += OnRadioChanged; radios.Add(radio); }
                }
            }
            catch (Exception error) { Log.Write("Radio notifications: " + error.Message); }
        }

        private void OnRadioChanged(Radio sender, object args) { Raise(RadioChanged); }

        // Follow the ACL link of the selected pair only. Passing null stops watching.
        // Call from the UI thread; a newer call always wins over a slower older one.
        internal async void WatchLink(string bluetoothId)
        {
            if (String.Equals(watching, bluetoothId, StringComparison.OrdinalIgnoreCase)) return;
            watching = bluetoothId;
            int version = ++watchVersion;
            if (link != null) { link.Changed -= OnLinkChanged; link = null; }
            if (bluetoothId == null) return;
            var opened = await BluetoothLink.GetAsync(bluetoothId);
            if (disposed || version != watchVersion) return;
            if (opened == null) { watching = null; return; } // allow a later retry
            link = opened; link.Changed += OnLinkChanged;
        }
        private string watching;
        private int watchVersion;

        private void OnLinkChanged(bool connected) { Raise(LinkChanged); }

        private void Raise(Action handler)
        {
            if (disposed || handler == null) return;
            try { handler(); } catch (Exception error) { Log.Write("Device event: " + error.Message); }
        }

        public void Dispose()
        {
            disposed = true;
            if (link != null) link.Changed -= OnLinkChanged;
            lock (radios) { foreach (var radio in radios) { try { radio.StateChanged -= OnRadioChanged; } catch { } } radios.Clear(); }
            if (enumerator != null)
            {
                try { if (client != null) enumerator.UnregisterEndpointNotificationCallback(client); } catch { }
                AudioService.Release(enumerator); enumerator = null;
            }
        }
    }
}
