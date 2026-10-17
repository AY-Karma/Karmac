using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace Karmac.Tests
{
    // Dependency-free regression tests. All BLE packets below are synthetic,
    // constructed from the documented decoder layout, not captured hardware data.
    internal static class Program
    {
        private static int passed;
        private static int failed;
        private static int assertions;

        private static int Main(string[] args)
        {
            if (args.Any(a => a != "--integration"))
            {
                Console.Error.WriteLine("Usage: Karmac.Tests.exe [--integration]");
                return 2;
            }

            Run("Synthetic normal-side battery, charging and in-ear mapping", NormalMapping);
            Run("Synthetic swapped-side battery, charging and in-ear mapping", SwappedMapping);
            Run("Synthetic opposite-side flags remain independent", OppositeSideFlags);
            Run("Supported model identifiers use little-endian decoding", SupportedModels);
            Run("Unknown models never become battery readings", UnknownModels);
            Run("Null, empty and unrelated advertisements provide no reading", MissingData);
            Run("Every truncated synthetic packet is rejected", Truncation);
            Run("Wrong TLV lengths and malformed prefixes are rejected", MalformedLengths);
            Run("Unrelated and empty TLVs can precede an AirPods TLV", TlvTraversal);
            Run("Battery nibbles preserve zero, full and unknown states", BatteryNibbles);
            Run("All-unknown batteries provide no reading", AllUnknownBatteries);
            Run("Classic Bluetooth addresses normalize consistently", AddressNormalization);
            Run("Already-on Bluetooth does not request access or cycle adapters", BluetoothAlreadyOn);
            Run("Bluetooth power-on waits for the adapter before continuing", BluetoothEnable);
            Run("Bluetooth access denial cannot reach the connection step", BluetoothAccessDenied);
            Run("Missing or disabled Bluetooth cannot request a radio change", BluetoothUnavailable);
            Run("Bluetooth adapter refusal and transition timeout are reported", BluetoothEnableFailure);
            Run("Already-off Bluetooth does not request access or write radio state", BluetoothAlreadyOff);
            Run("Bluetooth power-off disables every active adapter before completion", BluetoothDisableAll);
            Run("Bluetooth power-off access denial cannot report success", BluetoothDisableAccessDenied);
            Run("Bluetooth power-off reports adapter refusal and continues other adapters", BluetoothDisableRefusal);
            Run("Bluetooth power-off timeout and unknown state cannot report success", BluetoothDisableFailure);
            Run("Restoring audio driver errors remain distinguishable from permanent failures", TransientBluetoothDriverErrors);
            Run("Saved pair survives serialization as an unverified cold-start hint", SavedPairCache);
            Run("Disconnect preferences preserve the default for existing settings and explicit user choices", DisconnectPreferences);
            Run("Karmac settings preserve legacy preferences without overriding new settings", RenamedPreferences);
            Run("Cold connection restores endpoints and retries accepted requests until playback is active", ColdConnectionFlow);
            Run("Restoring-driver failures do not exhaust actual reconnect attempts", RestoringDriverBudget);
            Run("Already-active playback skips reconnect and verifies output", ActiveConnectionFlow);
            Run("Unconfirmed pairing stops all audio requests and routes", UnpairedConnectionFlow);
            Run("A refused handoff never reports connected or routes audio", RefusedConnectionFlow);
            Run("Route errors preserve actual connection and mark only the output stage failed", FailedRouteFlow);
            Run("Malformed and zero Bluetooth addresses never become identities", MalformedAddresses);
            Run("Apple audio identity requires a known product or confirmed Bluetooth audio endpoints", AirPodsHardwareIdentity);
            Run("Verified AirPods product IDs select the correct name and artwork family", ModelIdentity);
            Run("Apple wireless catalog models keep identity, layout and decoder support separate", AppleAudioCatalog);
            Run("Unsupported product IDs retain generic identity without name guessing", UnknownModelIdentity);
            Run("A nearby neighbor is never implicitly assigned to a target", NeighborIdentity);
            Run("Exact paired identity takes precedence over resolved beacons", ExplicitIdentity);
            Run("Short identity frames decode MAC and model without inventing battery", ShortIdentityPackets);
            Run("Same-model proximity ambiguity never guesses ownership", AmbiguousNeighbors);
            Run("Automatic identity requires its authorized container and model", AuthorizedIdentity);
            Run("Rotating BLE addresses require their own current identity proof", RotatedIdentity);
            Run("Device battery readings retain container scope and freshness", DeviceBatteryScope);
            Run("Manual battery source requires explicit authorized selection", ManualBatterySource);
            Run("Verified battery identity takes precedence over a manual source", VerifiedBeforeManual);
            Run("Manual battery source expires without rebinding rotated addresses", ManualSourceExpiry);
            Run("Stale readings cannot match identity or appear nearby", StaleReadings);
            Run("Nearby candidates enforce the RSSI cutoff and sort by signal", NearbyCandidates);
            Run("Empty caches and missing targets remain unknown", EmptyCache);
            Run("Windows native structure sizes and field offsets match x64 ABI", NativeAbi);
            Run("Bluetooth audio controllers require the native path prefix", BluetoothControllerIds);
            Run("A capture-only endpoint cannot imply playback is connected", PlaybackConnectionState);

            if (args.Contains("--integration"))
                Run("Read-only Windows device discovery", IntegrationDiscovery);

            Console.WriteLine("{0} passed, {1} failed; {2} assertions. BLE vectors are synthetic.", passed, failed, assertions);
            return failed == 0 ? 0 : 1;
        }

        private static void RenamedPreferences()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Karmac-preferences-" + Guid.NewGuid().ToString("N"));
            string current = System.IO.Path.Combine(root, "Karmac");
            string legacy = System.IO.Path.Combine(root, "OpenConnect");
            System.IO.Directory.CreateDirectory(current);
            System.IO.Directory.CreateDirectory(legacy);
            string legacyPath = System.IO.Path.Combine(legacy, "settings.json");
            const string oldSettings = "{\"Hotkey\":false,\"PowerOffBluetoothOnDisconnect\":false}";
            try
            {
                System.IO.File.WriteAllText(legacyPath, oldSettings);
                var restored = Preferences.LoadFromFolders(current, legacy);
                Assert(!restored.Hotkey && !restored.PowerOffBluetoothOnDisconnect, "Legacy choices survive the rename");
                Assert(!System.IO.File.Exists(System.IO.Path.Combine(current, "settings.json")), "Reading does not create or overwrite settings");
                System.IO.File.WriteAllText(System.IO.Path.Combine(current, "settings.json"), "{\"Hotkey\":true}");
                Assert(Preferences.LoadFromFolders(current, legacy).Hotkey, "Karmac settings take precedence");
                Equal(oldSettings, System.IO.File.ReadAllText(legacyPath), "Legacy file remains unchanged");
            }
            finally { System.IO.Directory.Delete(root, true); }
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + error.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            Assert(EqualityComparer<T>.Default.Equals(expected, actual),
                message + " (expected " + expected + ", got " + actual + ")");
        }

        private static BatteryReading Read(byte[] packet)
        {
            BatteryReading reading = BatteryMonitor.Parse(packet);
            Assert(reading != null, "Expected a recognized battery packet");
            return reading;
        }

        private static byte[] Packet(ushort model, byte state, byte battery, byte charge)
        {
            // Apple manufacturer bytes 4C 00 are excluded by the WinRT data API.
            // TLV: type 07, length 19; payload starts at index 2.
            byte[] packet = new byte[27];
            packet[0] = 0x07;
            packet[1] = 0x19;
            packet[2] = 0x01;
            packet[3] = (byte)(model & 0xFF);
            packet[4] = (byte)(model >> 8);
            packet[5] = state;
            packet[6] = battery;
            packet[7] = charge;
            return packet;
        }

        private sealed class FakeRadio : IBluetoothRadio
        {
            public RadioState Current;
            public RadioAccessStatus Result = RadioAccessStatus.Allowed;
            public bool CompleteTransition = true;
            public int Calls;
            public int OffCalls;
            public RadioState State { get { return Current; } }
            public async Task<RadioAccessStatus> TurnOnAsync()
            {
                Calls++;
                if (Result == RadioAccessStatus.Allowed && CompleteTransition)
                { await Task.Delay(10); Current = RadioState.On; }
                return Result;
            }
            public async Task<RadioAccessStatus> TurnOffAsync()
            {
                OffCalls++;
                if (Result == RadioAccessStatus.Allowed && CompleteTransition)
                { await Task.Delay(10); Current = RadioState.Off; }
                return Result;
            }
        }
        private static void BluetoothAlreadyOn()
        {
            var off = new FakeRadio { Current = RadioState.Off };
            var on = new FakeRadio { Current = RadioState.On };
            int access = 0;
            bool changed = BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { off, on },
                () => { access++; return Task.FromResult(RadioAccessStatus.Allowed); }, null, 200).GetAwaiter().GetResult();
            Assert(!changed && access == 0 && off.Calls == 0 && on.Calls == 0,
                "An enabled Bluetooth radio skips permission and state writes");
        }
        private static void BluetoothEnable()
        {
            var radio = new FakeRadio { Current = RadioState.Off };
            bool starting = false;
            int access = 0;
            bool changed = BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { radio },
                () => { Assert(starting && radio.Calls == 0, "Progress and access precede the state change"); access++; return Task.FromResult(RadioAccessStatus.Allowed); },
                () => starting = true, 200).GetAwaiter().GetResult();
            Assert(changed && radio.State == RadioState.On && access == 1 && radio.Calls == 1,
                "Power-on completes with the adapter ready for connection");
        }
        private static void BluetoothAccessDenied()
        {
            foreach (var status in new[] { RadioAccessStatus.DeniedByUser, RadioAccessStatus.DeniedBySystem, RadioAccessStatus.Unspecified })
            {
                var radio = new FakeRadio { Current = RadioState.Off };
                bool continued = false, failed = false;
                try
                {
                    BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { radio }, () => Task.FromResult(status), null, 0).GetAwaiter().GetResult();
                    continued = true;
                }
                catch (InvalidOperationException error) { failed = error.Message.Contains(status.ToString()); }
                Assert(failed && !continued && radio.Calls == 0, "Denied radio access stops before connection: " + status);
            }
        }
        private static void BluetoothUnavailable()
        {
            foreach (var radios in new[] { new IBluetoothRadio[0], new IBluetoothRadio[] { new FakeRadio { Current = RadioState.Disabled } } })
            {
                bool failed = false;
                int access = 0;
                try { BluetoothPower.EnsureOnAsync(radios, () => { access++; return Task.FromResult(RadioAccessStatus.Allowed); }, null, 0).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { failed = true; }
                Assert(failed && access == 0, "Unavailable hardware fails without requesting access");
            }
        }
        private static void BluetoothEnableFailure()
        {
            var denied = new FakeRadio { Current = RadioState.Off, Result = RadioAccessStatus.DeniedBySystem };
            bool failed = false;
            try { BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { denied }, () => Task.FromResult(RadioAccessStatus.Allowed), null, 0).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { failed = error.Message.Contains("DeniedBySystem"); }
            Assert(failed && denied.Calls == 1, "Set-state access refusal is reported");
            var stalled = new FakeRadio { Current = RadioState.Off, CompleteTransition = false };
            failed = false;
            try { BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { stalled }, () => Task.FromResult(RadioAccessStatus.Allowed), null, 0).GetAwaiter().GetResult(); }
            catch (TimeoutException) { failed = true; }
            Assert(failed && stalled.State == RadioState.Off, "Success is never claimed before radio state is on");
            var fallback = new FakeRadio { Current = RadioState.Off };
            Assert(BluetoothPower.EnsureOnAsync(new IBluetoothRadio[] { denied, fallback }, () => Task.FromResult(RadioAccessStatus.Allowed), null, 200).GetAwaiter().GetResult()
                && fallback.State == RadioState.On, "Another Bluetooth adapter can succeed if the first refuses");
        }

        private static void BluetoothAlreadyOff()
        {
            foreach (var radios in new[] { new IBluetoothRadio[0], new IBluetoothRadio[] {
                new FakeRadio { Current = RadioState.Off }, new FakeRadio { Current = RadioState.Disabled } } })
            {
                int access = 0;
                bool changed = BluetoothPower.EnsureOffAsync(radios,
                    () => { access++; return Task.FromResult(RadioAccessStatus.Allowed); },
                    () => { throw new InvalidOperationException("No transition should start"); }, 200).GetAwaiter().GetResult();
                Assert(!changed && access == 0, "No adapter already off requires permission or a transition");
                Assert(radios.Cast<FakeRadio>().All(r => r.OffCalls == 0 && r.Calls == 0), "Already-off adapters are untouched");
            }
        }

        private static void BluetoothDisableAll()
        {
            var first = new FakeRadio { Current = RadioState.On };
            var second = new FakeRadio { Current = RadioState.On };
            var off = new FakeRadio { Current = RadioState.Off };
            bool starting = false;
            int access = 0;
            bool changed = BluetoothPower.EnsureOffAsync(new IBluetoothRadio[] { first, off, second },
                () => { Assert(starting && first.OffCalls == 0 && second.OffCalls == 0, "Progress and access precede all power-off requests"); access++; return Task.FromResult(RadioAccessStatus.Allowed); },
                () => starting = true, 200).GetAwaiter().GetResult();
            Assert(changed && access == 1, "One access check covers all Bluetooth adapters");
            Assert(first.Current == RadioState.Off && second.Current == RadioState.Off,
                "Success confirms every initially active adapter is off");
            Assert(first.OffCalls == 1 && second.OffCalls == 1 && off.OffCalls == 0,
                "Every active adapter is disabled once; already-off adapters are untouched");
            Assert(first.Calls == 0 && second.Calls == 0 && off.Calls == 0, "Disconnect never turns an adapter on");
        }

        private static void BluetoothDisableAccessDenied()
        {
            foreach (var status in new[] { RadioAccessStatus.DeniedByUser, RadioAccessStatus.DeniedBySystem, RadioAccessStatus.Unspecified })
            {
                var radio = new FakeRadio { Current = RadioState.On };
                bool failed = false;
                try { BluetoothPower.EnsureOffAsync(new IBluetoothRadio[] { radio }, () => Task.FromResult(status), null, 0).GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { failed = error.Message.Contains(status.ToString()); }
                Assert(failed && radio.OffCalls == 0 && radio.Current == RadioState.On,
                    "Denied radio access cannot claim Bluetooth was disabled: " + status);
            }
        }

        private static void BluetoothDisableRefusal()
        {
            var denied = new FakeRadio { Current = RadioState.On, Result = RadioAccessStatus.DeniedBySystem };
            var allowed = new FakeRadio { Current = RadioState.On };
            bool failed = false;
            try { BluetoothPower.EnsureOffAsync(new IBluetoothRadio[] { denied, allowed },
                () => Task.FromResult(RadioAccessStatus.Allowed), null, 200).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { failed = error.Message.Contains("DeniedBySystem"); }
            Assert(failed && denied.OffCalls == 1 && denied.Current == RadioState.On,
                "One refused adapter prevents a false laptop-wide off result");
            Assert(allowed.OffCalls == 1 && allowed.Current == RadioState.Off,
                "A refused adapter does not stop disabling the remaining adapters");
        }

        private static void BluetoothDisableFailure()
        {
            var stalled = new FakeRadio { Current = RadioState.On, CompleteTransition = false };
            bool failed = false;
            try { BluetoothPower.EnsureOffAsync(new IBluetoothRadio[] { stalled },
                () => Task.FromResult(RadioAccessStatus.Allowed), null, 0).GetAwaiter().GetResult(); }
            catch (TimeoutException) { failed = true; }
            Assert(failed && stalled.OffCalls == 1 && stalled.Current == RadioState.On,
                "An accepted request without an off transition cannot report success");
            var unknown = new FakeRadio { Current = RadioState.Unknown };
            failed = false;
            try { BluetoothPower.EnsureOffAsync(new IBluetoothRadio[] { unknown },
                () => { throw new InvalidOperationException("No active adapter needs a write"); }, null, 0).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { failed = error.Message.Contains("confirm"); }
            Assert(failed && unknown.OffCalls == 0, "An unknown adapter state cannot be reported as off");
        }

        private static void TransientBluetoothDriverErrors()
        {
            foreach (uint code in new uint[] { 0x80070003, 0x80070002, 0x80070490 })
                Assert(AudioService.IsTransientConnectionError(new InvalidOperationException("Driver not ready",
                    new COMException("Native driver", unchecked((int)code)))), "Native restoration error survives wrapping");
            Assert(!AudioService.IsTransientConnectionError(new COMException("Access denied", unchecked((int)0x80070005))),
                "An access denial is not a restoring-driver error");
            Assert(!AudioService.IsTransientConnectionError(null), "Missing errors do not trigger a retry");
        }

        private sealed class FakeConnectionPlatform : IConnectionPlatform
        {
            internal int Requests, Refreshes, Routes;
            internal int ConnectOnRequest = 2, EndpointOnRefresh = 2;
            internal bool Verified, RejectPair, FailRoute, AlreadyActive, TransientFirst;
            internal int TransientFailures;
            internal bool PowerChanged = true;
            internal readonly List<string> Calls = new List<string>();
            public Task<bool> PowerOnAsync(Action starting) { Calls.Add("power"); if (PowerChanged) starting(); return Task.FromResult(PowerChanged); }
            public Task PowerOffAsync() { return Task.FromResult(true); }
            public Task DisconnectAsync(AudioTarget target) { foreach (var endpoint in target.Endpoints) endpoint.State = 8; return Task.FromResult(true); }
            public Task<string> ReadRadioStateAsync() { return Task.FromResult("On"); }
            public Task VerifyPairAsync(AudioTarget target)
            {
                Calls.Add("verify");
                if (RejectPair) throw new InvalidOperationException("Pair removed");
                Verified = true; target.RequiresPairingCheck = false; return Task.FromResult(true);
            }
            public Task RefreshAudioAsync(AudioTarget target)
            {
                Assert(Verified, "Audio discovery follows pairing verification");
                Refreshes++; Calls.Add("refresh");
                if (Refreshes >= EndpointOnRefresh)
                    target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { ControllerId = @"{2}.\\?\bth#saved", Flow = 0,
                        State = AlreadyActive || Requests >= ConnectOnRequest ? 1 : 8 } };
                return Task.FromResult(true);
            }
            public Task ReadAudioAsync(AudioTarget target)
            {
                foreach (var endpoint in target.Endpoints) endpoint.State = AlreadyActive || Requests >= ConnectOnRequest ? 1 : 8;
                return Task.FromResult(true);
            }
            public Task RequestAsync(AudioTarget target)
            {
                Assert(Verified && target.Endpoints.Count > 0, "Only the verified saved pair receives audio requests");
                Requests++; Calls.Add("request");
                if ((TransientFirst && Requests == 1) || Requests <= TransientFailures)
                    throw new InvalidOperationException("Restoring", new COMException("Path missing", unchecked((int)0x80070003)));
                return Task.FromResult(true);
            }
            public Task RouteAsync(AudioTarget target)
            {
                Assert(target.Connected, "Output is never routed before playback is active");
                Calls.Add("route"); Routes++; if (FailRoute) throw new InvalidOperationException("Output denied");
                return Task.FromResult(true);
            }
            public Task DelayAsync(int milliseconds) { return Task.Delay(milliseconds); }
        }
        private static AudioTarget SavedFixture()
        {
            return new AudioTarget { ContainerId = "f0010000-0000-4000-8000-000000000001", Name = "My Pro 3", Address = "001122334455", ModelId = 0x2027 };
        }
        private static void DisconnectPreferences()
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            Assert(new Preferences().PowerOffBluetoothOnDisconnect, "New installations keep laptop-wide Bluetooth power-off as the default");
            var legacy = serializer.Deserialize<Preferences>("{\"RouteAudio\":false,\"CloseToTray\":true,\"Hotkey\":false}");
            Assert(legacy.PowerOffBluetoothOnDisconnect, "Settings saved before the disconnect option retain laptop-wide power-off");
            Assert(!legacy.RouteAudio && !legacy.Hotkey && legacy.CloseToTray, "Migrating the disconnect default preserves other settings");
            foreach (bool choice in new[] { false, true })
            {
                var saved = new Preferences { PowerOffBluetoothOnDisconnect = choice };
                var restored = serializer.Deserialize<Preferences>(serializer.Serialize(saved));
                Equal(choice, restored.PowerOffBluetoothOnDisconnect, "The user's disconnect behavior survives a settings round trip");
            }
        }
        private static void SavedPairCache()
        {
            var target = SavedFixture();
            target.BluetoothId = "paired-test-id";
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var cache = serializer.Deserialize<SavedAirPods>(serializer.Serialize(SavedAirPods.FromTarget(target)));
            var hint = cache.Restore(target.ContainerId);
            Assert(hint != null && hint.RequiresPairingCheck && hint.Endpoints.Count == 0 && !hint.Connected,
                "Persisted metadata cannot become a claimed live or paired connection");
            Assert(hint.Name == target.Name && hint.Address == target.Address && hint.BluetoothId == target.BluetoothId && hint.ModelId == target.ModelId,
                "Cold-start identity retains the selected name, classic address, device ID and model");
            Assert(cache.Restore(Guid.NewGuid().ToString()) == null, "Another favorite is never replaced with the cached pair");
            Assert(SavedAirPods.FromTarget(hint) == null, "Unverified hints cannot authorize another persistent cache");
        }
        private static void ColdConnectionFlow()
        {
            foreach (bool transient in new[] { false, true })
            {
                var backend = new FakeConnectionPlatform { TransientFirst = transient };
                var target = SavedFixture(); target.RequiresPairingCheck = true;
                var trace = new ConnectionTrace(false);
                bool already = new ConnectionFlow(backend, 1500, 5, 1).ConnectAsync(target, true, false, trace).GetAwaiter().GetResult();
                Assert(!already && target.Connected && target.IsDefault && backend.Requests == 2 && backend.Refreshes >= 2,
                    "Cold endpoint absence and an accepted-but-inactive first request recover in one action");
                Assert(backend.Calls[0] == "power" && backend.Calls[1] == "verify" && backend.Routes == 1,
                    "Radio and exact pairing are confirmed before audio connection and route");
                Assert(trace.Steps.All(s => s.State == ConnectionStepState.Complete) && !trace.Running && !trace.Failed,
                    "Every completed stage is based on its verified operation");
            }
        }
        private static void ActiveConnectionFlow()
        {
            var backend = new FakeConnectionPlatform { AlreadyActive = true, EndpointOnRefresh = 1, PowerChanged = false };
            var target = SavedFixture();
            var trace = new ConnectionTrace(false);
            Assert(new ConnectionFlow(backend).ConnectAsync(target, false, true, trace).GetAwaiter().GetResult() && backend.Requests == 0 && backend.Routes == 1,
                "Explicit output switch verifies routing without issuing another reconnect");
            backend = new FakeConnectionPlatform { AlreadyActive = true, EndpointOnRefresh = 1 };
            target = SavedFixture(); trace = new ConnectionTrace(false);
            new ConnectionFlow(backend).ConnectAsync(target, false, false, trace).GetAwaiter().GetResult();
            Assert(backend.Routes == 0 && trace.Steps[3].State == ConnectionStepState.Skipped && !target.IsDefault,
                "Disabled automatic routing is shown as skipped rather than falsely verified");
        }
        private static void RestoringDriverBudget()
        {
            var backend = new FakeConnectionPlatform { EndpointOnRefresh = 1, TransientFailures = 6, ConnectOnRequest = 8 };
            var target = SavedFixture(); var trace = new ConnectionTrace(false);
            new ConnectionFlow(backend, 1500, 5, 1).ConnectAsync(target, true, false, trace).GetAwaiter().GetResult();
            Assert(target.Connected && target.IsDefault && backend.Requests == 8 && !trace.Failed,
                "Transient driver restoration cannot spend the accepted-reconnect budget before the radio is usable");
        }
        private static void UnpairedConnectionFlow()
        {
            var backend = new FakeConnectionPlatform { RejectPair = true };
            var trace = new ConnectionTrace(false);
            bool failed = false;
            try { new ConnectionFlow(backend).ConnectAsync(SavedFixture(), true, false, trace).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && backend.Requests == 0 && backend.Refreshes == 0 && backend.Routes == 0,
                "A removed or unconfirmed bond cannot receive a reconnect request");
            Assert(trace.Failed && trace.Steps[0].State == ConnectionStepState.Complete && trace.Steps[1].State == ConnectionStepState.Failed &&
                trace.Steps[2].State == ConnectionStepState.Pending, "The stream stops on the exact failed stage");
        }
        private static void RefusedConnectionFlow()
        {
            var backend = new FakeConnectionPlatform { ConnectOnRequest = Int32.MaxValue, EndpointOnRefresh = 1 };
            var target = SavedFixture(); var trace = new ConnectionTrace(false);
            bool timedOut = false;
            try { new ConnectionFlow(backend, 60, 5, 1).ConnectAsync(target, true, false, trace).GetAwaiter().GetResult(); }
            catch (TimeoutException) { timedOut = true; }
            Assert(timedOut && !target.Connected && !target.IsDefault && backend.Requests > 1 && backend.Requests <= 4 && backend.Routes == 0,
                "Bounded retries cannot claim a refused handoff succeeded");
            Assert(trace.Steps[2].State == ConnectionStepState.Failed && trace.Steps[3].State == ConnectionStepState.Pending,
                "Failed audio does not complete the Windows-output stage");
        }
        private static void FailedRouteFlow()
        {
            var backend = new FakeConnectionPlatform { AlreadyActive = true, EndpointOnRefresh = 1, FailRoute = true };
            var target = SavedFixture(); var trace = new ConnectionTrace(false);
            bool failed = false;
            try { new ConnectionFlow(backend).ConnectAsync(target, true, false, trace).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed && target.Connected && !target.IsDefault && trace.Steps[2].State == ConnectionStepState.Complete && trace.Steps[3].State == ConnectionStepState.Failed,
                "A route failure keeps real connectivity while exposing the failed output stage");
        }

        private static void NormalMapping()
        {
            BatteryReading value = Read(Packet(0x200E, 0x22, 0x73, 0x56));
            Equal((int?)30, value.Left, "Low nibble is left when status bit 20 is set");
            Equal((int?)70, value.Right, "High nibble is right when status bit 20 is set");
            Equal((int?)60, value.Case, "Case comes from the low charging nibble");
            Assert(value.LeftCharging && !value.RightCharging && value.CaseCharging, "Normal charging flags map independently");
            Assert(value.LeftInEar && !value.RightInEar, "Normal in-ear flags map independently");
            Assert(value.Fresh, "A newly decoded packet is fresh");
        }

        private static void SwappedMapping()
        {
            BatteryReading value = Read(Packet(0x200E, 0x08, 0x37, 0x66));
            Equal((int?)30, value.Left, "High nibble is left when status bit 20 is clear");
            Equal((int?)70, value.Right, "Low nibble is right when status bit 20 is clear");
            Equal((int?)60, value.Case, "Swapping does not change case battery");
            Assert(value.LeftCharging && !value.RightCharging && value.CaseCharging, "Swapped charging flags follow physical sides");
            Assert(value.LeftInEar && !value.RightInEar, "Swapped in-ear flags follow physical sides");
        }

        private static void OppositeSideFlags()
        {
            BatteryReading normal = Read(Packet(0x200F, 0x28, 0x94, 0x29));
            BatteryReading swapped = Read(Packet(0x200F, 0x02, 0x49, 0x19));
            foreach (BatteryReading value in new[] { normal, swapped })
            {
                Equal((int?)40, value.Left, "Physical left battery remains unchanged after swapping");
                Equal((int?)90, value.Right, "Physical right battery remains unchanged after swapping");
                Assert(!value.LeftCharging && value.RightCharging && !value.CaseCharging, "Only physical right is charging");
                Assert(!value.LeftInEar && value.RightInEar, "Only physical right is in ear");
            }
            BatteryReading clear = Read(Packet(0x2002, 0x20, 0x00, 0x00));
            Assert(!clear.LeftCharging && !clear.RightCharging && !clear.CaseCharging && !clear.LeftInEar && !clear.RightInEar,
                "Zero flags do not create charging or in-ear status");
        }

        private static void SupportedModels()
        {
            ushort[] models = { 0x2002, 0x200F, 0x200E, 0x2013, 0x2014, 0x2024, 0x200A, 0x201F, 0x2019, 0x201B, 0x2027 };
            foreach (ushort model in models)
                Equal(model, Read(Packet(model, 0x20, 0xAA, 0x0A)).Model, "Model must decode little-endian");
        }

        private static void UnknownModels()
        {
            ushort[] models = { 0x0000, 0xFFFF, 0x0E20, 0x2003, 0x2020, 0x2028 };
            foreach (ushort model in models)
                Assert(BatteryMonitor.Parse(Packet(model, 0x20, 0x99, 0x09)) == null, "Unknown model must stay unknown: " + model.ToString("X4"));
        }

        private static void MissingData()
        {
            Assert(BatteryMonitor.Parse(null) == null, "Null advertisement");
            Assert(BatteryMonitor.Parse(new byte[0]) == null, "Empty advertisement");
            Assert(BatteryMonitor.Parse(new byte[] { 0x07 }) == null, "Missing TLV length");
            byte[] unrelated = Packet(0x200E, 0x20, 0x99, 0x09);
            unrelated[0] = 0x10;
            Assert(BatteryMonitor.Parse(unrelated) == null, "Unrelated Apple TLVs are not battery data");
        }

        private static void Truncation()
        {
            byte[] complete = Packet(0x200E, 0x20, 0x99, 0x09);
            for (int length = 0; length < complete.Length; length++)
                Assert(BatteryMonitor.Parse(complete.Take(length).ToArray()) == null, "Truncated packet length " + length);
        }

        private static void MalformedLengths()
        {
            for (int length = 0; length <= 255; length++)
            {
                if (length == 25) continue;
                byte[] packet = new byte[length + 2];
                packet[0] = 7;
                packet[1] = (byte)length;
                if (length >= 6)
                {
                    packet[3] = 0x0E; packet[4] = 0x20; packet[5] = 0x20;
                    packet[6] = 0x99; packet[7] = 0x09;
                }
                Assert(BatteryMonitor.Parse(packet) == null, "Unsupported complete TLV length " + length);
            }
            byte[] valid = Packet(0x200E, 0x20, 0x99, 0x09);
            Assert(BatteryMonitor.Parse(new byte[] { 0x10, 0xFF }.Concat(valid).ToArray()) == null,
                "An overlong prefix cannot be skipped to decode arbitrary following bytes");
            valid[1] = 0xFF;
            Assert(BatteryMonitor.Parse(valid) == null, "Declared TLV length cannot exceed available bytes");
        }

        private static void TlvTraversal()
        {
            byte[] packet = new byte[] { 0x10, 0x02, 0xAA, 0xBB, 0x01, 0x00 }
                .Concat(Packet(0x2014, 0x20, 0x82, 0x05)).ToArray();
            BatteryReading value = Read(packet);
            Equal((int?)20, value.Left, "Battery TLV following unrelated data is found");
            Equal((int?)80, value.Right, "Traversal maintains TLV-relative offsets");
            Equal((int?)50, value.Case, "Traversal maintains case offset");
        }

        private static void BatteryNibbles()
        {
            for (int nibble = 0; nibble <= 15; nibble++)
            {
                int? expected = nibble <= 10 ? (int?)(nibble * 10) : null;
                BatteryReading normal = Read(Packet(0x200E, 0x20, (byte)(0xA0 | nibble), 0x0A));
                BatteryReading swapped = Read(Packet(0x200E, 0x00, (byte)((nibble << 4) | 0x0A), 0x0A));
                BatteryReading right = Read(Packet(0x200E, 0x20, (byte)(nibble << 4), 0x0A));
                BatteryReading caseValue = Read(Packet(0x200E, 0x20, 0xAA, (byte)nibble));
                Equal(expected, normal.Left, "Normal left nibble " + nibble);
                Equal(expected, swapped.Left, "Swapped left nibble " + nibble);
                Equal(expected, right.Right, "Normal right nibble " + nibble);
                Equal(expected, caseValue.Case, "Case nibble " + nibble);
                Equal((int?)100, normal.Right, "Known full battery survives another side being unknown");
            }
        }

        private static void AllUnknownBatteries()
        {
            for (int state = 0; state <= 0x20; state += 0x20)
                for (int nibble = 11; nibble <= 15; nibble++)
                    Assert(BatteryMonitor.Parse(Packet(0x200E, (byte)state, (byte)((nibble << 4) | nibble), (byte)(0x70 | nibble))) == null,
                        "Unknown battery nibbles must not produce a 0% reading");
            BatteryReading empty = Read(Packet(0x200E, 0x20, 0x00, 0x00));
            Equal((int?)0, empty.Left, "An actual zero battery is valid");
            Equal((int?)0, empty.Right, "An actual zero battery is valid");
            Equal((int?)0, empty.Case, "An actual zero case is valid");
        }

        private static void AddressNormalization()
        {
            Equal("AABBCCDDEEFF", DeviceDiscovery.NormalizeAddress("aa:bb:cc:dd:ee:ff"), "Colon-separated address");
            Equal("AABBCCDDEEFF", DeviceDiscovery.NormalizeAddress("aa-bb-cc-dd-ee-ff"), "Hyphen-separated address");
            Equal("AABBCCDDEEFF", DeviceDiscovery.NormalizeAddress("  aabbccddeeff  "), "Compact address permits surrounding whitespace");
            Equal("001122334455", DeviceDiscovery.NormalizeAddress("00:11:22:33:44:55"), "Valid leading zeros are retained");
            Equal<string>(null, DeviceDiscovery.NormalizeAddress(null), "Unknown address stays unknown");
            Equal("001122334455", new BatteryReading { Address = 0x001122334455 }.AddressLabel, "Leading zeros preserve 48-bit identity");
        }

        private static void MalformedAddresses()
        {
            string[] invalid = { "", " ", "0", "000000000000", "00:00:00:00:00:00", "00-00-00-00-00-00",
                "AABBCCDDEEF", "AABBCCDDEEFF0", "GG:BB:CC:DD:EE:FF", "00112233 455", "0x1122334455", "+01122334455" };
            foreach (string address in invalid)
                Equal<string>(null, DeviceDiscovery.NormalizeAddress(address), "Malformed or zero address: " + address);
        }

        private static void AirPodsHardwareIdentity()
        {
            foreach (int model in AirPodsModels.KnownIds)
                Assert(DeviceDiscovery.IsAirPodsHardwareId(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001004C_PID&" + model.ToString("X4")),
                    "A recognized AirPods hardware identity does not depend on its user-defined name: " + model.ToString("X4"));
            Assert(DeviceDiscovery.IsAirPodsHardwareId(@"bthenum\service_vid&0001004c_pid&2027"),
                "Renamed PID 2027 remains discoverable with case-insensitive PnP tokens");
            string[] invalid = { null, "", "AirPods", @"BTHENUM\service_VID&0001004C",
                @"BTHENUM\service_PID&2027", @"USB\service_VID&0001004C_PID&200B",
                @"BTHENUM\service_VID&0001004C_PID&2020",
                @"BTHENUM\service_VID&0001004C_PID&FFFF", @"BTHENUM\service_VID&00010005_PID&2027",
                @"BTHENUM\service_VID&0001004C_PID&202", @"BTHENUM\service_VID&0001004C_PID&ZZZZ",
                @"BTHENUM\service_VID&0001004C_PID&", @"BTHENUM\service_VID&004C_PID&2027",
                @"BTHENUM\service_VID&0001004C0_PID&2027", @"BTHENUM\service_VID&0001004C_PID&20270" };
            foreach (string id in invalid)
                Assert(!DeviceDiscovery.IsAirPodsHardwareId(id), "Missing, malformed, wrong-vendor or unknown PID is rejected: " + id);
            const string futureAudio = @"BTHENUM\service_VID&0001004C_PID&FFFF";
            Assert(DeviceDiscovery.IsAppleAudioHardwareId(futureAudio, true), "Unknown Apple product with confirmed Bluetooth audio can connect generically");
            Assert(!DeviceDiscovery.IsAppleAudioHardwareId(futureAudio, false), "A phone or keyboard cannot become headphones from its vendor alone");
            Assert(!DeviceDiscovery.IsAppleAudioHardwareId(@"BTHENUM\service_VID&00010005_PID&2027", true), "Audio endpoints cannot override wrong hardware vendor");
        }

        private static void ModelIdentity()
        {
            // Independent hardware product IDs and their expected UI names.
            int[] models = { 0x2002, 0x200F, 0x2013, 0x2019, 0x201B, 0x200E, 0x2014, 0x2024, 0x2027, 0x200A, 0x201F };
            string[] names = { "AirPods (1st generation)", "AirPods (2nd generation)", "AirPods (3rd generation)",
                "AirPods 4", "AirPods 4 with ANC", "AirPods Pro", "AirPods Pro 2 (Lightning)",
                "AirPods Pro 2 (USB-C)", "AirPods Pro 3", "AirPods Max (Lightning)", "AirPods Max (USB-C)" };
            string[] families = { "pods", "pods", "pods", "pods", "pods", "pro", "pro", "pro", "pro3", "max", "max" };
            for (int i = 0; i < models.Length; i++)
            {
                string label = models[i].ToString("X4");
                Assert(AirPodsModels.IsKnown(models[i]), "Verified product is recognized: " + label);
                Equal(names[i], AirPodsModels.Name(models[i]), "Product generation label: " + label);
                Equal(families[i], AirPodsModels.Family(models[i]), "Product artwork family: " + label);
                var target = new AudioTarget { ModelId = models[i], Name = "My renamed headphones" };
                Equal(names[i], target.ModelName, "Renaming never changes product generation: " + label);
            }
            var changing = new AudioTarget { ModelId = 0x2027, Name = "Personal name" };
            Equal("AirPods Pro 3", changing.ModelName, "Pro 3 has its own identity");
            changing.ModelId = 0x201F;
            Equal("AirPods Max (USB-C)", changing.ModelName, "ModelName derives from current hardware identity");
            Equal("Personal name", changing.Name, "User-defined display name stays intact");
        }

        private static void UnknownModelIdentity()
        {
            int[] unsupported = { 0, -1, Int32.MaxValue, 0xFFFF, 0x2020, 0x2028 };
            foreach (int model in unsupported)
            {
                Assert(!AirPodsModels.IsKnown(model), "Unsupported ID is not recognized: " + model);
                Equal("Apple audio", AirPodsModels.Name(model), "Unsupported ID keeps a generic label");
                Equal("unknown", AirPodsModels.Family(model), "Unsupported ID keeps generic artwork");
                Equal("Apple audio", new AudioTarget { ModelId = model, Name = "AirPods Pro 3" }.ModelName,
                    "A friendly name cannot promote unknown hardware to Pro 3");
            }
        }

        private static void AppleAudioCatalog()
        {
            int[] expanded = { 0x2036, 0x2030, 0x202D, 0x2003, 0x2005, 0x2006, 0x2009,
                0x200B, 0x200C, 0x200D, 0x2010, 0x2011, 0x2012, 0x2016, 0x2017, 0x201D, 0x2025, 0x2026, 0x2038 };
            string[] keys = { "airpods-5", "airpods-5-wireless", "airpods-max-2", "powerbeats-3", "beats-x", "beats-solo-3", "beats-studio-3",
                "powerbeats-pro", "beats-solo-pro", "powerbeats-4", "beats-flex", "beats-studio-buds", "beats-fit-pro", "beats-studio-buds-plus",
                "beats-studio-pro", "powerbeats-pro-2", "beats-solo-4", "beats-solo-buds", "beats-360" };
            for (int i = 0; i < expanded.Length; i++)
            {
                int model = expanded[i];
                Assert(AirPodsModels.IsKnown(model), "Current or legacy Apple wireless audio identity recognized " + model.ToString("X4"));
                Equal(keys[i], AirPodsModels.ArtworkKey(model), "Each product has a stable distinct artwork key");
                Assert(!BatteryMonitor.IsSupportedModel(model), "Catalog growth does not enable an unvalidated battery decoder");
                Assert(BatteryMonitor.Parse(Packet((ushort)model, 0x20, 0x99, 0x09)) == null,
                    "Unvalidated headset formats cannot become invented three-part battery readings");
            }
            Equal(AirPodsModels.KnownIds.Length, AirPodsModels.KnownIds.Select(AirPodsModels.ArtworkKey).Distinct().Count(), "All known product artwork keys are unique");
            Equal("AirPods 5", AirPodsModels.Name(0x2036), "AirPods 5 identity");
            Equal("AirPods Max 2", AirPodsModels.Name(0x202D), "AirPods Max 2 identity");
            Equal("Powerbeats Pro 2", AirPodsModels.Name(0x201D), "Powerbeats Pro 2 identity");
            foreach (int model in new[] { 0x200A, 0x201F, 0x202D, 0x2006, 0x2009, 0x200C, 0x2017, 0x2025, 0x2038 })
            {
                Assert(AirPodsModels.IsOverEar(model), "Headphone layout recognized " + model.ToString("X4"));
                Equal("single", AirPodsModels.ComponentLayout(model), "Headphones expose one device battery, not left/right/case");
            }
            foreach (int model in new[] { 0x2003, 0x2005, 0x200D, 0x2010 })
                Equal("single", AirPodsModels.ComponentLayout(model), "Neckband earphones expose one device battery");
            foreach (int model in new[] { 0x2002, 0x2036, 0x2030, 0x2027, 0x200B, 0x201D, 0x2012, 0x2011, 0x2016 })
                Equal("dual-case", AirPodsModels.ComponentLayout(model), "True wireless charging case layout");
            Equal("dual", AirPodsModels.ComponentLayout(0x2026), "Solo Buds case has no battery to invent");
            Equal("single", AirPodsModels.ComponentLayout(0), "Unknown device does not invent component layout");
        }

        private static BatteryReading Beacon(ulong address, short rssi, int ageSeconds)
        {
            return new BatteryReading { Address = address, Model = 0x2027, Rssi = rssi, Seen = DateTime.UtcNow.AddSeconds(-ageSeconds), Left = 50 };
        }

        private static AudioTarget Target(string address)
        {
            return new AudioTarget { ContainerId = "11111111-2222-3333-4444-555555555555", Address = address, ModelId = 0x2027,
                Name = "My renamed AirPods", IsApple = true };
        }

        private static void Identity(BatteryMonitor monitor, ulong beacon, string container, string classicAddress, int model, int ageSeconds)
        {
            Type nested = typeof(BatteryMonitor).GetNestedType("ResolvedIdentity", BindingFlags.NonPublic);
            object identity = Activator.CreateInstance(nested, true);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            nested.GetField("ContainerId", flags).SetValue(identity, container);
            nested.GetField("ClassicAddress", flags).SetValue(identity, classicAddress);
            nested.GetField("Model", flags).SetValue(identity, model);
            nested.GetField("Seen", flags).SetValue(identity, DateTime.UtcNow.AddSeconds(-ageSeconds));
            var cache = (System.Collections.IDictionary)typeof(BatteryMonitor).GetField("identities", flags).GetValue(monitor);
            cache[beacon] = identity;
        }

        private static BatteryMonitor Cache(params BatteryReading[] values)
        {
            BatteryMonitor monitor = new BatteryMonitor();
            FieldInfo field = typeof(BatteryMonitor).GetField("readings", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "Test cache injection requires the existing private readings dictionary");
            Dictionary<ulong, BatteryReading> cache = (Dictionary<ulong, BatteryReading>)field.GetValue(monitor);
            foreach (BatteryReading value in values) cache.Add(value.Address, value);
            return monitor;
        }

        private static void NeighborIdentity()
        {
            BatteryReading neighbor = Beacon(0xAABBCCDDEEFF, -20, 0);
            using (BatteryMonitor monitor = Cache(neighbor))
            {
                AudioTarget target = Target("001122334455");
                monitor.RegisterTargets(new[] { target });
                Equal(1, monitor.Candidates().Count, "Loud neighbor is a selectable proximity candidate");
                Assert(monitor.ForTarget(target) == null,
                    "A close AirPods beacon cannot substitute for another classic Bluetooth address");
                target.Address = null;
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null,
                    "An AirPods display name does not authenticate a random BLE address");
            }
        }

        private static void ExplicitIdentity()
        {
            BatteryReading own = Beacon(0x001122334455, -75, 20);
            BatteryReading neighbor = Beacon(0xAABBCCDDEEFF, -20, 0);
            foreach (bool reverse in new[] { false, true })
            using (BatteryMonitor monitor = reverse ? Cache(own, neighbor) : Cache(neighbor, own))
            {
                AudioTarget target = Target(own.AddressLabel);
                monitor.RegisterTargets(new[] { target });
                Identity(monitor, neighbor.Address, target.ContainerId, null, 0, 0);
                BatteryReading selected = monitor.ForTarget(target);
                Assert(selected != null && selected.Address == own.Address, "Exact identity wins over a newer resolved beacon in either insertion order");
                Equal("Paired AirPods Bluetooth address", selected.Source, "Source identifies direct paired-address evidence");
                Assert(own.Source == null, "Adding source metadata does not mutate shared cached readings");
            }
        }

        private static void StaleReadings()
        {
            BatteryReading stale = Beacon(0x001122334455, -25, 120);
            BatteryReading fresh = Beacon(0xAABBCCDDEEFF, -30, 0);
            Assert(!stale.Fresh && fresh.Fresh, "Age distinguishes stale and current telemetry");
            using (BatteryMonitor monitor = Cache(stale, fresh))
            {
                AudioTarget target = Target(stale.AddressLabel);
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null, "Stale exact identity never falls back to a fresh unverified neighbor");
                Identity(monitor, fresh.Address, target.ContainerId, null, 0, 181);
                Assert(monitor.ForTarget(target) == null, "Stale Windows identity mapping is rejected even for fresh battery data");
                Assert(monitor.Candidates().All(r => r.Address != stale.Address), "Stale nearby beacons are excluded");
            }
        }

        private static void NearbyCandidates()
        {
            BatteryReading edge = Beacon(1, BatteryMonitor.NearbyRssiThreshold, 0);
            BatteryReading far = Beacon(2, (short)(BatteryMonitor.NearbyRssiThreshold - 1), 0);
            BatteryReading close = Beacon(3, -30, 0);
            using (BatteryMonitor monitor = Cache(far, edge, close))
            {
                List<BatteryReading> candidates = monitor.Candidates();
                Equal(2, candidates.Count, "Only signals at or above the nearby cutoff are offered");
                Equal(close.Address, candidates[0].Address, "Strongest signal is listed first");
                Equal(edge.Address, candidates[1].Address, "The exact RSSI cutoff is inclusive");
            }
        }

        private static void EmptyCache()
        {
            using (BatteryMonitor monitor = Cache())
            {
                Equal(0, monitor.Candidates().Count, "No invented proximity candidates");
                Assert(monitor.ForTarget(Target(null)) == null, "Missing telemetry remains unknown");
                Assert(monitor.ForTarget(null) == null, "A battery reading still requires a target");
            }
        }

        private static void ShortIdentityPackets()
        {
            // Synthetic short 07/0F mode-00 identity, NOT a captured BLE packet.
            byte[] packet = { 7, 15, 0, 0x27, 0x20, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0, 0, 0, 0, 0, 0 };
            AirPodsIdentity value = BatteryMonitor.ParseIdentity(packet);
            Assert(value != null, "Recognized short identity is decoded");
            Equal("001122334455", value.Address, "Classic MAC preserves display byte order and leading zeros");
            Equal((ushort)0x2027, value.Model, "Identity model is little-endian");
            Assert(BatteryMonitor.Parse(packet) == null, "Short identity fields cannot become invented battery percentages");
            Assert(BatteryMonitor.ParseIdentity(null) == null, "Missing identity remains unknown");
            for (int length = 0; length < packet.Length; length++)
                Assert(BatteryMonitor.ParseIdentity(packet.Take(length).ToArray()) == null, "Truncated identity length " + length);
            byte[] prefixed = new byte[] { 0x10, 2, 0xAA, 0xBB }.Concat(packet).ToArray();
            Equal(value.Address, BatteryMonitor.ParseIdentity(prefixed).Address, "Identity traversal uses TLV-relative offsets");
            foreach (int index in new[] { 0, 1, 2, 3, 4 })
            {
                byte[] invalid = (byte[])packet.Clone(); invalid[index] = 0xFF;
                Assert(BatteryMonitor.ParseIdentity(invalid) == null, "Wrong type, length, mode or model byte is rejected at " + index);
            }
            byte[] zero = (byte[])packet.Clone(); Array.Clear(zero, 5, 6);
            Assert(BatteryMonitor.ParseIdentity(zero) == null, "Zero classic MAC cannot claim ownership");
            Assert(BatteryMonitor.ParseIdentity(Packet(0x2027, 0x20, 0x99, 0x09)) == null, "Battery-only packet cannot establish classic identity");
        }

        private static void AmbiguousNeighbors()
        {
            AudioTarget target = Target("001122334455");
            BatteryReading first = Beacon(0xAABBCCDDEEFF, -10, 0);
            BatteryReading second = Beacon(0x66778899AABB, -60, 0);
            foreach (bool reverse in new[] { false, true })
            using (BatteryMonitor monitor = reverse ? Cache(second, first) : Cache(first, second))
            {
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null, "Same-model neighbors never gain ownership from signal strength or cache order");
            }
        }

        private static void AuthorizedIdentity()
        {
            AudioTarget target = Target("001122334455");
            BatteryReading beacon = Beacon(0xAABBCCDDEEFF, -30, 0);
            using (BatteryMonitor monitor = Cache(beacon))
            {
                Identity(monitor, beacon.Address, null, target.Address, 0x2027, 0);
                Assert(monitor.ForTarget(target) == null, "Even a matching advertised MAC requires an authorized paired target");
                monitor.RegisterTargets(new[] { target });
                BatteryReading selected = monitor.ForTarget(target);
                Assert(selected != null && selected.Address == beacon.Address, "Matching classic MAC plus authorized model associates a private BLE address");
                Equal("AirPods advertised paired device identity", selected.Source, "Source records advertised classic identity evidence");
                Identity(monitor, beacon.Address, null, "66778899AABB", 0x2027, 0);
                Assert(monitor.ForTarget(target) == null, "Same-model identity for a different classic MAC is rejected");
                Identity(monitor, beacon.Address, null, target.Address, 0x200E, 0);
                Assert(monitor.ForTarget(target) == null, "Advertised model mismatch is rejected");
                Identity(monitor, beacon.Address, null, target.Address, 0x2027, 0);
                beacon.Model = 0x200E;
                Assert(monitor.ForTarget(target) == null, "Battery model must agree with short identity model");
                beacon.Model = 0x2027;
                Identity(monitor, beacon.Address, "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", null, 0, 0);
                Assert(monitor.ForTarget(target) == null, "An unrelated Windows container cannot claim target battery");
                Identity(monitor, beacon.Address, target.ContainerId, null, 0, 0);
                selected = monitor.ForTarget(target);
                Assert(selected != null, "Windows verified same-container identity can associate its beacon");
                Equal("Windows matched AirPods device identity", selected.Source, "Source records Windows container evidence");
                var other = Target(target.Address); other.ContainerId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
                Assert(monitor.ForTarget(other) == null, "A matching display name and MAC cannot bypass container authorization");
                monitor.RegisterTargets(new AudioTarget[0]);
                Assert(monitor.ForTarget(target) == null, "Removing a paired target immediately revokes its previous identity mapping");
            }
        }

        private static void RotatedIdentity()
        {
            AudioTarget target = Target("001122334455");
            BatteryReading known = Beacon(0xAABBCCDDEEFF, -40, 5);
            BatteryReading rotated = Beacon(0x66778899AABB, -20, 0);
            using (BatteryMonitor monitor = Cache(known, rotated))
            {
                monitor.RegisterTargets(new[] { target });
                Identity(monitor, known.Address, null, target.Address, 0x2027, 0);
                Equal(known.Address, monitor.ForTarget(target).Address, "A newer same-model private address cannot inherit an older address proof");
                known.Seen = DateTime.UtcNow.AddMinutes(-2);
                Assert(monitor.ForTarget(target) == null, "Expired proven address cannot fall back to the loud rotated address");
                Identity(monitor, rotated.Address, target.ContainerId, null, 0, 0);
                Equal(rotated.Address, monitor.ForTarget(target).Address, "A rotated address works once Windows independently proves its container");
            }
        }

        private static void DeviceBatteryScope()
        {
            AudioTarget target = Target("001122334455");
            using (BatteryMonitor monitor = Cache())
            {
                // Block the periodic background refresh while exercising only the
                // deterministic authorization/cache path. No WinRT operation runs.
                typeof(BatteryMonitor).GetField("nextBatteryRefresh", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(monitor, DateTime.MaxValue);
                target.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 }); target.Battery = 62;
                monitor.RegisterTargets(new[] { target });
                DeviceBatteryReading value = monitor.GetDeviceBattery(target);
                Assert(value != null && value.Percent == 62 && value.Fresh, "Current Windows device battery is scoped to the authorized connected container");
                Assert(value.Source != null && value.Source.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    value.Source.IndexOf("cached", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Windows snapshot battery identifies its source and possible cache age");
                var other = Target(target.Address); other.ContainerId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
                other.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 });
                Assert(monitor.GetDeviceBattery(other) == null, "Another container cannot consume a cached device battery");
                value.Seen = DateTime.UtcNow.AddMinutes(-4);
                Assert(monitor.GetDeviceBattery(target) == null, "Stale aggregate device battery is rejected");
                target.Endpoints[0].State = 8;
                Assert(monitor.GetDeviceBattery(target) == null, "Disconnected targets do not display cached device battery as live");
                monitor.RegisterTargets(new AudioTarget[0]);
                Assert(monitor.GetDeviceBattery(target) == null, "Snapshot revocation removes cached device battery");
            }
        }

        private static void ManualBatterySource()
        {
            AudioTarget target = Target("001122334455");
            BatteryReading nearby = Beacon(0xAABBCCDDEEFF, -30, 0);
            BatteryReading far = Beacon(0x66778899AABB, -90, 0);
            BatteryReading wrong = Beacon(0x112233445566, -25, 0); wrong.Model = 0x200E;
            using (BatteryMonitor monitor = Cache(nearby, far, wrong))
            {
                Assert(!monitor.SelectSource(target, nearby.Address), "Unregistered target cannot receive a manual source");
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null, "Nearby readable data still requires an explicit choice");
                Equal(1, monitor.CandidatesForTarget(target).Count, "Chooser filters wrong models and weak signals");
                Assert(!monitor.SelectSource(target, far.Address), "Weak source cannot be selected");
                Assert(!monitor.SelectSource(target, wrong.Address), "Wrong model cannot be selected");
                Assert(monitor.SelectSource(target, nearby.Address), "Explicit selection accepts a fresh matching nearby source");
                var value = monitor.ForTarget(target);
                Assert(value != null && value.Address == nearby.Address && value.UserSelected, "Returned reading is explicitly marked user-selected");
                Assert(value.Source.Contains("not verified") && !nearby.UserSelected, "Unverified label is retained without mutating shared cache");
                monitor.ClearSource(target);
                Assert(monitor.ForTarget(target) == null, "Removing a manual source restores strict automatic identity matching");
                Assert(monitor.SelectSource(target, nearby.Address), "Source can be selected again explicitly");
                monitor.RegisterTargets(new AudioTarget[0]);
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null, "Removing the paired target revokes its manual choice");
            }
        }

        private static void VerifiedBeforeManual()
        {
            var own = Beacon(0x001122334455, -80, 1);
            var other = Beacon(0xAABBCCDDEEFF, -20, 0);
            var target = Target(own.AddressLabel);
            using (BatteryMonitor monitor = Cache(own, other))
            {
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.SelectSource(target, other.Address), "Explicit source choice is accepted even if verified data is already cached");
                var value = monitor.ForTarget(target);
                Assert(value.Address == own.Address && !value.UserSelected, "Verified paired address outranks the selected nearby source");
                own.Seen = DateTime.UtcNow.AddMinutes(-2);
                Identity(monitor, other.Address, target.ContainerId, null, 0, 0);
                Assert(!monitor.ForTarget(target).UserSelected, "Windows verified identity is never downgraded to user-selected");
            }
        }

        private static void ManualSourceExpiry()
        {
            var first = Beacon(0xAABBCCDDEEFF, -30, 0);
            var rotated = Beacon(0x66778899AABB, -20, 0);
            var target = Target("001122334455");
            using (BatteryMonitor monitor = Cache(first, rotated))
            {
                monitor.RegisterTargets(new[] { target });
                monitor.SelectSource(target, first.Address);
                first.Seen = DateTime.UtcNow.AddSeconds(-91);
                Assert(monitor.ForTarget(target) == null, "Expired selected source does not switch to another same-model address");
                first.Seen = DateTime.UtcNow;
                Assert(monitor.ForTarget(target) == null, "Expired selection is revoked even if its old address returns");
                monitor.SelectSource(target, first.Address);
                target.Address = "112233445566";
                monitor.RegisterTargets(new[] { target });
                Assert(monitor.ForTarget(target) == null, "Changing the paired identity revokes the old source");
            }
        }

        private static void NativeAbi()
        {
            Equal(8, IntPtr.Size, "Build targets the supported Windows x64 process ABI");
            Equal(20, Marshal.SizeOf(typeof(PropertyKey)), "PROPERTYKEY size");
            Equal(24, Marshal.SizeOf(typeof(KsProperty)), "KSPROPERTY size used in KsProperty calls");
            Equal(24, Marshal.SizeOf(typeof(PropVariant)), "x64 PROPVARIANT size");
            Equal(16L, Marshal.OffsetOf(typeof(PropertyKey), "Id").ToInt64(), "PROPERTYKEY pid offset");
            Equal(16L, Marshal.OffsetOf(typeof(KsProperty), "Id").ToInt64(), "KSPROPERTY id offset");
            Equal(20L, Marshal.OffsetOf(typeof(KsProperty), "Flags").ToInt64(), "KSPROPERTY flags offset");
            Equal(0L, Marshal.OffsetOf(typeof(PropVariant), "Type").ToInt64(), "PROPVARIANT vt offset");
            Equal(8L, Marshal.OffsetOf(typeof(PropVariant), "Pointer").ToInt64(), "PROPVARIANT union offset");
        }

        private static void BluetoothControllerIds()
        {
            Assert(AudioService.IsBluetoothController(@"{2}.\\?\bth#device"), "Expected native Bluetooth controller path");
            Assert(AudioService.IsBluetoothController(@"{2}.\\?\BTH#device"), "Native paths compare case-insensitively");
            Assert(!AudioService.IsBluetoothController(null), "Null controller is rejected");
            Assert(!AudioService.IsBluetoothController(@"{2}.\\?\usb#device"), "USB controllers cannot receive Bluetooth KS requests");
            Assert(!AudioService.IsBluetoothController("bth#device"), "Arbitrary strings containing bth are rejected");
        }

        private static void PlaybackConnectionState()
        {
            AudioTarget target = new AudioTarget();
            target.Endpoints.Add(new AudioEndpoint { Flow = 1, State = 1 });
            target.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 8 });
            Assert(!target.Connected, "An active microphone with disconnected playback is not connected playback");
            target.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 });
            Assert(target.Connected, "An active render endpoint confirms playback connectivity");
        }

        private static void IntegrationDiscovery()
        {
            // Explicit opt-in only. Enumerates metadata; never connects, disconnects,
            // pairs, routes audio or starts the BLE watcher.
            DiscoveryResult result = DeviceDiscovery.DiscoverAsync().GetAwaiter().GetResult();
            Assert(result != null && result.Targets != null, "Discovery returns a structured result");
            Console.WriteLine("Read-only discovery: Bluetooth {0}; {1} paired AirPods audio target(s).", result.Radio, result.Targets.Count);
            if (!String.IsNullOrEmpty(result.Warning)) Console.WriteLine("Discovery warning: " + result.Warning);
            foreach (AudioTarget target in result.Targets)
                Console.WriteLine("  {0}: playback {1}; default {2}; endpoints {3}; OS battery {4}",
                    target.Name, target.Connected ? "active" : "inactive", target.IsDefault, target.Endpoints.Count,
                    target.Battery.HasValue ? target.Battery.Value + "%" : "unknown");
        }
    }
}
