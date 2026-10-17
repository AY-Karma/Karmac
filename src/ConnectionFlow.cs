using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Karmac
{
    internal enum ConnectionStepState { Pending, Running, Complete, Failed, Skipped }

    internal sealed class ConnectionStep : INotifyPropertyChanged
    {
        public string Title { get; private set; }
        public string Detail { get; internal set; }
        public ConnectionStepState State { get; internal set; }
        internal long Started, Duration;
        internal bool Motion;
        internal Func<long> Now;
        internal ConnectionStep(string title, bool motion, Func<long> now) { Title = title; Motion = motion; Now = now; }
        public bool IsRunning { get { return State == ConnectionStepState.Running; } }
        public bool Pulse { get { return IsRunning && Motion; } }
        public string Color { get { return State == ConnectionStepState.Complete ? "#4ADE80" : State == ConnectionStepState.Failed ? "#F87171" : IsRunning ? "#F5F5F5" : "#505050"; } }
        public string TextColor { get { return State == ConnectionStepState.Failed ? "#F87171" : IsRunning ? "#F5F5F5" : State == ConnectionStepState.Pending ? "#707070" : "#C8C8C8"; } }
        public string Time { get {
            if (State == ConnectionStepState.Pending) return "—";
            if (State == ConnectionStepState.Skipped) return "Off";
            long milliseconds = IsRunning ? Now() - Started : Duration;
            return milliseconds < 1000 ? milliseconds + " ms" : (milliseconds / 1000.0).ToString("0.0") + " s";
        } }
        public string Accessible { get { return Title + ". " + State + ". " + Detail; } }
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Refresh()
        {
            var changed = PropertyChanged;
            if (changed != null) foreach (string name in new[] { "Detail", "State", "IsRunning", "Pulse", "Color", "TextColor", "Time", "Accessible" })
                changed(this, new PropertyChangedEventArgs(name));
        }
    }

    internal sealed class ConnectionTrace
    {
        private readonly Stopwatch clock = new Stopwatch();
        private readonly bool motion;
        private int current = -1;
        public ObservableCollection<ConnectionStep> Steps { get; private set; }
        public bool HasStarted { get { return Steps.Count > 0; } }
        public bool Running { get; private set; }
        public bool Failed { get; private set; }
        public string Title { get; private set; }
        public string Detail { get; private set; }
        public string Elapsed { get { return clock.Elapsed.TotalSeconds.ToString("0.0") + " s"; } }
        public event Action Changed;
        internal ConnectionTrace(bool animate) { motion = animate; Steps = new ObservableCollection<ConnectionStep>(); }
        internal void Begin()
        {
            clock.Restart(); Steps.Clear(); current = -1; Failed = false; Running = true;
            foreach (string title in new[] { "Bluetooth", "Your saved pair", "Audio link", "Windows output" })
                Steps.Add(new ConnectionStep(title, motion, () => clock.ElapsedMilliseconds));
            Advance(0, "Checking the adapter");
        }
        internal void Advance(int index, string detail)
        {
            if (current >= 0 && Steps[current].IsRunning) FinishCurrent(ConnectionStepState.Complete);
            current = index; Steps[index].State = ConnectionStepState.Running; Steps[index].Started = clock.ElapsedMilliseconds;
            Title = Steps[index].Title; Note(detail);
        }
        internal void Note(string detail)
        {
            Detail = detail; if (current >= 0) { Steps[current].Detail = detail; Steps[current].Refresh(); }
            if (Changed != null) Changed();
        }
        internal void Complete(bool routed)
        {
            FinishCurrent(routed ? ConnectionStepState.Complete : ConnectionStepState.Skipped);
            Running = false; clock.Stop(); Title = routed ? "Ready to listen" : "Headphones connected";
            Detail = routed ? "Audio output verified" : "Automatic output switching is off";
            if (Changed != null) Changed();
        }
        internal void Fail(string message)
        {
            FinishCurrent(ConnectionStepState.Failed); Failed = true; Running = false; clock.Stop();
            Title = "Connection needs attention"; Detail = message;
            if (Changed != null) Changed();
        }
        private void FinishCurrent(ConnectionStepState state)
        {
            if (current < 0) return;
            Steps[current].Duration = clock.ElapsedMilliseconds - Steps[current].Started;
            Steps[current].State = state; Steps[current].Refresh();
        }
        internal void Tick() { if (Running && current >= 0) Steps[current].Refresh(); }
        internal void Reset()
        {
            clock.Reset(); Steps.Clear(); current = -1; Running = false; Failed = false; Title = Detail = "";
            if (Changed != null) Changed();
        }
    }

    internal interface IConnectionPlatform
    {
        Task<bool> PowerOnAsync(Action starting);
        Task PowerOffAsync();
        Task DisconnectAsync(AudioTarget target);
        Task<string> ReadRadioStateAsync();
        Task VerifyPairAsync(AudioTarget target);
        Task RefreshAudioAsync(AudioTarget target);
        Task ReadAudioAsync(AudioTarget target);
        Task RequestAsync(AudioTarget target);
        Task RouteAsync(AudioTarget target);
        Task DelayAsync(int milliseconds);
    }

    internal sealed class WindowsConnectionPlatform : IConnectionPlatform
    {
        public Task<bool> PowerOnAsync(Action starting) { return BluetoothPower.EnsureOnAsync(starting); }
        public async Task PowerOffAsync() { await BluetoothPower.EnsureOffAsync(null); }
        public Task DisconnectAsync(AudioTarget target)
        {
            return Task.Run(() => {
                Guid container;
                if (target == null || !Guid.TryParse(target.ContainerId, out container) || container == Guid.Empty)
                    throw new InvalidOperationException("Select your paired headphones before disconnecting.");
                // Enumerate again so stale endpoint/controller hints cannot reach another device.
                // The native enumeration groups render and capture endpoints by exact physical container.
                var current = AudioService.Enumerate().FirstOrDefault(t => DeviceDiscovery.SameGuid(t.ContainerId, target.ContainerId));
                if (current == null)
                    throw new InvalidOperationException("Windows could not find the selected headphones' audio endpoints. Refresh devices and retry.");
                target.Endpoints = current.Endpoints;
                AudioService.RequestConnection(current, false);
            });
        }
        public async Task<string> ReadRadioStateAsync()
        {
            var radios = (await WinRtTask.Wait(Windows.Devices.Radios.Radio.GetRadiosAsync(), 4000))
                .Where(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth).ToList();
            return radios.Count == 0 ? "No adapter" : radios.Any(r => r.State == Windows.Devices.Radios.RadioState.On) ? "On" :
                radios.All(r => r.State == Windows.Devices.Radios.RadioState.Off || r.State == Windows.Devices.Radios.RadioState.Disabled) ? "Off" : "Unknown";
        }
        public Task VerifyPairAsync(AudioTarget target) { return DeviceDiscovery.VerifyPairAsync(target); }
        public Task RefreshAudioAsync(AudioTarget target)
        {
            return Task.Run(() => {
                var current = AudioService.Enumerate().FirstOrDefault(t => DeviceDiscovery.SameGuid(t.ContainerId, target.ContainerId));
                if (current != null) { target.Endpoints = current.Endpoints; target.IsDefault = current.IsDefault; }
            });
        }
        public Task ReadAudioAsync(AudioTarget target) { return Task.Run(() => { target.Endpoints = AudioService.ReadStates(target); }); }
        public Task RequestAsync(AudioTarget target) { return Task.Run(() => AudioService.RequestConnection(target, true)); }
        public Task RouteAsync(AudioTarget target) { return Task.Run(() => AudioService.Route(target)); }
        public Task DelayAsync(int milliseconds) { return Task.Delay(milliseconds); }
    }

    internal sealed class ConnectionFlow
    {
        private readonly IConnectionPlatform platform;
        private readonly int timeoutMs, retryMs, pollMs;
        internal ConnectionFlow(IConnectionPlatform backend, int timeout = 15000, int retry = 1000, int poll = 150)
        { platform = backend; timeoutMs = timeout; retryMs = retry; pollMs = poll; }

        internal async Task<bool> ConnectAsync(AudioTarget target, bool routeNew, bool explicitSwitch, ConnectionTrace trace)
        {
            trace.Begin();
            try
            {
                bool poweredOn = await platform.PowerOnAsync(() => trace.Note("Turning Bluetooth on"));
                trace.Note(poweredOn ? "Bluetooth is on" : "Bluetooth is already on");
                trace.Advance(1, "Confirming the saved Windows pairing");
                await platform.VerifyPairAsync(target);
                trace.Note("Your saved pair is confirmed");
                trace.Advance(2, "Restoring your audio connection");
                var wait = Stopwatch.StartNew();
                long nextRequest = 0, nextRefresh = 0;
                int attempts = 0, acceptedRequests = 0;
                bool alreadyConnected = false;
                while (true)
                {
                    if (wait.ElapsedMilliseconds >= nextRefresh)
                    { await platform.RefreshAudioAsync(target); nextRefresh = wait.ElapsedMilliseconds + retryMs; }
                    await platform.ReadAudioAsync(target);
                    if (target.Connected) { alreadyConnected = attempts == 0; break; }
                    if (wait.ElapsedMilliseconds >= timeoutMs)
                        throw new TimeoutException("Your headphones did not accept the connection. Keep them awake nearby. Another device or an active call may be holding the connection; Windows cannot guarantee a forced handoff.");
                    if (wait.ElapsedMilliseconds >= nextRequest && acceptedRequests < 4 &&
                        target.Endpoints.Any(e => AudioService.IsBluetoothController(e.ControllerId)))
                    {
                        attempts++;
                        trace.Note(attempts == 1 ? "Requesting the headphone audio link" : "Requesting the audio link again · attempt " + attempts);
                        Exception failure = null;
                        try
                        {
                            var request = platform.RequestAsync(target);
                            if (await Task.WhenAny(request, Task.Delay(12000)) != request)
                                trace.Note("Windows is still processing the audio request");
                            await request;
                            acceptedRequests++;
                        }
                        catch (Exception error) { failure = error; }
                        if (failure != null && !AudioService.IsTransientConnectionError(failure)) throw failure;
                        trace.Note(failure == null ? "Request sent · waiting for playback to become ready" : "Windows is restoring the audio driver · retrying");
                        nextRequest = wait.ElapsedMilliseconds + (failure == null ? retryMs : Math.Min(200, retryMs));
                        if (failure != null) nextRefresh = 0;
                    }
                    await platform.DelayAsync(pollMs);
                }
                trace.Note("Headphone playback is active");
                bool route = routeNew || (explicitSwitch && alreadyConnected);
                trace.Advance(3, route ? "Switching and verifying Windows audio output" : "Keeping your current output preference");
                if (route) { await platform.RouteAsync(target); target.IsDefault = true; }
                trace.Complete(route);
                return alreadyConnected;
            }
            catch (Exception error) { trace.Fail(error.Message); throw; }
        }
    }
}
