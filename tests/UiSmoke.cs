using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
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
    // Exercises real WPF controls and real read-only Windows discovery. Any
    // connection or power mutations below use an injected synthetic backend.
    internal static class UiSmoke
    {
        private static int checks;
        private static int result = 1;
        private static string captureDirectory;
        private static bool background;

        [STAThread]
        private static int Main(string[] args)
        {
            // Deterministic offscreen captures: the GPU may cull unchanged text
            // in an invisible window during repeated RenderTargetBitmap calls.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            foreach (string argument in args)
                if (argument == "--background") background = true;
                else captureDirectory = argument;
            if (captureDirectory != null) Directory.CreateDirectory(captureDirectory);
            AppController controller = null;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                try
                {
                    controller = new AppController(app, null, activation);
                    Window window = app.MainWindow;
                    if (background)
                    {
                        window.ShowActivated = false; window.ShowInTaskbar = false;
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = SystemParameters.VirtualScreenLeft - window.Width - 128;
                        window.Top = SystemParameters.VirtualScreenTop - window.Height - 128;
                        window.Opacity = 0; window.IsHitTestVisible = false;
                    }
                    window.Title = "Karmac - read-only UI verification";
                    AppController current = controller;
                    app.DispatcherUnhandledException += (sender, e) => {
                        Console.Error.WriteLine("FAIL Dispatcher: " + e.Exception);
                        e.Handled = true;
                        app.Shutdown();
                    };
                    app.Dispatcher.BeginInvoke(new Action(async () => {
                        try
                        {
                            await Verify(current, window, activation);
                            result = 0;
                        }
                        catch (Exception error) { Console.Error.WriteLine("FAIL UI smoke: " + error); }
                        finally { app.Shutdown(); }
                    }));
                    controller.Show();
                    app.Run();
                    Assert(app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished, "Quit completes dispatcher shutdown");
                    if (result == 0) Console.WriteLine("UI smoke passed: {0} checks; no connection or routing actions.", checks);
                }
                catch (Exception error) { result = 1; Console.Error.WriteLine("FAIL Startup: " + error); }
                finally { if (controller != null) controller.Dispose(); }
            }
            return result;
        }

        private static async Task Verify(AppController controller, Window window, EventWaitHandle activation)
        {
            Assert(await Until(() => Field<bool>(controller, "initialized") && !Field<bool>(controller, "refreshing"), 10000),
                "Real Windows discovery finishes within 10 seconds");
            VerifyBusyIndicator(controller, window);
            var picker = (ComboBox)window.FindName("DevicePicker");
            if (background)
            {
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                popup.Child.Opacity = 0; popup.Child.IsHitTestVisible = false;
            }
            Assert(picker.Items.Count > 0, "Windows found a paired AirPods audio target");
            if (picker.SelectedItem == null) picker.SelectedIndex = 0;
            // Exercise the default presentation consistently even when a user
            // saved headphones-only mode. The harness restores their settings.
            ((CheckBox)window.FindName("PowerOffBluetoothCheck")).IsChecked = true;
            await Task.Delay(150);
            window.UpdateLayout();
            AudioTarget target = picker.SelectedItem as AudioTarget;
            Assert(target != null && !String.IsNullOrWhiteSpace(target.Name), "Selected real AirPods has a nonblank Name");
            Assert(HasRenderedText(picker, target.Name), "Device picker visibly renders selected name: " + target.Name);
            var primaryConnect = (Button)window.FindName("ConnectButton");
            var readyPanel = (Border)window.FindName("ReadyPanel");
            Assert(target.Connected && target.IsDefault ? readyPanel.IsVisible && !primaryConnect.IsVisible : primaryConnect.IsEnabled,
                "Active AirPods output shows a connected success state instead of another connect button");
            var pickerSurface = (Border)picker.Template.FindName("PickerSurface", picker);
            Assert(pickerSurface != null && pickerSurface.BorderThickness == new Thickness(0) && readyPanel.BorderThickness == new Thickness(0),
                "Device selector and connected confirmation have no box outlines or underlines");
            Assert(((TextBlock)window.FindName("CopyrightLabel")).Text == "AY-Karma (c)", "App footer contains the exact requested copyright");
            Assert(((TextBlock)window.FindName("BatteryStatus")).Text != "Unavailable", "Missing battery data has an explicit reporting state");
            var windowBackground = window.Background as SolidColorBrush;
            Assert(windowBackground != null && windowBackground.Color.A == 255 && windowBackground.Color.R == 0 && windowBackground.Color.G == 0 && windowBackground.Color.B == 0,
                "Window uses an opaque pure-black background");
            Assert(IsBright(picker.Foreground), "Device picker uses bright text on black");
            var status = (Border)window.FindName("StatusPanel");
            var deviceRow = (Grid)window.FindName("DeviceNameRow");
            Assert(deviceRow != null && deviceRow.Children.Contains(picker) && deviceRow.Children.Contains(status),
                "Compact status pill shares the device-name row with the picker");
            Assert(Grid.GetColumn(status) > Grid.GetColumn(picker), "Compact status pill sits beside the selected device name");
            var statusSwitch = (ToggleButton)window.FindName("ConnectionToggle");
            Assert(statusSwitch != null && status.Child is Grid && ((Grid)status.Child).Children.Contains(statusSwitch) && Grid.GetColumn(statusSwitch) == 2,
                "Connection switch sits directly beside the connected/disconnected status");
            Assert(System.Windows.Automation.AutomationProperties.GetHelpText(statusSwitch).Contains("entire laptop"),
                "Switch explains that off affects Bluetooth devices across the laptop");
            Assert(window.FindName("BatteryLinkButton") == null && window.FindName("StatusMessage") == null,
                "Legacy beacon dialog and oversized status message remain removed");
            var detail = status.ToolTip as TextBlock;
            Assert(detail != null && !String.IsNullOrWhiteSpace(detail.Text) && detail.Text.Contains(Environment.NewLine),
                "Status tooltip retains full title and diagnostic detail");
            Assert(AirPodsModels.IsKnown(target.ModelId), "Real paired hardware supplies a recognized model ID: " + target.ModelId.ToString("X4"));
            Assert(target.ModelName == AirPodsModels.Name(target.ModelId), "Real target displays its hardware-derived model: " + target.ModelName);
            Assert(((TextBlock)window.FindName("ModelLabel")).Text == target.ModelName.ToUpperInvariant(), "Visible model badge matches selected hardware");
            var image = (Image)window.FindName("DeviceArtwork");
            Assert(image.Source != null && image.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0,
                "Selected device has visible loaded artwork");
            if (target.ModelId == 0x2027)
            {
                Assert(target.ModelName == "AirPods Pro 3", "PID 2027 is identified as AirPods Pro 3");
                var bitmap = image.Source as BitmapSource;
                Assert(bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Pro 3 uses its decoded embedded product illustration");
            }
            Capture(window, "main");
            picker.IsDropDownOpen = true;
            await Task.Delay(80);
            var selectedRow = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(picker.SelectedIndex);
            Assert(selectedRow != null && HasRenderedText(selectedRow, target.Name) && HasRenderedText(selectedRow, target.ModelName),
                "Device menu renders the saved name and accurate model on separate readable lines");
            var selectedCheck = (System.Windows.Shapes.Path)selectedRow.Template.FindName("SelectedCheck", selectedRow);
            Assert(selectedCheck.IsVisible, "Device menu marks the current pair with a visible selection check");
            var devicePopup = (Popup)picker.Template.FindName("PART_Popup", picker);
            Assert(((Border)devicePopup.Child).BorderThickness == new Thickness(0) &&
                ((Border)selectedRow.Template.FindName("ItemSurface", selectedRow)).BorderThickness == new Thickness(0),
                "Floating device menu and selected row have no box outlines");
            CaptureElement((FrameworkElement)((Border)devicePopup.Child).Child, "device-menu");
            picker.IsDropDownOpen = false;

            var settingsButton = (Button)window.FindName("SettingsButton");
            var settingsPanel = (Border)window.FindName("SettingsPanel");
            Visibility initialVisibility = settingsPanel.Visibility;
            Click(settingsButton);
            Assert(settingsPanel.Visibility != initialVisibility, "Settings button opens its panel");
            Assert(((Button)window.FindName("RouteButton")).IsVisible && ((Button)window.FindName("DisconnectButton")).IsVisible &&
                ((Button)window.FindName("RefreshButton")).IsVisible && ((Button)window.FindName("PairButton")).IsVisible,
                "Settings keeps routing, disconnection, refresh and Windows pairing accessible");
            window.UpdateLayout(); Capture(window, "settings");
            Click(settingsButton);
            Assert(settingsPanel.Visibility == initialVisibility, "Settings button closes its panel");
            if (settingsPanel.Visibility != Visibility.Visible) Click(settingsButton);

            var trayCheck = (CheckBox)window.FindName("TrayCheck");
            bool? oldTray = trayCheck.IsChecked;
            var hotkeyCheck = (CheckBox)window.FindName("HotkeyCheck");
            bool? oldHotkey = hotkeyCheck.IsChecked;
            try
            {
                // Keep verification entirely read-only even if the user presses
                // the app shortcut while this brief test window is displayed.
                if (hotkeyCheck.IsChecked == true) Toggle(hotkeyCheck);
                if (trayCheck.IsChecked != true) Toggle(trayCheck);
                Assert(Field<Preferences>(controller, "settings").CloseToTray, "Tray checkbox updates the real preference");
                Click((Button)window.FindName("CloseButton"));
                Assert(!window.IsVisible, "Close hides the app to its tray");
                if (background)
                {
                    controller.Show();
                    Assert(window.IsVisible && !window.IsActive, "Background verification reopens without activating a test window");
                }
                else
                {
                    activation.Set();
                    Assert(await Until(() => window.IsVisible, 3000), "Activation event reopens the hidden app");
                    Assert(window.WindowState == WindowState.Normal, "Reopened window has normal window state");
                }

                // The guard is tested without issuing a native driver operation.
                SetField(controller, "polling", true);
                Task guarded = (Task)typeof(AppController).GetMethod("PollStatesAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                Assert(guarded.IsCompleted, "An already-running state poll prevents another poll");
                SetField(controller, "polling", false);
            }
            finally
            {
                trayCheck.IsChecked = oldTray;
                hotkeyCheck.IsChecked = oldHotkey;
            }
            await VerifyColdSavedPair(controller, window, picker, target);
            await VerifyPresentationStates(controller, window, picker, target);
            await VerifyManualBatterySource(controller, window, target);
            await VerifyConnectionStream(controller, window, target);
            await VerifyArtworkMotion(controller, window, target);
            await VerifyAppleAudioCatalog(controller, window, picker, target);
            // Exercise the app's real quit path. Main then disposes tray, watcher,
            // timers, registered wait and hotkey exactly as the application does.
            Click((Button)window.FindName("QuitButton"));
            Assert(Field<bool>(controller, "exiting") && !Field<DispatcherTimer>(controller, "timer").IsEnabled,
                "Quit marks the app exiting and stops its timer");
        }

        private static async Task VerifyColdSavedPair(AppController controller, Window window, ComboBox picker, AudioTarget target)
        {
            Field<DispatcherTimer>(controller, "timer").Stop();
            Invoke(controller, "ShowAirPods");
            var original = Field<Func<Task<DiscoveryResult>>>(controller, "discoverDevices");
            var targets = Field<List<AudioTarget>>(controller, "targets");
            string radio = Field<string>(controller, "radioState");
            try
            {
                SetField(controller, "discoverDevices", new Func<Task<DiscoveryResult>>(() => Task.FromResult(new DiscoveryResult { Targets = targets, Radio = "On" })));
                await (Task)Invoke(controller, "RefreshAsync", true);
                SetField(controller, "discoverDevices", new Func<Task<DiscoveryResult>>(() => Task.FromResult(new DiscoveryResult { Targets = new List<AudioTarget>(), Radio = "Off" })));
                await (Task)Invoke(controller, "RefreshAsync", true);
                Assert(picker.SelectedItem is AudioTarget && DeviceDiscovery.SameGuid(((AudioTarget)picker.SelectedItem).ContainerId, target.ContainerId),
                    "A transient empty cold-start query retains the saved AirPods selection");
                Assert(((Button)window.FindName("ConnectButton")).IsVisible && ((Button)window.FindName("ConnectButton")).IsEnabled,
                    "Cold Bluetooth-off discovery retains a one-click connect action");
                var restored = (AudioTarget)picker.SelectedItem;
                Assert(restored.RequiresPairingCheck && restored.Endpoints.Count == 0 && !restored.Connected,
                    "Restored identity is a verification-required hint, never a fabricated live connection");
            }
            finally
            {
                SetField(controller, "discoverDevices", original);
                SetField(controller, "refreshing", true); SetField(controller, "targets", targets); SetField(controller, "radioState", radio);
                picker.ItemsSource = targets; picker.SelectedItem = target; SetField(controller, "refreshing", false);
                Field<BatteryMonitor>(controller, "battery").RegisterTargets(targets);
                Invoke(controller, "UpdateDevice");
            }
        }

        private static async Task VerifyPresentationStates(AppController controller, Window window, ComboBox picker, AudioTarget target)
        {
            // Presentation fixtures only: stop polling and never invoke a driver,
            // route, pairing action, shortcut, or synthetic device registration.
            Field<DispatcherTimer>(controller, "timer").Stop();
            var panel = (Border)window.FindName("SettingsPanel");
            if (panel.Visibility == Visibility.Visible) Click((Button)window.FindName("BackButton"));
            var source = picker.ItemsSource;
            var originalTargets = Field<List<AudioTarget>>(controller, "targets");
            double oldWidth = window.Width, oldHeight = window.Height;
            string originalRadio = Field<string>(controller, "radioState");
            try
            {
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await Task.Delay(80);
                window.UpdateLayout();
                var connect = (Button)window.FindName("ConnectButton");
                var ready = (Border)window.FindName("ReadyPanel");
                Assert(InViewport(ready.IsVisible ? (FrameworkElement)ready : connect, window), "Primary action or connected state remains fully visible at the minimum window size");
                Assert(InViewport((TextBlock)window.FindName("CopyrightLabel"), window), "Copyright remains at the bottom of the minimum window");
                var status = (Border)window.FindName("StatusPanel");
                Rect pickerBounds = picker.TransformToAncestor(window).TransformBounds(new Rect(picker.RenderSize));
                Rect statusBounds = status.TransformToAncestor(window).TransformBounds(new Rect(status.RenderSize));
                Assert(picker.ActualWidth >= 160 && !pickerBounds.IntersectsWith(statusBounds),
                    "Compact status leaves room for the device name without overlap at minimum width");
                Capture(window, "minimum-size");

                SetField(controller, "refreshing", true);
                var disconnected = new AudioTarget { ContainerId = target.ContainerId, Name = target.Name,
                    Address = target.Address, ModelId = target.ModelId, IsApple = true };
                picker.ItemsSource = new List<AudioTarget> { disconnected }; picker.SelectedItem = disconnected;
                SetField(controller, "refreshing", false);
                SetField(controller, "radioState", "On");
                Invoke(controller, "UpdateDevice");
                await Task.Delay(80); window.UpdateLayout();
                Assert(connect.IsEnabled && ((TextBlock)window.FindName("ConnectionBadge")).Text == "Disconnected",
                    "Saved disconnected AirPods retain an enabled connection action and explicit disconnected state");
                var mainDisconnect = (Button)window.FindName("MainDisconnectButton");
                var connectionToggle = (ToggleButton)window.FindName("ConnectionToggle");
                Assert(connectionToggle.IsVisible && connectionToggle.IsChecked == true && !mainDisconnect.IsVisible,
                    "Bluetooth-on switch remains on for disconnected AirPods without a duplicate Disconnect button");
                Assert(HasRenderedText(picker, target.Name), "Disconnected view retains the selected AirPods name");
                var dot = ((System.Windows.Shapes.Ellipse)window.FindName("ConnectionDot")).Fill as SolidColorBrush;
                Assert(dot != null && dot.Color == (Color)ColorConverter.ConvertFromString("#F87171"),
                    "Disconnected state has the requested red indicator");
                Capture(window, "disconnected-fixture");

                SetField(controller, "radioState", "Off");
                Invoke(controller, "UpdateDevice");
                Assert(connectionToggle.IsChecked == false && connectionToggle.IsEnabled,
                    "Bluetooth-off switch is off and ready for one-click connection");
                Assert(connect.IsVisible && connect.IsEnabled && connect.Content.ToString() == "Connect AirPods" &&
                    !((Button)window.FindName("SetupButton")).IsVisible,
                    "Bluetooth-off state keeps the one-click connect action available");
                Assert(((TextBlock)window.FindName("DeviceSubtitle")).Text.Contains("automatically"),
                    "Bluetooth-off hint explains automatic power-on");
                Capture(window, "bluetooth-off-fixture");
                disconnected.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 }); disconnected.IsDefault = true;
                Invoke(controller, "UpdateDevice");
                Assert(connect.IsVisible && !ready.IsVisible && ((TextBlock)window.FindName("ConnectionBadge")).Text == "Disconnected",
                    "Stale active endpoints cannot hide connect while Bluetooth is off");
                disconnected.Endpoints.Clear(); disconnected.IsDefault = false;
                SetField(controller, "busy", true); Invoke(controller, "UpdateDevice");
                Assert(!connect.IsEnabled && !connectionToggle.IsEnabled, "Radio transition keeps repeat connect and switch clicks disabled");
                SetField(controller, "busy", false); SetField(controller, "radioState", "No adapter"); Invoke(controller, "UpdateDevice");
                Assert(!connect.IsVisible && ((Button)window.FindName("SetupButton")).IsVisible,
                    "Missing hardware keeps a clear setup state");
                Assert(!connectionToggle.IsEnabled && connectionToggle.IsChecked == false,
                    "No-adapter switch is disabled and off");
                SetField(controller, "radioState", "On"); Invoke(controller, "UpdateDevice");

                Invoke(controller, "SetStatus", "Audio route needs attention", "Example diagnostic detail for visual review. No hardware request was sent.", false, true);
                await Task.Delay(80);
                window.UpdateLayout();
                Assert(InViewport(status, window) && InViewport(connect, window),
                    "An error state preserves visible connection feedback and the main action");
                Assert(connectionToggle.IsChecked == true,
                    "An audio or pairing error cannot turn the Bluetooth-on switch off");
                if (!background)
                {
                    status.Focus();
                    var keyboardTip = Field<ToolTip>(controller, "keyboardStatusTip");
                    Assert(keyboardTip != null && keyboardTip.IsOpen &&
                        ((TextBlock)keyboardTip.Content).Text.Contains("No hardware request was sent."),
                        "Keyboard focus exposes complete error details without requiring a mouse");
                    ((Button)window.FindName("SettingsButton")).Focus();
                }
                Capture(window, "error-fixture");

                window.Width = oldWidth; window.Height = oldHeight;
                disconnected.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 });
                disconnected.IsDefault = false;
                Invoke(controller, "UpdateDevice");
                Assert(connect.IsVisible && connect.Content.ToString() == "Switch audio to AirPods" && !ready.IsVisible,
                    "Connected AirPods that are not the output offer an explicit audio-switch action");
                Assert(connectionToggle.IsVisible && connectionToggle.IsChecked == true && connectionToggle.IsEnabled && !mainDisconnect.IsVisible,
                    "Connected AirPods expose the Bluetooth switch beside status without a duplicate Disconnect button");
                disconnected.IsDefault = true;
                Invoke(controller, "UpdateDevice");
                Assert(ready.IsVisible && !connect.IsVisible,
                    "Already-routed AirPods display a non-clickable connected confirmation");
                window.UpdateLayout();
                Assert(connectionToggle.IsChecked == true && connectionToggle.IsEnabled && InViewport(connectionToggle, window),
                    "Active AirPods output keeps an accessible on switch beside its status");
                Capture(window, "connected-main-actions-fixture");
                ((UniformGrid)window.FindName("BatteryLevels")).Visibility = Visibility.Visible;
                Invoke(controller, "BatteryCell", "Left", (int?)80, false, true);
                Invoke(controller, "BatteryCell", "Right", (int?)20, true, false);
                Invoke(controller, "BatteryCell", "Case", (int?)60, false, false);
                await Task.Delay(80);
                window.UpdateLayout(); Capture(window, "battery-fixture");

                picker.SelectedIndex = -1;
                Invoke(controller, "UpdateDevice");
                await Task.Delay(80);
                var setup = (Button)window.FindName("SetupButton");
                Assert(setup.IsVisible && setup.IsEnabled && setup.Content.ToString().IndexOf("Choose", StringComparison.OrdinalIgnoreCase) >= 0,
                    "A missing selection offers the already-paired AirPods before new pairing");
                Click(setup);
                Assert(picker.IsDropDownOpen, "Choose AirPods opens the device picker without opening Windows Settings");
                picker.IsDropDownOpen = false;
                SetField(controller, "targets", new List<AudioTarget>());
                picker.ItemsSource = new List<AudioTarget>();
                Invoke(controller, "UpdateDevice");
                await Task.Delay(80);
                Assert(setup.IsVisible && setup.IsEnabled && !connect.IsVisible &&
                    setup.Content.ToString().IndexOf("Pair", StringComparison.OrdinalIgnoreCase) >= 0,
                    "No saved pair exposes Windows pairing through the primary action");
                window.UpdateLayout(); Capture(window, "no-selection-fixture");
            }
            finally
            {
                SetField(controller, "refreshing", true);
                SetField(controller, "radioState", originalRadio); SetField(controller, "busy", false);
                SetField(controller, "targets", originalTargets);
                picker.ItemsSource = source; picker.SelectedItem = target;
                SetField(controller, "refreshing", false);
                window.Width = oldWidth; window.Height = oldHeight;
                Invoke(controller, "UpdateDevice"); Invoke(controller, "UpdateBattery");
            }
        }

        private static async Task VerifyManualBatterySource(AppController controller, Window window, AudioTarget target)
        {
            var monitor = Field<BatteryMonitor>(controller, "battery");
            var readings = Field<Dictionary<ulong, BatteryReading>>(monitor, "readings");
            var gate = Field<object>(monitor, "gate");
            const ulong first = 0xAABBCCDDEE01, second = 0xAABBCCDDEE02;
            try
            {
                // Synthetic broadcasts exercise selection without assigning any real
                // nearby person's beacon or changing a Bluetooth/audio connection.
                lock (gate)
                {
                    readings[first] = new BatteryReading { Address = first, Model = (ushort)target.ModelId, Rssi = -30,
                        Seen = DateTime.UtcNow, Left = 80, Right = 70, Case = 60 };
                    readings[second] = new BatteryReading { Address = second, Model = (ushort)target.ModelId, Rssi = -35,
                        Seen = DateTime.UtcNow, Left = 50, Right = 40, Case = 30 };
                }
                Click((Button)window.FindName("BatterySourceButton"));
                var panel = (Border)window.FindName("BatterySourcePanel");
                var list = (ListBox)window.FindName("BatterySourcesList");
                var use = (Button)window.FindName("UseBatterySourceButton");
                await Task.Delay(80); window.UpdateLayout();
                Assert(panel.IsVisible && !((StackPanel)window.FindName("MainPanel")).IsVisible,
                    "Optional battery source opens inline without a dropdown box");
                Assert(list.Items.Count >= 2 && list.SelectedItem == null && !use.IsEnabled,
                    "Nearby readings require explicit source selection");
                foreach (BatterySourceOption option in list.Items)
                    if (option.Reading.Address == first) list.SelectedItem = option;
                Assert(use.IsEnabled, "Selecting a source enables the confirmation action");
                Capture(window, "manual-source-synthetic-fixture");
                Assert(InViewport(use, window), "Source confirmation is visible at the default window size");
                Click(use);
                var chosen = monitor.ForTarget(target);
                Assert(!panel.IsVisible && chosen != null && chosen.UserSelected && chosen.Left == 80,
                    "Explicit selection returns to AirPods with a labelled manual reading");
                var overall = monitor.GetDeviceBattery(target);
                if (overall == null && !target.Battery.HasValue)
                    Assert(((TextBlock)window.FindName("BatteryStatus")).Text == "User-selected" &&
                        ((TextBlock)window.FindName("BatteryNote")).Text.Contains("not verified"),
                        "Manual battery display clearly states that identity is not verified");
                Capture(window, "manual-battery-synthetic-fixture");
                Click((Button)window.FindName("BatterySourceButton"));
                Click((Button)window.FindName("AutomaticBatteryButton"));
                chosen = monitor.ForTarget(target);
                Assert(!panel.IsVisible && (chosen == null || !chosen.UserSelected),
                    "Automatic-only action clears the optional manual association");
            }
            finally
            {
                monitor.ClearSource(target);
                lock (gate) { readings.Remove(first); readings.Remove(second); }
                Invoke(controller, "ShowAirPods"); Invoke(controller, "UpdateBattery");
            }
        }

        private sealed class StreamFixturePlatform : IConnectionPlatform
        {
            internal readonly TaskCompletionSource<bool> PairGate = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<bool> RouteGate = new TaskCompletionSource<bool>();
            internal int Requests;
            internal bool RejectPair;
            internal bool RejectOff;
            internal int PowerOffRequests;
            internal int DisconnectRequests;
            internal bool RejectDisconnect, Disconnected;
            internal string DisconnectedContainer;
            internal string Radio = "Off";
            public Task<bool> PowerOnAsync(Action starting) { starting(); Radio = "On"; return Task.FromResult(true); }
            public Task PowerOffAsync()
            {
                PowerOffRequests++;
                if (RejectOff) throw new InvalidOperationException("Synthetic adapter denied Bluetooth power-off.");
                Radio = "Off";
                return Task.FromResult(true);
            }
            public Task<string> ReadRadioStateAsync() { return Task.FromResult(Radio); }
            public Task DisconnectAsync(AudioTarget target)
            {
                DisconnectRequests++; DisconnectedContainer = target.ContainerId;
                if (RejectDisconnect) throw new InvalidOperationException("Synthetic selected-pair disconnect rejected.");
                Disconnected = true;
                return Task.FromResult(true);
            }
            public async Task VerifyPairAsync(AudioTarget target)
            {
                await PairGate.Task;
                if (RejectPair) throw new InvalidOperationException("Windows could not confirm this saved pair. Pair your AirPods in Windows if the pairing was removed.");
                target.RequiresPairingCheck = false;
            }
            public Task RefreshAudioAsync(AudioTarget target)
            {
                target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Id = "synthetic-output", ControllerId = @"{2}.\\?\bth#fixture", Flow = 0, State = Requests >= 2 ? 1 : 8 } };
                return Task.FromResult(true);
            }
            public Task ReadAudioAsync(AudioTarget target)
            { foreach (var item in target.Endpoints) item.State = Disconnected ? 8 : RejectDisconnect || Requests >= 2 ? 1 : 8; return Task.FromResult(true); }
            public Task RequestAsync(AudioTarget target) { Requests++; return Task.FromResult(true); }
            public async Task RouteAsync(AudioTarget target) { await RouteGate.Task; }
            public Task DelayAsync(int milliseconds) { return Task.Delay(10); }
        }

        private static async Task VerifyConnectionStream(AppController controller, Window window, AudioTarget target)
        {
            // Real app orchestration, synthetic backend: no radio, connection or
            // output writes. Hold stages to inspect their actual streamed controls.
            var backend = new StreamFixturePlatform();
            var original = Field<IConnectionPlatform>(controller, "connectionPlatform");
            var discovery = Field<Func<Task<DiscoveryResult>>>(controller, "discoverDevices");
            var oldEndpoints = target.Endpoints; bool oldDefault = target.IsDefault;
            string radio = Field<string>(controller, "radioState");
            var trace = Field<ConnectionTrace>(controller, "connectionTrace");
            var powerOffCheck = (CheckBox)window.FindName("PowerOffBluetoothCheck");
            bool? oldPowerOff = powerOffCheck.IsChecked;
            double width = window.Width, height = window.Height;
            var lateDiscovery = new TaskCompletionSource<DiscoveryResult>();
            Task refresh = null;
            try
            {
                powerOffCheck.IsChecked = true;
                SetField(controller, "connectionPlatform", backend);
                SetField(controller, "discoverDevices", new Func<Task<DiscoveryResult>>(() => lateDiscovery.Task));
                target.Endpoints = new List<AudioEndpoint>(); target.IsDefault = false;
                SetField(controller, "radioState", "Off"); Invoke(controller, "ShowAirPods");
                refresh = (Task)Invoke(controller, "RefreshAsync", true);
                var connect = (Button)window.FindName("ConnectButton");
                Assert(connect.IsEnabled, "Saved pair can connect while a slow discovery query is still running");
                var connectionToggle = (ToggleButton)window.FindName("ConnectionToggle");
                Invoke(controller, "UpdateDevice");
                Assert(connectionToggle.IsChecked == false && connectionToggle.IsEnabled,
                    "Cold-start switch is off and enabled before a synthetic power-on");
                Toggle(connectionToggle);
                Assert(Field<bool>(controller, "busy") && trace.Running && trace.Steps[1].IsRunning,
                    "One click immediately streams Bluetooth and saved-pair verification");
                Assert(connectionToggle.IsChecked == true && !connectionToggle.IsEnabled,
                    "Switch follows powered-on radio while pairing is pending and blocks overlapping clicks");
                Assert(((StackPanel)window.FindName("ConnectionStream")).IsVisible && !((Border)window.FindName("BatterySection")).IsVisible && !connect.IsEnabled,
                    "Live stages replace secondary battery content and prevent overlapping requests");
                lateDiscovery.SetResult(new DiscoveryResult { Targets = new List<AudioTarget>(), Radio = "Off" });
                await refresh;
                Assert(((ComboBox)window.FindName("DevicePicker")).SelectedItem == target && Field<string>(controller, "radioState") == "On",
                    "Late cold-start discovery cannot overwrite a connection already in progress");
                backend.PairGate.SetResult(true);
                Assert(await Until(() => backend.Requests == 1, 2000), "Audio-link request follows pairing verification");
                window.UpdateLayout(); Capture(window, "connection-stream-synthetic-fixture");
                Assert(InViewport((ItemsControl)window.FindName("ConnectionSteps"), window) &&
                    InViewport((TextBlock)window.FindName("CopyrightLabel"), window), "Live timeline and footer remain fully visible");
                Assert(await Until(() => trace.Steps[3].IsRunning, 3000) && backend.Requests == 2,
                    "Accepted requests are retried until actual playback is ready before output verification");
                window.UpdateLayout(); Capture(window, "connection-output-synthetic-fixture");
                backend.RouteGate.SetResult(true);
                Assert(await Until(() => !Field<bool>(controller, "busy"), 2000), "Output completion finishes the connection action");
                Assert(trace.Steps[3].State == ConnectionStepState.Complete && !((StackPanel)window.FindName("ConnectionStream")).IsVisible &&
                    ((Button)window.FindName("ConnectionDetailsButton")).IsVisible && ((Border)window.FindName("BatterySection")).IsVisible,
                    "Completed timings collapse into a small details control and restore battery information");
                Click((Button)window.FindName("ConnectionDetailsButton")); window.UpdateLayout();
                Assert(((StackPanel)window.FindName("ConnectionStream")).IsVisible, "Completed connection stages can be inspected again");
                Capture(window, "connection-complete-synthetic-fixture");
                var failed = new StreamFixturePlatform { RejectPair = true }; failed.PairGate.SetResult(true);
                SetField(controller, "connectionPlatform", failed);
                target.Endpoints = new List<AudioEndpoint>(); target.IsDefault = false;
                await (Task)Invoke(controller, "ConnectAsync", true);
                Assert(trace.Failed && trace.Steps[1].State == ConnectionStepState.Failed && failed.Requests == 0 &&
                    ((StackPanel)window.FindName("ConnectionStream")).IsVisible,
                    "A pairing failure stays visible on the exact stage without sending a connection request");
                Assert(connectionToggle.IsChecked == true && connectionToggle.IsEnabled && Field<string>(controller, "radioState") == "On",
                    "Failed AirPods connection leaves the switch on when Bluetooth is actually on");
                window.UpdateLayout(); Capture(window, "connection-failure-synthetic-fixture");
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Assert(InViewport(connect, window) && InViewport((TextBlock)window.FindName("CopyrightLabel"), window),
                    "Retry action and footer stay accessible at minimum size after a connection failure");
                window.Width = width; window.Height = height;
                target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Flow = 0, State = 1 } };
                target.IsDefault = true; Invoke(controller, "UpdateDevice");
                Toggle(connectionToggle);
                Assert(await Until(() => !Field<bool>(controller, "busy") && failed.PowerOffRequests == 1, 2000),
                    "Switch-off dispatches exactly one synthetic laptop Bluetooth power-off request");
                bool endpointActive = false;
                foreach (var endpoint in target.Endpoints) if (endpoint.Active) endpointActive = true;
                Assert(Field<string>(controller, "radioState") == "Off" && connectionToggle.IsChecked == false &&
                    !endpointActive && !target.IsDefault,
                    "Successful power-off turns the switch off and invalidates old active playback state");
                Assert(failed.Requests == 0,
                    "Switch-off uses radio power control without issuing an AirPods reconnect request");
                window.UpdateLayout(); Capture(window, "switch-off-synthetic-fixture");

                failed.Radio = "On"; failed.RejectOff = true;
                SetField(controller, "radioState", "On"); Invoke(controller, "UpdateDevice");
                Toggle(connectionToggle);
                Assert(await Until(() => !Field<bool>(controller, "busy") && failed.PowerOffRequests == 2, 2000),
                    "A rejected radio power-off settles without leaving the switch busy");
                Assert(connectionToggle.IsChecked == true && connectionToggle.IsEnabled && Field<string>(controller, "radioState") == "On" &&
                    ((TextBlock)window.FindName("StatusTitle")).IsVisible &&
                    ((TextBlock)((Border)window.FindName("StatusPanel")).ToolTip).Text.Contains("Synthetic adapter denied"),
                    "Denied power-off restores the actual on state and exposes the adapter error");
                window.UpdateLayout(); Capture(window, "switch-off-denied-synthetic-fixture");

                powerOffCheck.IsChecked = false;
                var selectedOnly = new StreamFixturePlatform { Radio = "On" };
                SetField(controller, "connectionPlatform", selectedOnly);
                target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Id = "synthetic-render", Flow = 0, State = 1 },
                    new AudioEndpoint { Id = "synthetic-microphone", Flow = 1, State = 1 } };
                target.IsDefault = true; Invoke(controller, "UpdateDevice");
                Assert(!Field<Preferences>(controller, "settings").PowerOffBluetoothOnDisconnect &&
                    ((Button)window.FindName("DisconnectButton")).Content.ToString() == "Disconnect headphones" &&
                    System.Windows.Automation.AutomationProperties.GetHelpText(connectionToggle).Contains("only the selected headphones"),
                    "Optional setting updates saved preference, disconnect action and accessible switch explanation");
                Toggle(connectionToggle);
                Assert(await Until(() => !Field<bool>(controller, "busy") && selectedOnly.DisconnectRequests == 1, 2000),
                    "Optional switch-off dispatches one selected-headphone audio disconnect");
                Assert(selectedOnly.PowerOffRequests == 0 && selectedOnly.DisconnectedContainer == target.ContainerId &&
                    selectedOnly.Radio == "On" && Field<string>(controller, "radioState") == "On" && connectionToggle.IsChecked == false &&
                    !target.Endpoints.Exists(endpoint => endpoint.Active) && !target.IsDefault,
                    "Headphones-only mode confirms render and capture inactive while leaving Bluetooth on and switch off");
                window.UpdateLayout(); Capture(window, "headphones-only-disconnected-synthetic-fixture");
                selectedOnly.Disconnected = false; selectedOnly.PairGate.SetResult(true); selectedOnly.RouteGate.SetResult(true);
                Toggle(connectionToggle);
                Assert(await Until(() => !Field<bool>(controller, "busy") && selectedOnly.Requests >= 2, 3000) &&
                    connectionToggle.IsChecked == true && target.Connected,
                    "Optional switch-on reconnects the selected headphones even though Bluetooth was already on");

                selectedOnly.RejectDisconnect = true;
                Toggle(connectionToggle);
                Assert(await Until(() => !Field<bool>(controller, "busy") && selectedOnly.DisconnectRequests == 2, 2000) &&
                    connectionToggle.IsChecked == true && selectedOnly.PowerOffRequests == 0 &&
                    ((TextBlock)((Border)window.FindName("StatusPanel")).ToolTip).Text.Contains("Synthetic selected-pair"),
                    "Rejected selected disconnect keeps actual connected state and never falls back to powering off Bluetooth");

                Invoke(controller, "ToggleSettings"); window.UpdateLayout(); Capture(window, "settings-headphones-only");
                Assert(powerOffCheck.IsVisible && powerOffCheck.IsChecked == false,
                    "Disconnect scope is discoverable in the app's existing settings view");
                powerOffCheck.IsChecked = true;
                window.UpdateLayout(); Capture(window, "settings-default-synthetic-fixture");
                Invoke(controller, "ShowAirPods");
            }
            finally
            {
                if (!lateDiscovery.Task.IsCompleted) lateDiscovery.TrySetResult(new DiscoveryResult { Targets = new List<AudioTarget>(), Radio = radio });
                backend.PairGate.TrySetResult(true); backend.RouteGate.TrySetResult(true);
                powerOffCheck.IsChecked = oldPowerOff;
                SetField(controller, "stateRevision", Field<long>(controller, "stateRevision") + 1);
                SetField(controller, "refreshing", false);
                SetField(controller, "connectionPlatform", original); SetField(controller, "discoverDevices", discovery);
                target.Endpoints = oldEndpoints; target.IsDefault = oldDefault;
                trace.Reset(); SetField(controller, "radioState", radio);
                window.Width = width; window.Height = height; Invoke(controller, "UpdateDevice"); Invoke(controller, "UpdateBattery");
            }
        }

        private static void VerifyBusyIndicator(AppController controller, Window window)
        {
            var bar = (ProgressBar)window.FindName("BusyBar");
            Invoke(controller, "SetStatus", "Idle", "Idle animation regression check", false, false);
            Assert(!bar.IsIndeterminate, "Idle status releases the busy indicator animation");
            Invoke(controller, "SetStatus", "Working", "Synthetic progress; no hardware mutation", true, false);
            Assert(bar.IsIndeterminate == SystemParameters.ClientAreaAnimation,
                "Visible progress follows the Windows animation preference");
            window.Hide();
            Assert(!bar.IsIndeterminate, "Tray-hidden progress stops the busy indicator animation");
            window.Show();
            Assert(bar.IsIndeterminate == SystemParameters.ClientAreaAnimation,
                "Showing in-progress work restores the busy indicator animation");
            window.WindowState = WindowState.Minimized;
            Assert(!bar.IsIndeterminate, "Minimized progress stops the busy indicator animation");
            window.WindowState = WindowState.Normal;
            Assert(bar.IsIndeterminate == SystemParameters.ClientAreaAnimation,
                "Restoring in-progress work restores the busy indicator animation");
            Invoke(controller, "SetStatus", "Ready", "Regression check complete", false, false);
            window.Hide(); window.Show();
            Assert(!bar.IsIndeterminate, "Showing an idle window does not restart the busy indicator animation");
        }

        private static async Task VerifyArtworkMotion(AppController controller, Window window, AudioTarget target)
        {
            var motion = Field<ArtworkMotion>(controller, "artworkMotion");
            var endpoints = target.Endpoints; string radio = Field<string>(controller, "radioState");
            try
            {
                Invoke(controller, "ShowAirPods");
                SetField(controller, "radioState", "On");
                target.Endpoints = new List<AudioEndpoint> { new AudioEndpoint { Flow = 0, State = 1 } };
                Invoke(controller, "UpdateDevice");
                Assert(motion.Floating == SystemParameters.ClientAreaAnimation,
                    "Connected artwork follows the Windows animation preference");
                if (SystemParameters.ClientAreaAnimation)
                {
                    await Task.Delay(320);
                    double current = motion.Transform.Y;
                    Assert(current < -0.01 && current >= -3.01, "Connected artwork floats subtly within three pixels");
                    target.Endpoints[0].State = 8; Invoke(controller, "UpdateDevice");
                    Assert(!motion.Floating && Math.Abs(motion.Transform.Y - current) < 0.03,
                        "Disconnect begins its settle from the current position without jumping");
                    await Task.Delay(520);
                    Assert(Math.Abs(motion.Transform.Y) < 0.01 && !motion.Transform.HasAnimatedProperties,
                        "Disconnected artwork settles at rest and releases the animation clock");
                    target.Endpoints[0].State = 1; Invoke(controller, "UpdateDevice"); await Task.Delay(180);
                    target.Endpoints[0].State = 8; Invoke(controller, "UpdateDevice");
                    target.Endpoints[0].State = 1; Invoke(controller, "UpdateDevice"); await Task.Delay(600);
                    Assert(motion.Floating && motion.Transform.HasAnimatedProperties,
                        "Rapid reconnect supersedes an old settle callback and preserves the new animation");
                    Invoke(controller, "ToggleSettings");
                    Assert(!motion.Floating && !motion.Transform.HasAnimatedProperties && motion.Transform.Y == 0,
                        "Hidden artwork releases its animation clock while settings are open");
                    Invoke(controller, "ShowAirPods");
                    Assert(motion.Floating, "Returning to the device view resumes connected artwork");
                }
                motion.Update(true, false);
                Assert(!motion.Floating && !motion.Transform.HasAnimatedProperties && motion.Transform.Y == 0,
                    "Reduced-motion mode leaves artwork stationary without an active clock");
            }
            finally
            {
                target.Endpoints = endpoints; SetField(controller, "radioState", radio);
                Invoke(controller, "ShowAirPods"); Invoke(controller, "UpdateDevice");
            }
        }

        private static async Task VerifyAppleAudioCatalog(AppController controller, Window window, ComboBox picker, AudioTarget target)
        {
            // These are presentation fixtures, not claims of hardware support or
            // measured connection speed. They never connect, pair, route, power a
            // radio, or request a thumbnail from a fabricated Bluetooth identity.
            Field<DispatcherTimer>(controller, "timer").Stop();
            var source = picker.ItemsSource;
            var originalTargets = Field<List<AudioTarget>>(controller, "targets");
            var monitor = Field<BatteryMonitor>(controller, "battery");
            var readings = Field<Dictionary<ulong, BatteryReading>>(monitor, "readings");
            object gate = Field<object>(monitor, "gate");
            string radio = Field<string>(controller, "radioState");
            var models = AirPodsModels.KnownIds;
            var sources = new Dictionary<string, ImageSource>();
            var injected = new List<ulong>();
            try
            {
                Invoke(controller, "ShowAirPods");
                SetField(controller, "radioState", "On");
                foreach (int model in models)
                {
                    Assert(AirPodsModels.IsKnown(model), "Catalog presentation recognizes " + AirPodsModels.Name(model));
                    ulong address = 0xAABBCC000000UL + (ulong)model;
                    var fixture = new AudioTarget { ContainerId = Guid.NewGuid().ToString(), Name = AirPodsModels.Name(model),
                        Address = address.ToString("X12"), ModelId = model, IsApple = true };
                    // Authorize the synthetic snapshot while disconnected so the
                    // monitor cannot issue any GATT/native battery read for it.
                    monitor.RegisterTargets(new List<AudioTarget> { fixture });
                    fixture.Endpoints.Add(new AudioEndpoint { Flow = 0, State = 1 }); fixture.IsDefault = true;
                    bool components = model == 0x2002 || model == 0x200F || model == 0x2013 || model == 0x2019 ||
                        model == 0x201B || model == 0x200E || model == 0x2014 || model == 0x2024 || model == 0x2027;
                    if (components)
                    {
                        lock (gate) readings[address] = new BatteryReading { Address = address, Model = (ushort)model,
                            Left = 80, Right = 70, Case = 60, Rssi = -35, Seen = DateTime.UtcNow,
                            Source = "Synthetic UI presentation fixture; no hardware measurement" };
                        injected.Add(address);
                    }
                    else fixture.Battery = 86; // Example Windows overall reading only.
                    SetField(controller, "refreshing", true);
                    SetField(controller, "targets", new List<AudioTarget> { fixture });
                    picker.ItemsSource = new List<AudioTarget> { fixture }; picker.SelectedItem = fixture;
                    SetField(controller, "refreshing", false);
                    Invoke(controller, "SetStatus", "Presentation preview", "Example model presentation only. No connection or battery hardware measurement.", false, false);
                    Invoke(controller, "UpdateDevice"); Invoke(controller, "UpdateBattery");
                    await Task.Delay(70); window.UpdateLayout();
                    var image = (Image)window.FindName("DeviceArtwork");
                    Assert(image.Source != null && image.ActualHeight > 0 && image.ActualWidth > 0 &&
                        ((TextBlock)window.FindName("ModelLabel")).Text == fixture.ModelName.ToUpperInvariant(),
                        "Synthetic " + fixture.ModelName + " shows its own model label and artwork");
                    Assert(await Until(() => image.Source is System.Windows.Media.Imaging.BitmapSource, 2000),
                        fixture.ModelName + " has a loaded high-resolution family render instead of a flat vector");
                    sources[AirPodsModels.ArtworkKey(model)] = image.Source;
                    var levels = (UniformGrid)window.FindName("BatteryLevels");
                    if (components)
                        Assert(levels.IsVisible && levels.Columns == 3 && ((TextBlock)window.FindName("LeftBattery")).Text == "80%" &&
                            ((Border)window.FindName("RightBatteryCell")).IsVisible && ((Border)window.FindName("CaseBatteryCell")).IsVisible,
                            "Synthetic " + fixture.ModelName + " presents separate left, right and case charge");
                    else
                        Assert(levels.IsVisible && levels.Columns == 1 && ((TextBlock)window.FindName("LeftBattery")).Text == "86%" &&
                            !((Border)window.FindName("RightBatteryCell")).IsVisible && !((Border)window.FindName("CaseBatteryCell")).IsVisible,
                            "Synthetic " + fixture.ModelName + " shows only its Windows overall charge without invented components");
                    var subtitle = (TextBlock)window.FindName("DeviceSubtitle");
                    subtitle.Text = "Presentation preview · example charge · no hardware speed test";
                    subtitle.Visibility = Visibility.Visible;
                    Capture(window, "catalog-" + AirPodsModels.ArtworkKey(model) + "-synthetic-fixture");
                    ImageSource cached = image.Source;
                    Invoke(controller, "UpdateDevice");
                    Assert(Object.ReferenceEquals(cached, image.Source), "Repeated model rendering reuses cached artwork for " + fixture.ModelName);
                }
                Assert(!Object.ReferenceEquals(sources["airpods-1"], sources["airpods-4"]) &&
                    !Object.ReferenceEquals(sources["airpods-4"], sources["airpods-pro-2-usbc"]) &&
                    !Object.ReferenceEquals(sources["beats-studio-pro"], sources["airpods-max-usbc"]) &&
                    !Object.ReferenceEquals(sources["beats-flex"], sources["powerbeats-pro-2"]),
                    "Long stems, short stems, silicone tips, headband shapes and neckband use distinct original fallback artwork");
            }
            finally
            {
                lock (gate) foreach (ulong address in injected) readings.Remove(address);
                monitor.RegisterTargets(originalTargets);
                SetField(controller, "refreshing", true); SetField(controller, "targets", originalTargets);
                picker.ItemsSource = source; picker.SelectedItem = target;
                SetField(controller, "refreshing", false); SetField(controller, "radioState", radio);
                Invoke(controller, "UpdateDevice"); Invoke(controller, "UpdateBattery");
            }
        }

        private static object Invoke(object owner, string name, params object[] arguments)
        {
            return owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, arguments);
        }
        private static bool InViewport(FrameworkElement element, Window window)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
        }
        private static void Capture(Window window, string name)
        {
            if (captureDirectory == null) return;
            var root = (FrameworkElement)window.Content;
            CaptureElement(root, name);
        }
        private static void CaptureElement(FrameworkElement root, string name)
        {
            if (captureDirectory == null) return;
            InvalidateVisualTree(root);
            root.UpdateLayout();
            // Flush WPF's retained drawing changes before a second offscreen
            // capture; otherwise unchanged text/images can be missing in PNGs.
            var frame = new DispatcherFrame();
            root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(captureDirectory, name + ".png"))) encoder.Save(stream);
        }
        private static void InvalidateVisualTree(DependencyObject node)
        {
            var element = node as UIElement;
            if (element != null) element.InvalidateVisual();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                InvalidateVisualTree(VisualTreeHelper.GetChild(node, i));
        }

        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        private static void Toggle(ToggleButton check)
        {
            typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(check, null);
        }
        private static T Field<T>(object owner, string name)
        {
            return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        }
        private static void SetField(object owner, string name, object value)
        {
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
        }
        private static bool IsBright(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid != null && solid.Color.A == 255 && solid.Color.R >= 200 && solid.Color.G >= 200 && solid.Color.B >= 200;
        }
        private static bool HasRenderedText(DependencyObject node, string expected)
        {
            var text = node as TextBlock;
            if (text != null && text.Text == expected && text.IsVisible && text.ActualWidth > 0) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (HasRenderedText(VisualTreeHelper.GetChild(node, i), expected)) return true;
            return false;
        }
        private static async Task<bool> Until(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds >= timeoutMs) return false;
                await Task.Delay(50);
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
