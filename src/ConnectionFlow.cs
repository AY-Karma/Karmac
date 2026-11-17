using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        public bool Cancelled { get; private set; }
        public string Title { get; private set; }
        public string Detail { get; private set; }
        public string Elapsed { get { return clock.Elapsed.TotalSeconds.ToString("0.0") + " s"; } }
        public event Action Changed;
        internal ConnectionTrace(bool animate) { motion = animate; Steps = new ObservableCollection<ConnectionStep>(); }
        internal void Begin()
        {
            clock.Restart(); Steps.Clear(); current = -1; Failed = Cancelled = false; Running = true;
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
        // Updates the headline while a step runs, e.g. "Waking your AirPods".
        internal void Headline(string title, string detail)
        {
            if (Title == title && Detail == detail) return;
            Title = title; Note(detail);
        }
        internal void Note(string detail)
        {
            if (Detail == detail && current >= 0 && Steps[current].Detail == detail) return;
            Detail = detail; if (current >= 0) { Steps[current].Detail = detail; Steps[current].Refresh(); }
            if (Changed != null) Changed();
        }
        internal void Complete(bool routed, bool callsOnly)
        {
            FinishCurrent(routed ? ConnectionStepState.Complete : ConnectionStepState.Skipped);
            Running = false; clock.Stop();
            Title = callsOnly ? "Connected for calls only" : routed ? "Ready to listen" : "Headphones connected";
            Detail = callsOnly ? "Stereo audio didn't start, so Windows output was left unchanged" :
                routed ? "Audio output verified" : "Automatic output switching is off";
            if (Changed != null) Changed();
        }
        internal void Fail(string message) { Fail(message, -1); }

        // Marks the step that actually failed. A step that ran in parallel (the pairing
        // recheck) can fail after a later step started; that later step returns to pending.
        internal void Fail(string message, int step)
        {
            if (step >= 0 && step != current && current >= 0)
            {
                Steps[current].State = ConnectionStepState.Pending; Steps[current].Detail = null; Steps[current].Refresh();
                current = step;
            }
            FinishCurrent(ConnectionStepState.Failed); Failed = true; Running = false; clock.Stop();
            Title = "Connection needs attention"; Detail = message;
            if (Steps.Count > 0 && current >= 0) { Steps[current].Detail = message; Steps[current].Refresh(); }
            if (Changed != null) Changed();
        }
        internal void Cancel()
        {
            FinishCurrent(ConnectionStepState.Skipped); Cancelled = true; Running = false; clock.Stop();
            Title = "Connection cancelled"; Detail = "Stopped trying. A request already sent may still finish connecting them";
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
            clock.Reset(); Steps.Clear(); current = -1; Running = false; Failed = Cancelled = false; Title = Detail = "";
            if (Changed != null) Changed();
        }
    }

    // What nearby Apple beacons say about a pair of the saved model. A hint for messages
    // and deadlines only; it never authorizes anything.
    internal enum NearbyHint { Unknown, NotSeen, Nearby, Worn }

    internal sealed class ConnectResult
    {
        internal bool AlreadyConnected, CallsOnly, Routed;
        internal AudioService.OutputSnapshot PreviousOutput;
    }

    internal interface IConnectionPlatform
    {
        Task<bool> PowerOnAsync(Action starting);
        Task<string> ReadRadioStateAsync();
        Task VerifyPairAsync(AudioTarget target, CancellationToken token);
        Task RefreshAudioAsync(AudioTarget target);
        Task ReadAudioAsync(AudioTarget target);
        Task RequestAsync(AudioTarget target, bool connect);
        Task<bool?> ReadLinkAsync(AudioTarget target, CancellationToken token);
        Task PageAsync(AudioTarget target, CancellationToken token);
        NearbyHint Nearby(AudioTarget target);
        Task<AudioService.OutputSnapshot> RouteAsync(AudioTarget target);
        Task DelayAsync(int milliseconds, CancellationToken token);
    }

    internal sealed class WindowsConnectionPlatform : IConnectionPlatform
    {
        internal Func<AudioTarget, NearbyHint> NearbyProvider;
        public Task<bool> PowerOnAsync(Action starting) { return BluetoothPower.EnsureOnAsync(starting); }
        public Task<string> ReadRadioStateAsync() { return BluetoothPower.DescribeAsync(); }
        public Task VerifyPairAsync(AudioTarget target, CancellationToken token) { return DeviceDiscovery.VerifyPairAsync(target, token); }
        public Task RefreshAudioAsync(AudioTarget target)
        {
            return Task.Run(() => {
                var current = AudioService.Enumerate().FirstOrDefault(t => DeviceDiscovery.SameGuid(t.ContainerId, target.ContainerId));
                if (current != null) { target.Endpoints = current.Endpoints; target.IsDefault = current.IsDefault; }
            });
        }
        public Task ReadAudioAsync(AudioTarget target) { return Task.Run(() => { target.Endpoints = AudioService.ReadStates(target); }); }
        public Task RequestAsync(AudioTarget target, bool connect)
        {
            if (connect) return Task.Run(() => AudioService.RequestConnection(target, true));
            return Task.Run(() => {
                Guid container;
                if (target == null || !Guid.TryParse(target.ContainerId, out container) || container == Guid.Empty)
                    throw new InvalidOperationException("Select your paired headphones before disconnecting.");
                // Enumerate again so stale endpoint/controller hints cannot reach another device.
                var current = AudioService.Enumerate().FirstOrDefault(t => DeviceDiscovery.SameGuid(t.ContainerId, target.ContainerId));
                if (current == null)
                    throw new InvalidOperationException("Windows couldn't find this pair's audio endpoints. Refresh devices and try again.");
                target.Endpoints = current.Endpoints;
                AudioService.RequestConnection(current, false);
            });
        }
        public async Task<bool?> ReadLinkAsync(AudioTarget target, CancellationToken token)
        {
            var link = await BluetoothLink.GetAsync(target.BluetoothId, token);
            return link == null ? (bool?)null : link.Connected;
        }
        public async Task PageAsync(AudioTarget target, CancellationToken token)
        {
            var link = await BluetoothLink.GetAsync(target.BluetoothId, token);
            if (link != null) await link.PageAsync(token);
        }
        public NearbyHint Nearby(AudioTarget target) { return NearbyProvider == null ? NearbyHint.Unknown : NearbyProvider(target); }
        public Task<AudioService.OutputSnapshot> RouteAsync(AudioTarget target)
        {
            return Task.Run(() => {
                var previous = AudioService.CurrentOutput();
                AudioService.Route(target);
                return previous;
            });
        }
        public Task DelayAsync(int milliseconds, CancellationToken token) { return Task.Delay(milliseconds, token); }
    }

    internal sealed class ConnectionOptions
    {
        internal int TimeoutMs = 20000;          // normal hard deadline
        internal int TakeoverTimeoutMs = 30000;  // when another device holds the pair
        internal int PollMs = 60;
        // While the link is down, wake attempts alternate between a driver request and an
        // SDP page, one at a time, each given a full page window before the next.
        internal int LinkDownRetryMs = 5000;
        internal int LinkUpRetryMs = 1500;       // re-ask for audio profiles once linked
        internal int UnknownLinkRetryMs = 2000;  // link state unavailable: requests only
        internal int StereoGraceMs = 6000;       // calls-only grace before settling for it
        internal int SlowRequestLogMs = 8000;    // log a driver call still running after this
    }

    internal sealed class ConnectionFlow
    {
        private readonly IConnectionPlatform platform;
        private readonly ConnectionOptions options;
        internal ConnectionFlow(IConnectionPlatform backend, ConnectionOptions settings = null)
        { platform = backend; options = settings ?? new ConnectionOptions(); }

        private static bool HasController(AudioTarget target)
        {
            return target.Endpoints.Any(e => AudioService.IsBluetoothController(e.ControllerId));
        }

        internal async Task<ConnectResult> ConnectAsync(AudioTarget target, bool routeNew, bool explicitSwitch,
            ConnectionTrace trace, CancellationToken cancel)
        {
            string term = AirPodsModels.Term(target.ModelId);
            Task verify = null;
            trace.Begin();
            try
            {
                bool poweredOn = await platform.PowerOnAsync(() => trace.Note("Turning Bluetooth on"));
                cancel.ThrowIfCancellationRequested();
                trace.Note(poweredOn ? "Bluetooth is on" : "Bluetooth is already on");

                trace.Advance(1, "Confirming the saved Windows pairing");
                // A pair confirmed by this session's discovery is rechecked alongside the
                // endpoint and link reads. No driver request goes out until it passes.
                bool confirmedEarlier = !target.RequiresPairingCheck;
                verify = platform.VerifyPairAsync(target, cancel);
                if (!confirmedEarlier) { await verify; trace.Note("Your saved pair is confirmed"); }
                else trace.Note("Confirmed earlier · rechecking alongside the connection");

                trace.Advance(2, "Checking your " + term);
                var result = await LinkAsync(target, term, trace, verify, cancel);

                await verify;
                bool route = !result.CallsOnly && (routeNew || (explicitSwitch && result.AlreadyConnected));
                trace.Advance(3, result.CallsOnly ? "Keeping your current output · stereo audio isn't active" :
                    route ? "Switching and verifying Windows audio output" : "Keeping your current output preference");
                if (route)
                {
                    result.PreviousOutput = await platform.RouteAsync(target);
                    target.IsDefault = true; result.Routed = true;
                }
                trace.Complete(route, result.CallsOnly);
                return result;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException && cancel.IsCancellationRequested) trace.Cancel();
                // A failed pairing recheck belongs to "Your saved pair", even if it
                // surfaced while a later step was running.
                else if (verify != null && verify.IsFaulted) trace.Fail(error.Message, 1);
                else trace.Fail(error.Message);
                throw;
            }
        }

        private async Task<ConnectResult> LinkAsync(AudioTarget target, string term, ConnectionTrace trace, Task verify, CancellationToken cancel)
        {
            var clock = Stopwatch.StartNew();
            long budget = options.TimeoutMs, nextAction = 0, nextRefresh = 0;
            long callsOnlySince = -1, requestStarted = 0;
            int requests = 0;
            bool takeover = false, linkEverUp = false, slowNoted = false, pageNext = false;
            Task<Exception> pending = null, page = null;
            NearbyHint hint = NearbyHint.Unknown;

            if (!HasController(target)) await platform.RefreshAudioAsync(target);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                // A full endpoint enumeration is only needed until the driver exposes
                // this pair's Bluetooth controller (e.g. right after power-on).
                if (!HasController(target))
                {
                    if (clock.ElapsedMilliseconds >= nextRefresh)
                    { await platform.RefreshAudioAsync(target); nextRefresh = clock.ElapsedMilliseconds + 1000; }
                }
                else await platform.ReadAudioAsync(target);

                if (target.PlaybackReady)
                {
                    trace.Note(term + " audio is active");
                    return new ConnectResult { AlreadyConnected = requests == 0 };
                }

                bool? link = await platform.ReadLinkAsync(target, cancel);
                if (link == true) linkEverUp = true;
                hint = platform.Nearby(target);
                if (!takeover && link != true && hint == NearbyHint.Worn && requests > 0)
                { takeover = true; budget = Math.Max(budget, options.TakeoverTimeoutMs); }

                if (target.CallsOnly)
                {
                    if (callsOnlySince < 0) callsOnlySince = clock.ElapsedMilliseconds;
                    else if (clock.ElapsedMilliseconds - callsOnlySince >= options.StereoGraceMs)
                    {
                        trace.Note("Only the call profile connected");
                        return new ConnectResult { AlreadyConnected = requests == 0, CallsOnly = true };
                    }
                }
                else callsOnlySince = -1;

                if (clock.ElapsedMilliseconds >= budget)
                {
                    // Out of time, but calls already work: report that rather than a failure.
                    if (target.CallsOnly) return new ConnectResult { AlreadyConnected = requests == 0, CallsOnly = true };
                    throw new TimeoutException(FailureMessage(term, linkEverUp, takeover, hint));
                }

                // Harvest a finished driver request or page without ever blocking on it.
                if (pending != null && pending.IsCompleted)
                {
                    var failure = pending.Result; pending = null;
                    if (failure != null && !AudioService.IsTransientConnectionError(failure)) throw failure;
                    if (failure != null) { nextAction = clock.ElapsedMilliseconds + 200; nextRefresh = 0; pageNext = false; }
                }
                else if (pending != null && !slowNoted && clock.ElapsedMilliseconds - requestStarted > options.SlowRequestLogMs)
                { slowNoted = true; Log.Write("Connect: driver request still running after " + options.SlowRequestLogMs + " ms"); }
                if (page != null && page.IsCompleted) page = null;

                // One wake attempt in flight at a time. A removed bond never gets a request.
                if (verify.IsFaulted || verify.IsCanceled) await verify;
                bool idle = pending == null && page == null;
                if (idle && verify.Status == TaskStatus.RanToCompletion && clock.ElapsedMilliseconds >= nextAction && HasController(target))
                {
                    if (link == false && pageNext)
                        page = Capture(() => platform.PageAsync(target, cancel));
                    else
                    {
                        requests++; requestStarted = clock.ElapsedMilliseconds;
                        pending = Capture(() => platform.RequestAsync(target, true));
                    }
                    pageNext = link == false && !pageNext;
                    nextAction = clock.ElapsedMilliseconds + (link == true ? options.LinkUpRetryMs :
                        link == false ? options.LinkDownRetryMs : options.UnknownLinkRetryMs);
                }

                Describe(trace, term, link, takeover, hint, target, requests);
                await platform.DelayAsync(options.PollMs, cancel);
            }
        }

        private static void Describe(ConnectionTrace trace, string term, bool? link, bool takeover, NearbyHint hint, AudioTarget target, int requests)
        {
            if (target.CallsOnly)
                trace.Headline("Loading stereo audio", "Calls are ready · waiting for music audio");
            else if (link == true)
                trace.Headline("Linked · loading audio", "Your " + term + " are linked · Windows is starting their audio");
            else if (takeover)
                trace.Headline("Taking your " + term + " over", "They're in use on another device · asking them to switch to this PC");
            else if (requests == 0)
                trace.Headline("Audio link", "Requesting the audio link");
            else if (hint == NearbyHint.NotSeen)
                trace.Headline("Looking for your " + term, "Open the case or put them in, near this PC");
            else
                trace.Headline("Waking your " + term, "Paging them · this can take a few seconds");
        }

        private static string FailureMessage(string term, bool linkEverUp, bool takeover, NearbyHint hint)
        {
            if (linkEverUp)
                return "Your " + term + " linked, but Windows didn't finish loading their audio. Try again; if it keeps happening, remove and re-pair them in Windows Settings.";
            if (takeover)
                return "Your " + term + " stayed with another device. If a call is active there, end it first. On iPhone, set Settings › Bluetooth › your " + term +
                    " › Connect to This iPhone › When Last Connected to This iPhone so it doesn't take them back.";
            if (hint == NearbyHint.NotSeen)
                return "Your " + term + " didn't answer. Make sure they're charged and near this PC: open the case or put them in, then try again.";
            return "Your " + term + " didn't answer. Close and reopen the case, or take them out, then try again. Another device may also be holding them.";
        }

        // Runs an operation in the background and reports its failure as a value, so the
        // loop never blocks on it. Synchronous throws are captured the same way.
        private static async Task<Exception> Capture(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); return null; }
            catch (Exception error) { return error; }
        }

        // Disconnects only this pair, then confirms both the audio profiles and the
        // Bluetooth link dropped. Returns false when audio stopped but the link lingers.
        internal async Task<bool> DisconnectAsync(AudioTarget target, CancellationToken cancel)
        {
            await platform.RequestAsync(target, false);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                await platform.ReadAudioAsync(target);
                if (!target.Endpoints.Any(e => e.Active)) break;
                if (clock.ElapsedMilliseconds >= 8000)
                    throw new InvalidOperationException("Windows still reports an active audio profile for this pair. Bluetooth was left on.");
                await platform.DelayAsync(options.PollMs, cancel);
            }
            target.IsDefault = false;
            bool resent = false;
            clock.Restart();
            while (true)
            {
                bool? link = await platform.ReadLinkAsync(target, cancel);
                if (link != true) return true;
                if (!resent && clock.ElapsedMilliseconds >= 2500)
                {
                    resent = true;
                    try { await platform.RequestAsync(target, false); }
                    catch (Exception error) { Log.Write("Disconnect retry: " + error.Message); }
                }
                if (clock.ElapsedMilliseconds >= 6000) return false;
                await platform.DelayAsync(250, cancel);
            }
        }
    }
}
