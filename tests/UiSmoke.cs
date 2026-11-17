using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Karmac.Tests
{
    // Read-only UI verification on synthetic AirPods. Nothing pairs, connects, routes
    // audio or powers a radio: every state is staged through the real controller and
    // rendered offscreen. Pass a folder to also save each state as a PNG for review.
    internal static class UiSmoke
    {
        private static string captureDirectory;
        private static int checks;

        [STAThread]
        private static int Main(string[] args)
        {
            // Deterministic offscreen captures.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (string argument in args) if (argument != "--background") captureDirectory = argument;
            if (captureDirectory != null) Directory.CreateDirectory(captureDirectory);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int exit = 1;
            using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                AppController controller = null;
                try
                {
                    Motion.Suppressed = true; // assertions and captures read settled values
                    controller = new AppController(app, null, activation);
                    var window = app.MainWindow;
                    var card = Field<Window>(controller, "card");
                    foreach (var surface in new[] { window, card })
                    {
                        surface.ShowActivated = false; surface.Opacity = 0; surface.IsHitTestVisible = false;
                    }
                    app.DispatcherUnhandledException += (sender, e) => {
                        Console.Error.WriteLine("FAIL Dispatcher: " + e.Exception); e.Handled = true; app.Shutdown();
                    };
                    app.Dispatcher.BeginInvoke(new Action(async () => {
                        try { await Verify(controller, window, card); exit = 0; Console.WriteLine("UI smoke passed: " + checks + " checks; synthetic AirPods only."); }
                        catch (Exception error) { Console.Error.WriteLine("FAIL UI smoke: " + error); }
                        finally { app.Shutdown(); }
                    }));
                    app.Run();
                }
                catch (Exception error) { Console.Error.WriteLine("FAIL Startup: " + error); }
                finally { if (controller != null) controller.Dispose(); }
            }
            return exit;
        }

        private static async Task Verify(AppController controller, Window window, Window card)
        {
            Field<DispatcherTimer>(controller, "timer").Stop();
            window.Show(); // offscreen, transparent, never activated
            var picker = (ComboBox)window.FindName("DevicePicker");
            var toggle = (ToggleButton)window.FindName("ConnectionToggle");
            var primary = (Button)window.FindName("PrimaryButton");
            var secondary = (Button)window.FindName("SecondaryButton");
            var status = (TextBlock)window.FindName("StatusLine");
            var badge = (TextBlock)window.FindName("ConnectionBadge");

            Assert(window.AllowsTransparency && !window.ShowInTaskbar && window.Topmost && window.WindowStyle == WindowStyle.None,
                "Flyout is a borderless, taskbar-free surface above other windows");
            var sheet = (Border)window.FindName("Sheet");
            Assert(sheet.VerticalAlignment == VerticalAlignment.Bottom || sheet.VerticalAlignment == VerticalAlignment.Top,
                "Sheet is anchored to the taskbar edge");

            // No AirPods yet.
            Stage(controller, picker, new List<AudioTarget>(), null, "On");
            Assert(badge.Text == "No AirPods" && !toggle.IsVisible && primary.IsVisible && (string)primary.Content == "Pair AirPods",
                "Without a paired pair the flyout offers pairing and hides the switch");
            Assert(((Grid)window.FindName("BatteryRow")).Visibility != Visibility.Visible, "No battery row without AirPods");
            Shot(window, "flyout-no-airpods");

            var pro = Pair(controller, 0x2027, "AirPods Pro", 0xAABBCC000027UL);
            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "On");
            Assert(badge.Text == "Not connected" && toggle.IsVisible && toggle.IsChecked == false &&
                (string)primary.Content == "Connect", "Disconnected pair offers Connect with the switch off");
            Assert(Tint(window, "ConnectionDot") == Res(window, "Danger"), "Not connected keeps the red status dot");
            Assert(status.Text == pro.ModelName, "The line under the name is just the model; the pill carries the state");
            Shot(window, "flyout-disconnected");

            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "Off");
            Assert(badge.Text == "Bluetooth off" && (string)primary.Content == "Turn on Bluetooth and connect",
                "Bluetooth off explains that Connect turns it on");
            Shot(window, "flyout-bluetooth-off");

            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "No adapter");
            Assert(status.Text == "No Bluetooth adapter found" && (string)primary.Content == "Open Bluetooth settings" && !toggle.IsEnabled,
                "Missing adapter routes to Windows settings and disables the switch");

            // Connected, playing here, with a matched battery broadcast.
            Connect(pro, true);
            Inject(controller, pro, 82, 100, 45, false, true, false);
            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "On");
            Assert(badge.Text == "Connected" && toggle.IsChecked == true && status.Text == pro.ModelName,
                "Connected pair: the pill says Connected and the line names the model");
            Assert(!primary.IsVisible && !secondary.IsVisible && ((StackPanel)window.FindName("Actions")).Visibility == Visibility.Collapsed,
                "Nothing to do when already playing: no action buttons");
            Assert(Tint(window, "ConnectionDot") == Res(window, "On"), "Connected uses the green status dot");
            var left = (RingGauge)window.FindName("LeftRing"); var right = (RingGauge)window.FindName("RightRing"); var caseRing = (RingGauge)window.FindName("CaseRing");
            Assert(left.Known && Math.Abs(left.Value - 0.82) < 0.001 && right.Known && right.Charging && Math.Abs(caseRing.Value - 0.45) < 0.001,
                "Rings show left, right (charging) and case charge");
            Assert(right.Fill == Res(window, "On"), "A charging ring turns green");
            Assert(((Border)window.FindName("CaseCell")).IsVisible, "Case ring shown for a model with a case");
            Shot(window, "flyout-connected");

            Inject(controller, pro, 100, 90, null, false, false, false);
            Invoke(controller, "UpdateBattery");
            Assert(!((Border)window.FindName("CaseCell")).IsVisible && ((Border)window.FindName("RightCell")).IsVisible,
                "A closed case that isn't reporting is left out rather than shown as an empty gauge");
            Shot(window, "flyout-case-closed");

            Inject(controller, pro, 14, 12, 8, false, false, false);
            Invoke(controller, "UpdateBattery");
            Assert(left.Fill == Res(window, "Danger"), "Low charge turns the ring red");
            Shot(window, "flyout-low-battery");

            // Connected with no reading yet: a note, not empty gauges.
            var silent = Pair(controller, 0x2027, "Quiet Pods", 0xAABBCC0E2027UL);
            Connect(silent, true);
            Stage(controller, picker, new List<AudioTarget> { silent }, silent, "On");
            Assert(((Border)window.FindName("NoBattery")).IsVisible && !((Grid)window.FindName("BatteryRow")).IsVisible,
                "Connected without a reading shows an honest note instead of empty gauges");
            Shot(window, "flyout-no-reading");
            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "On");

            // Device menu: two pairs, the selected one checked.
            var second = Pair(controller, 0x2024, "Office AirPods", 0xAABBCC0A2024UL);
            Stage(controller, picker, new List<AudioTarget> { pro, second }, pro, "On");
            picker.IsDropDownOpen = true;
            var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
            popup.Child.Opacity = 1; popup.UpdateLayout(); popup.Child.UpdateLayout();
            Assert(picker.Items.Count == 2, "Device menu lists every paired pair");
            await Task.Delay(400);
            if (captureDirectory != null) CaptureMenu((FrameworkElement)popup.Child, "flyout-device-menu");
            picker.IsDropDownOpen = false;
            Stage(controller, picker, new List<AudioTarget> { pro }, pro, "On");

            // Connected elsewhere: offer to play here.
            pro.IsDefault = false; Invoke(controller, "UpdateDevice");
            Assert(primary.IsVisible && (string)primary.Content == "Play on this PC", "Connected but not the output offers Play on this PC");
            pro.IsDefault = true;

            // A warning wins the status line.
            Invoke(controller, "SetStatus", "Couldn't switch audio", "Synthetic detail for review.", false, true);
            Assert(status.Text == "Couldn't switch audio" && status.Foreground == Res(window, "Warn"), "Warnings take the status line in the warning colour");
            Invoke(controller, "SetStatus", "", "", false, false);


            // AirPods Max: one ring, no case.
            var max = Pair(controller, 0x202D, "Studio Max", 0xAABBCC00202DUL);
            Connect(max, true); max.Battery = 64;
            Stage(controller, picker, new List<AudioTarget> { max }, max, "On");
            Assert(left.Known && Math.Abs(left.Value - 0.64) < 0.001 && !((Border)window.FindName("RightCell")).IsVisible &&
                !((Border)window.FindName("CaseCell")).IsVisible && ((TextBlock)window.FindName("LeftLabel")).Text == "Battery",
                "Over-ear AirPods show a single battery ring");
            Shot(window, "flyout-max");

            await VerifyConnectionStream(controller, window, picker);
            VerifySettings(controller, window);
            VerifyCard(controller, card, picker);
            VerifyTheme(controller, window, picker, pro);
            VerifyTrayPlacement(controller, window);
            await Task.Delay(10);
        }

        private sealed class StreamFixturePlatform : IConnectionPlatform
        {
            internal readonly TaskCompletionSource<bool> PairGate = new TaskCompletionSource<bool>();
            internal int Requests;
            internal bool RejectPair;
            internal string Radio = "On";
            public Task<bool> PowerOnAsync(Action starting) { starting(); Radio = "On"; return Task.FromResult(true); }
            public Task<string> ReadRadioStateAsync() { return Task.FromResult(Radio); }
            public Task<bool?> ReadLinkAsync(AudioTarget target, CancellationToken token) { return Task.FromResult((bool?)null); }
            public Task PageAsync(AudioTarget target, CancellationToken token) { return Task.FromResult(true); }
            public NearbyHint Nearby(AudioTarget target) { return NearbyHint.Unknown; }
            public async Task VerifyPairAsync(AudioTarget target, CancellationToken token)
            {
                await PairGate.Task;
                if (RejectPair) throw new InvalidOperationException("Windows couldn't confirm this saved pair. Pair your AirPods in Windows if the pairing was removed.");
                target.RequiresPairingCheck = false;
            }
            public Task RefreshAudioAsync(AudioTarget target)
            {
                target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Id = "synthetic-output", ControllerId = @"{2}.\\?\bth#fixture", Flow = 0, State = Requests >= 1 ? 1 : 8 } };
                return Task.FromResult(true);
            }
            public Task ReadAudioAsync(AudioTarget target) { foreach (var e in target.Endpoints) e.State = Requests >= 1 ? 1 : 8; return Task.FromResult(true); }
            public Task RequestAsync(AudioTarget target, bool connect) { if (connect) Requests++; return Task.FromResult(true); }
            public Task<AudioService.OutputSnapshot> RouteAsync(AudioTarget target) { return Task.FromResult<AudioService.OutputSnapshot>(null); }
            public Task DelayAsync(int milliseconds, CancellationToken token) { return Task.Delay(10, token); }
        }

        private static async Task VerifyConnectionStream(AppController controller, Window window, ComboBox picker)
        {
            var original = Field<IConnectionPlatform>(controller, "connectionPlatform");
            var pair = Pair(controller, 0x2024, "Desk AirPods", 0xAABBCC002024UL);
            pair.RequiresPairingCheck = true;
            Stage(controller, picker, new List<AudioTarget> { pair }, pair, "On");
            var stream = (Border)window.FindName("ConnectionStream");
            var secondary = (Button)window.FindName("SecondaryButton");
            try
            {
                var held = new StreamFixturePlatform();
                SetField(controller, "connectionPlatform", held);
                var connecting = (Task)Invoke(controller, "ConnectAsync", true);
                Assert(await Until(() => stream.IsVisible, 3000), "Connecting expands the live steps inside the flyout");
                Assert(((TextBlock)window.FindName("ConnectionBadge")).Text == "Connecting" && secondary.IsVisible && (string)secondary.Content == "Cancel",
                    "While connecting the chip says so and Cancel is offered");
                Assert(((ToggleButton)window.FindName("ConnectionToggle")).IsChecked == true && ((ToggleButton)window.FindName("ConnectionToggle")).IsEnabled,
                    "The switch stays on and enabled so it can cancel");
                Assert(((ItemsControl)window.FindName("ConnectionSteps")).Items.Count >= 3, "Steps stream in as named stages");
                await Task.Delay(300); // let the step rows finish their entrance
                Shot(window, "flyout-connecting");
                held.RejectPair = true; held.PairGate.SetResult(true);
                await connecting;
                Assert(stream.IsVisible && ((TextBlock)window.FindName("ConnectionTitle")).Text == "Couldn't connect",
                    "A failed attempt keeps its steps visible with a clear title");
                Assert(((Button)window.FindName("PrimaryButton")).IsVisible && !secondary.IsVisible, "After a failure Connect is offered again");
                Shot(window, "flyout-failed");

                var ok = new StreamFixturePlatform(); ok.PairGate.SetResult(true);
                SetField(controller, "connectionPlatform", ok);
                pair.RequiresPairingCheck = true;
                await (Task)Invoke(controller, "ConnectAsync", true);
                Assert(pair.Connected && !stream.IsVisible, "A successful connect collapses the steps back into the summary");
            }
            finally
            {
                SetField(controller, "connectionPlatform", original);
                Field<ConnectionTrace>(controller, "connectionTrace").Reset();
                Invoke(controller, "UpdateDevice");
            }
        }

        private static void VerifySettings(AppController controller, Window window)
        {
            Invoke(controller, "ShowSettings", true, false);
            window.UpdateLayout();
            var page = (Grid)window.FindName("SettingsPage");
            Assert(page.IsVisible && !((StackPanel)window.FindName("HomePage")).IsVisible, "Settings replaces the home page inside the same sheet");
            foreach (string name in new[] { "AutoRouteCheck", "SwitchWhenConnectedCheck", "NotificationsCheck", "HotkeyCheck" })
                Assert(window.FindName(name) is CheckBox, "Setting present: " + name);
            Assert(window.FindName("PowerOffBluetoothCheck") == null && window.FindName("PauseWhenRemovedCheck") == null,
                "Removed features stay removed: Bluetooth power-off and ear pause");
            var sheet = (Border)window.FindName("Sheet");
            Assert(sheet.ActualHeight <= window.ActualHeight, "Settings fit inside the flyout window");
            Assert(window.FindName("TrayBatteryCheck") == null, "Battery stays in the flyout and tooltip, not as extra tray icons");
            var tray = (CheckBox)window.FindName("NotificationsCheck");
            bool before = tray.IsChecked == true;
            Toggle(tray); Assert(Field<Preferences>(controller, "settings").Notifications != before, "Toggling a setting updates preferences");
            Toggle(tray);
            Shot(window, "flyout-settings");
            Invoke(controller, "ShowSettings", false, false);
            var gear = (Button)window.FindName("SettingsButton");
            gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(page.IsVisible && (string)gear.Tag == "Active", "The gear opens Settings and shows as active");
            gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(!page.IsVisible && gear.Tag == null, "Clicking the gear again collapses Settings back to home");
        }

        private static void VerifyCard(AppController controller, Window card, ComboBox picker)
        {
            var pro = (AudioTarget)picker.SelectedItem;
            var near = new BatteryReading { Address = 0xAABBCC0FFFFFUL, Model = (ushort)0x2027, Left = 100, Right = 90, Case = 70,
                CaseCharging = true, Rssi = -40, Seen = DateTime.UtcNow, Source = "Synthetic card fixture" };
            Invoke(controller, "ShowCard", pro, "AirPods Pro 3 nearby · if they're yours", true, near);
            card.UpdateLayout();
            var action = (Button)card.FindName("CardAction");
            Assert(card.IsVisible && action.IsVisible && (string)action.Content == "Connect", "Case-open card offers Connect");
            var caseRing = (RingGauge)card.FindName("CardCaseRing");
            Assert(caseRing.Known && caseRing.Charging && Math.Abs(caseRing.Value - 0.7) < 0.001, "Card rings use the nearby reading");
            Assert(((TextBlock)card.FindName("CardStatus")).Text.Contains("if they're yours"), "Card wording doesn't claim a nearby pair is the user's");
            ShotCard(card, "card-nearby");
            Invoke(controller, "CloseCard");
            Invoke(controller, "ShowCard", pro, "Connected · playing on this PC", false, null);
            card.UpdateLayout();
            Assert(!action.IsVisible, "Connected card has no action");
            ShotCard(card, "card-connected");
            Invoke(controller, "CloseCard");
            Assert(!card.IsVisible, "Card dismisses back into the taskbar");
        }

        private static void VerifyTheme(AppController controller, Window window, ComboBox picker, AudioTarget pro)
        {
            var sheet = ((SolidColorBrush)((Border)window.FindName("Sheet")).Background).Color;
            var card = ((SolidColorBrush)((Border)Field<Window>(controller, "card").FindName("Sheet")).Background).Color;
            Assert(sheet == Colors.Black && card == Colors.Black && !Theme.Light, "Flyout and card are pure black in every Windows mode");
            var raised = ((SolidColorBrush)window.FindResource("Raised")).Color;
            Assert(raised.R <= 0x10 && raised.G <= 0x10 && raised.B <= 0x10, "Raised surfaces stay near-black, separated by hairlines");
        }

        private static void VerifyTrayPlacement(AppController controller, Window window)
        {
            var popup = Field<TrayPopup>(controller, "flyout");
            popup.Place();
            Rect area = SystemParameters.WorkArea;
            Assert(window.Left + window.Width <= area.Right + 1 && window.Top + window.Height <= area.Bottom + 1 &&
                window.Left >= area.Left - 1 && window.Top >= area.Top - 1, "Flyout docks inside the work area beside the tray");
        }

        // ---- Fixtures -------------------------------------------------------------------

        private static AudioTarget Pair(AppController controller, int model, string name, ulong address)
        {
            var target = new AudioTarget { ContainerId = Guid.NewGuid().ToString(), Name = name, Address = address.ToString("X12"),
                ModelId = model, IsApple = true };
            // Authorized while disconnected, so the monitor never issues a battery read for it.
            Field<BatteryMonitor>(controller, "battery").RegisterTargets(new List<AudioTarget> { target });
            return target;
        }

        private static void Connect(AudioTarget target, bool isDefault)
        {
            target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Id = "synthetic-" + target.ModelId, Flow = 0, State = 1 } };
            target.IsDefault = isDefault;
        }

        private static void Inject(AppController controller, AudioTarget target, int? left, int? right, int? caseLevel, bool leftCharging, bool rightCharging, bool caseCharging)
        {
            var monitor = Field<BatteryMonitor>(controller, "battery");
            var readings = Field<Dictionary<ulong, BatteryReading>>(monitor, "readings");
            ulong address = Convert.ToUInt64(target.Address, 16);
            lock (Field<object>(monitor, "gate"))
                readings[address] = new BatteryReading { Address = address, Model = (ushort)target.ModelId, Left = left, Right = right, Case = caseLevel,
                    LeftCharging = leftCharging, RightCharging = rightCharging, CaseCharging = caseCharging, Rssi = -35, Seen = DateTime.UtcNow,
                    Source = "Synthetic UI fixture; no hardware measurement" };
        }

        private static void Stage(AppController controller, ComboBox picker, List<AudioTarget> targets, AudioTarget selected, string radio)
        {
            SetField(controller, "refreshing", true);
            SetField(controller, "targets", targets);
            picker.ItemsSource = targets; picker.SelectedItem = selected;
            SetField(controller, "refreshing", false);
            SetField(controller, "radioState", radio);
            Invoke(controller, "SetStatus", "", "", false, false);
            Invoke(controller, "UpdateDevice"); Invoke(controller, "UpdateBattery");
            picker.UpdateLayout();
        }

        // ---- Helpers --------------------------------------------------------------------

        private static Brush Res(Window window, string key) { return (Brush)window.FindResource(key); }
        private static Brush Tint(Window window, string name)
        {
            var fill = ((System.Windows.Shapes.Ellipse)window.FindName(name)).Fill as SolidColorBrush;
            foreach (string key in new[] { "On", "Danger", "Off", "Accent" })
                if (fill != null && ((SolidColorBrush)window.FindResource(key)).Color == fill.Color) return Res(window, key);
            return fill;
        }
        private static void Shot(Window window, string name) { Capture((FrameworkElement)window.FindName("Root"), (FrameworkElement)window.FindName("Sheet"), name); }
        private static void ShotCard(Window card, string name) { Capture((FrameworkElement)card.FindName("Root"), (FrameworkElement)card.FindName("Sheet"), name); }
        private static void Capture(FrameworkElement root, FrameworkElement sheet, string name)
        {
            if (captureDirectory == null) return;
            root.UpdateLayout();
            var frame = new DispatcherFrame();
            root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            full.Render(root);
            Rect bounds = sheet.TransformToAncestor(root).TransformBounds(new Rect(sheet.RenderSize));
            bounds.Inflate(10, 10); bounds.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            var crop = new CroppedBitmap(full, new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
            // Composite over a desktop-like backdrop so transparent edges read correctly.
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Theme.Light ? Color.FromRgb(0xDD, 0xE3, 0xEA) : Color.FromRgb(0x10, 0x14, 0x1C)), null, new Rect(0, 0, crop.PixelWidth, crop.PixelHeight));
                context.DrawImage(crop, new Rect(0, 0, crop.PixelWidth, crop.PixelHeight));
            }
            var output = new RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            output.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
            using (var stream = File.Create(Path.Combine(captureDirectory, name + ".png"))) encoder.Save(stream);
        }

        private static void CaptureMenu(FrameworkElement menu, string name)
        {
            var frame = new DispatcherFrame();
            menu.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            int w = (int)Math.Ceiling(menu.ActualWidth) + 40, h = (int)Math.Ceiling(menu.ActualHeight) + 40;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)), null, new Rect(0, 0, w, h));
                context.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(20, 20, menu.ActualWidth, menu.ActualHeight));
            }
            var output = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            output.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
            using (var stream = File.Create(Path.Combine(captureDirectory, name + ".png"))) encoder.Save(stream);
        }

        private static void Toggle(ToggleButton check)
        {
            typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(check, null);
        }
        private static object Invoke(object owner, string name, params object[] arguments)
        {
            var method = owner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(m => m.Name == name && m.GetParameters().Count(p => !p.IsOptional) <= arguments.Length && m.GetParameters().Length >= arguments.Length);
            var parameters = method.GetParameters();
            var padded = parameters.Select((p, i) => i < arguments.Length ? arguments[i] : p.DefaultValue).ToArray();
            return method.Invoke(owner, padded);
        }
        private static T Field<T>(object owner, string name)
        {
            return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        }
        private static void SetField(object owner, string name, object value)
        {
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
        }
        private static async Task<bool> Until(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds >= timeoutMs) return false;
                await Task.Delay(30);
            }
            return true;
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
    }
}
