using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Karmac.Tests
{
    // Manual native smoke check. The default invocation is read-only and never
    // starts discovery of unpaired devices, a BLE listener or an audio connection.
    internal static class HardwareCheck
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                string containerId = null;
                bool connect = false, cycle = false, verifyRoute = false, probeBattery = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--connect") connect = true;
                    else if (args[i] == "--cycle") cycle = true;
                    else if (args[i] == "--verify-existing-route") verifyRoute = true;
                    else if (args[i] == "--battery") probeBattery = true;
                    else if (args[i] == "--target" && i + 1 < args.Length) containerId = args[++i];
                    else throw new ArgumentException("Usage: Karmac.HardwareCheck.exe [--target CONTAINER_GUID] [--connect | --cycle] [--verify-existing-route]");
                }
                Guid requested;
                if (containerId != null && (!Guid.TryParse(containerId, out requested) || requested == Guid.Empty))
                    throw new ArgumentException("The target must be a nonempty paired-device container GUID.");
                if ((connect || cycle) && containerId == null)
                    throw new ArgumentException("--connect and --cycle require an explicit --target from the read-only listing.");
                if (connect && cycle) throw new ArgumentException("Choose either --connect or --cycle.");

                var watch = Stopwatch.StartNew();
                var discovery = DeviceDiscovery.DiscoverAsync().GetAwaiter().GetResult();
                Console.WriteLine("Discovery: {0} ms; Bluetooth {1}; {2} paired AirPods target(s).",
                    watch.ElapsedMilliseconds, discovery.Radio, discovery.Targets.Count);
                if (!String.IsNullOrEmpty(discovery.Warning)) Console.WriteLine("Warning: " + discovery.Warning);
                foreach (var device in discovery.Targets)
                    Console.WriteLine("{0}\n  Container: {1}\n  Playback: {2}; multimedia default: {3}; endpoints: {4}; battery: {5}",
                        device.Name, device.ContainerId, device.Connected ? "active" : "inactive", device.IsDefault,
                        device.Endpoints.Count, BatteryReading.Percent(device.Battery));

                string[] before = ReadRoles();
                PrintRoles("Current playback defaults", before);
                AudioTarget target = containerId == null ? null : discovery.Targets.FirstOrDefault(t => SameGuid(t.ContainerId, containerId));
                if (containerId != null && target == null)
                    throw new InvalidOperationException("The requested container is not a confirmed paired AirPods audio target.");
                if (cycle) return Cycle(target);
                if (probeBattery)
                {
                    target = target ?? discovery.Targets.SingleOrDefault(t => t.Connected);
                    if (target == null) throw new InvalidOperationException("Select one connected AirPods target for the battery probe.");
                    using (var monitor = new BatteryMonitor())
                    {
                        monitor.RegisterTargets(discovery.Targets); monitor.Start();
                        var elapsed = Stopwatch.StartNew();
                        while (elapsed.ElapsedMilliseconds < 12000)
                        {
                            var reading = monitor.ForTarget(target);
                            var overall = monitor.GetDeviceBattery(target);
                            if (reading != null || overall != null)
                            {
                                Console.WriteLine("BATTERY_REPORTED: per-component={0}; overall={1}; source={2}",
                                    reading != null, overall == null ? "none" : overall.Percent.ToString(), reading == null ? overall.Source : reading.Source);
                                return 0;
                            }
                            Thread.Sleep(250);
                        }
                        Console.WriteLine("BATTERY_NOT_REPORTED: paired playback={0}; Windows property={1}; readable nearby frames={2}; listener={3}",
                            target.Connected, target.Battery.HasValue, monitor.Candidates().Count, monitor.Status);
                        Console.WriteLine("Nearby readable models: " + String.Join(", ", monitor.Candidates().Select(r => AirPodsModels.Name(r.Model)).Distinct()));
                        return 2;
                    }
                }

                if (connect)
                {
                    target.Endpoints = AudioService.ReadStates(target);
                    if (target.Connected) Console.WriteLine("Reconnect skipped: this target already has active playback; no driver request sent.");
                    else
                    {
                        watch.Restart();
                        AudioService.RequestConnection(target, true);
                        long requestMs = watch.ElapsedMilliseconds;
                        while (watch.Elapsed < TimeSpan.FromSeconds(12))
                        {
                            target.Endpoints = AudioService.ReadStates(target);
                            if (target.Connected) break;
                            Thread.Sleep(100);
                        }
                        if (!target.Connected)
                            throw new TimeoutException("The saved AirPods playback endpoint did not become active within 12 seconds.");
                        Console.WriteLine("Reconnect: driver request {0} ms; active playback confirmed after {1} ms. No routing request sent.",
                            requestMs, watch.ElapsedMilliseconds);
                    }
                }

                if (verifyRoute)
                {
                    if (target == null)
                    {
                        var existing = discovery.Targets.Where(t => t.Connected && t.IsDefault).ToList();
                        if (existing.Count == 1) target = existing[0];
                    }
                    if (target == null || !target.Connected || !target.IsDefault)
                    {
                        Console.WriteLine("Routing verification skipped: no unambiguous AirPods target is already active and default.");
                        return 0;
                    }
                    target.Endpoints = AudioService.ReadStates(target);
                    var render = PreferredRender(target);
                    // Re-read immediately before the optional write. Refuse to change
                    // an unrelated role, including a separately configured call output.
                    before = ReadRoles();
                    if (render == null || before.Any(id => !String.Equals(id, render.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine("Routing verification skipped: all three roles must already use the selected active AirPods playback endpoint.");
                        return 0;
                    }
                    watch.Restart();
                    string routed = AudioService.Route(target);
                    long routeMs = watch.ElapsedMilliseconds;
                    string[] after = ReadRoles();
                    if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase) || !String.Equals(render.Id, routed, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The existing playback defaults changed during verification.");
                    Console.WriteLine("Idempotent routing verified: {0} ms; Console, Multimedia and Communications remained on their existing AirPods endpoint.", routeMs);
                    PrintRoles("Verified playback defaults", after);
                }
                else Console.WriteLine(connect ? "Connection check complete." : "Read-only check complete; no connection or routing requests sent.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static int Cycle(AudioTarget target)
        {
            target.Endpoints = AudioService.ReadStates(target);
            var render = PreferredRender(target);
            string[] originalRoles = ReadRoles();
            if (render == null || !target.IsDefault || originalRoles.Any(id =>
                !String.Equals(id, render.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Cycle skipped: the exact saved target must already be active and default for all three playback roles.");
                return 0;
            }

            // Preserve controller identities even if endpoints temporarily disappear.
            var knownEndpoints = target.Endpoints.ToList();
            var cycleWatch = Stopwatch.StartNew();
            Exception disconnectError = null, restorationError = null;
            long disconnectMs = -1, driverRequestMs = -1, reconnectAndRouteMs = -1;
            Console.WriteLine("Cycling only this saved AirPods target; its original playback route will be restored.");
            try
            {
                try
                {
                    AudioService.RequestConnection(target, false);
                    if (!WaitForPlayback(target, knownEndpoints, null, false, TimeSpan.FromSeconds(6)))
                        throw new TimeoutException("AirPods playback did not become inactive within 6 seconds.");
                    disconnectMs = cycleWatch.ElapsedMilliseconds;
                    Console.WriteLine("Disconnection confirmed: {0} ms.", disconnectMs);
                }
                catch (Exception error) { disconnectError = error; }
            }
            finally
            {
                // A partially successful disconnect must still be followed by a
                // restore attempt, including when the driver reports an error.
                target.Endpoints = knownEndpoints;
                var reconnectWatch = Stopwatch.StartNew();
                try
                {
                    Exception requestError = null;
                    try { AudioService.RequestConnection(target, true); }
                    catch (Exception error) { requestError = error; }
                    driverRequestMs = reconnectWatch.ElapsedMilliseconds;
                    if (!WaitForPlayback(target, knownEndpoints, render.Id, true, TimeSpan.FromSeconds(12)))
                        throw new TimeoutException("The original AirPods playback endpoint did not reactivate within 12 seconds." +
                            (requestError == null ? String.Empty : " Driver request: " + requestError.Message));
                    string routed = AudioService.Route(target);
                    string[] restoredRoles = ReadRoles();
                    reconnectAndRouteMs = reconnectWatch.ElapsedMilliseconds;
                    if (!String.Equals(render.Id, routed, StringComparison.OrdinalIgnoreCase) ||
                        !originalRoles.SequenceEqual(restoredRoles, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The original Console, Multimedia and Communications route was not restored.");
                    Console.WriteLine("Reconnect driver request: {0} ms; reconnect plus verified original route: {1} ms; complete cycle: {2} ms.",
                        driverRequestMs, reconnectAndRouteMs, cycleWatch.ElapsedMilliseconds);
                    if (requestError != null) Console.WriteLine("Driver request warning (playback restoration independently confirmed): " + requestError.Message);
                    PrintRoles("Restored original playback defaults", restoredRoles);
                }
                catch (Exception error) { restorationError = error; }
            }
            if (restorationError != null)
                throw new InvalidOperationException("AirPods restoration was attempted but not confirmed. Open Windows Bluetooth settings if playback remains unavailable.", restorationError);
            if (disconnectError != null)
                throw new InvalidOperationException("The disconnect phase was not confirmed; the original AirPods playback and route have been restored.", disconnectError);
            Console.WriteLine("Cycle passed: inactive playback was observed, then the original active playback endpoint and every original role were restored.");
            return 0;
        }

        private static bool WaitForPlayback(AudioTarget target, List<AudioEndpoint> knownEndpoints,
            string requiredRenderId, bool active, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                target.Endpoints = knownEndpoints;
                target.Endpoints = AudioService.ReadStates(target);
                bool observed = requiredRenderId == null ? target.Connected : target.Endpoints.Any(e =>
                    e.Flow == 0 && e.Active && String.Equals(e.Id, requiredRenderId, StringComparison.OrdinalIgnoreCase));
                if (observed == active) return true;
                Thread.Sleep(100);
            }
            while (watch.Elapsed < timeout);
            return false;
        }

        private static AudioEndpoint PreferredRender(AudioTarget target)
        {
            return target.Endpoints.Where(e => e.Flow == 0 && e.Active)
                .OrderBy(e => IsHandsFree(e.Name) ? 1 : 0).FirstOrDefault();
        }

        private static bool SameGuid(string left, string right)
        {
            Guid a, b;
            return Guid.TryParse(left, out a) && Guid.TryParse(right, out b) && a == b;
        }
        private static bool IsHandsFree(string name)
        {
            return name != null && (name.IndexOf("hands-free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("handsfree", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private static string[] ReadRoles()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var result = new string[3];
            try
            {
                for (int role = 0; role <= 2; role++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (enumerator.GetDefaultAudioEndpoint(0, role, out device) >= 0)
                            AudioService.Check(device.GetId(out result[role]));
                    }
                    finally { AudioService.Release(device); }
                }
            }
            finally { AudioService.Release(enumerator); }
            return result;
        }
        private static void PrintRoles(string label, string[] roles)
        {
            string[] names = { "Console", "Multimedia", "Communications" };
            Console.WriteLine(label + ":");
            for (int role = 0; role < roles.Length; role++)
                Console.WriteLine("  {0}: {1}", names[role], roles[role] ?? "unassigned");
        }
    }
}
