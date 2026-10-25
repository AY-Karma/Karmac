using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Karmac")]
[assembly: AssemblyProduct("Karmac")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AY-Karma and contributors")]
[assembly: AssemblyInformationalVersion("1.0.0-preview.1")]
[assembly: AssemblyDescription("One-click reconnection for paired Apple and Beats headphones")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Karmac
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--diagnose")
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    var result = Task.Run(() => DeviceDiscovery.DiscoverAsync()).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new {
                        elapsedMs = watch.ElapsedMilliseconds, radio = result.Radio,
                        warning = result.Warning, targets = result.Targets
                    }));
                    return 0;
                }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); return 1; }
            }
            string capture = args.Length >= 2 && args[0] == "--capture" ? args[1] : null;
            bool owned;
            using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Karmac.AirPods.Activate"))
            using (var mutex = new Mutex(true, "Local\\Karmac.AirPods", out owned))
            {
                if (!owned && capture == null)
                {
                    activation.Set();
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += (sender, e) => {
                        Log.Write(e.Exception.ToString());
                        MessageBox.Show("Karmac encountered a problem. Details were saved in %LOCALAPPDATA%\\Karmac\\app.log.", "Karmac");
                        e.Handled = true;
                    };
                    var controller = new AppController(app, capture, activation);
                    controller.Show();
                    app.Run();
                    controller.Dispose();
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Write(ex.ToString());
                    MessageBox.Show(ex.Message, "Karmac could not start"); return 1;
                }
            }
        }
    }

    internal sealed class BatterySourceOption
    {
        internal readonly BatteryReading Reading;
        internal BatterySourceOption(BatteryReading reading) { Reading = reading; }
        public string Name { get { return AirPodsModels.Name(Reading.Model); } }
        public string Detail { get { return "Left " + BatteryReading.Percent(Reading.Left) + "  ·  Right " + BatteryReading.Percent(Reading.Right) + "  ·  Case " + BatteryReading.Percent(Reading.Case); } }
        public string Signal { get { return "Signal " + Reading.Rssi + " dBm  ·  Broadcast " + Reading.AddressLabel.Substring(8); } }
        public override string ToString() { return Name + ". " + Detail + ". " + Signal; }
    }

    internal sealed class AppController : IDisposable
    {
        private readonly Application app;
        private readonly Window window;
        private readonly Preferences settings;
        private readonly BatteryMonitor battery = new BatteryMonitor();
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer connectionTimer;
        private readonly ConnectionTrace connectionTrace;
        private readonly ArtworkMotion artworkMotion = new ArtworkMotion();
        private IConnectionPlatform connectionPlatform = new WindowsConnectionPlatform();
        private bool connectionExpanded;
        private bool keyboardNavigation;
        private readonly string capturePath;
        private Forms.NotifyIcon tray;
        private ToolTip keyboardStatusTip;
        private HwndSource hwndSource;
        private RegisteredWaitHandle activationWait;
        private bool busy, refreshing, polling, exiting, initialized, preferencesLoaded;
        private long stateRevision;
        private int refreshTick;
        private List<AudioTarget> targets = new List<AudioTarget>();
        private Func<Task<DiscoveryResult>> discoverDevices = DeviceDiscovery.DiscoverAsync;
        private readonly Dictionary<string, ImageSource> artwork = new Dictionary<string, ImageSource>();
        private readonly Dictionary<string, ArtworkImage> deviceArtwork = new Dictionary<string, ArtworkImage>();
        private readonly Dictionary<string, DateTime> artworkRequested = new Dictionary<string, DateTime>();
        private int artworkRevision;
        private string radioState = "Unknown";
        private ComboBox Picker { get { return Get<ComboBox>("DevicePicker"); } }
        private AudioTarget Selected { get { return Picker.SelectedItem as AudioTarget; } }

        internal AppController(Application application, string capture, EventWaitHandle activation)
        {
            app = application; capturePath = capture; settings = Preferences.Load();
            connectionTrace = new ConnectionTrace(SystemParameters.ClientAreaAnimation);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(stream);
            window.MaxHeight = SystemParameters.WorkArea.Height;
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height - 32);
            if (capturePath != null)
            {
                window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = SystemParameters.VirtualScreenLeft - window.Width - 128;
                window.Top = SystemParameters.VirtualScreenTop - window.Height - 128;
                window.Opacity = 0; window.IsHitTestVisible = false;
            }
            app.MainWindow = window;
            Get<Image>("DeviceArtwork").RenderTransform = artworkMotion.Transform;
            Get<StackPanel>("MainPanel").IsVisibleChanged += (sender, e) => UpdateArtworkMotion();
            window.IsVisibleChanged += (sender, e) => UpdateArtworkMotion();
            window.StateChanged += (sender, e) => { UpdateArtworkMotion(); UpdateBusyIndicator(); };
            Get<ProgressBar>("BusyBar").IsVisibleChanged += (sender, e) => UpdateBusyIndicator();
            SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged;
            if (capturePath == null)
                activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (state, timedOut) => {
                    if (exiting || window.Dispatcher.HasShutdownStarted) return;
                    window.Dispatcher.BeginInvoke(new Action(() => {
                        if (exiting) return;
                        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
                    }));
                }, null, Timeout.Infinite, false);
            var chrome = new System.Windows.Shell.WindowChrome { CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12) };
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);
            Get<Grid>("TitleBar").MouseLeftButtonDown += (sender, e) => {
                var element = e.OriginalSource as FrameworkElement;
                while (element != null)
                {
                    if (element is Button) return;
                    element = element.Parent as FrameworkElement ?? VisualTreeHelper.GetParent(element) as FrameworkElement;
                }
                if (e.LeftButton == MouseButtonState.Pressed) {
                    try { window.DragMove(); } catch (InvalidOperationException) { }
                }
            };
            Button("ConnectButton").Click += async (sender, e) => await ConnectAsync(true);
            Button("ConnectionDetailsButton").Click += (sender, e) => { connectionExpanded = !connectionExpanded; RenderConnectionTrace(); UpdateDevice(); };
            Get<ItemsControl>("ConnectionSteps").ItemsSource = connectionTrace.Steps;
            Button("DisconnectButton").Click += async (sender, e) => { ShowAirPods(); await DisconnectAsync(); };
            Button("MainDisconnectButton").Click += async (sender, e) => { ShowAirPods(); await DisconnectAsync(); };
            Get<System.Windows.Controls.Primitives.ToggleButton>("ConnectionToggle").Click += async (sender, e) => {
                if (busy) { UpdateDevice(); return; }
                if (Get<System.Windows.Controls.Primitives.ToggleButton>("ConnectionToggle").IsChecked == true) await ConnectAsync(true);
                else { ShowAirPods(); await DisconnectAsync(); }
            };
            Button("RouteButton").Click += async (sender, e) => { ShowAirPods(); await RouteAsync(); };
            Button("RefreshButton").Click += async (sender, e) => {
                AudioArtwork.Invalidate(); deviceArtwork.Clear(); artworkRequested.Clear(); artworkRevision++;
                ShowAirPods(); await RefreshAsync(false);
            };
            Button("PairButton").Click += (sender, e) => OpenBluetoothSettings();
            Button("SetupButton").Click += (sender, e) => {
                if (radioState != "No adapter" && Selected == null && Picker.HasItems)
                { FocusControl(Picker); Picker.IsDropDownOpen = true; }
                else OpenBluetoothSettings();
            };
            Button("CloseButton").Click += (sender, e) => window.Close();
            Button("MinimizeButton").Click += (sender, e) => window.WindowState = WindowState.Minimized;
            Button("QuitButton").Click += (sender, e) => Quit();
            Button("LicenseButton").Click += (sender, e) => ShowNotices();
            Button("SettingsButton").Click += (sender, e) => ToggleSettings();
            Button("BackButton").Click += (sender, e) => ToggleSettings();
            Button("BatterySourceButton").Click += (sender, e) => ShowBatterySources();
            Button("BatteryBackButton").Click += (sender, e) => ShowAirPods();
            Button("UseBatterySourceButton").Click += (sender, e) => UseBatterySource();
            Button("AutomaticBatteryButton").Click += (sender, e) => {
                battery.ClearSource(Selected); ShowAirPods(); UpdateBattery();
            };
            Get<ListBox>("BatterySourcesList").SelectionChanged += (sender, e) =>
                Button("UseBatterySourceButton").IsEnabled = Selected != null && Get<ListBox>("BatterySourcesList").SelectedItem != null;
            window.PreviewKeyDown += (sender, e) => {
                keyboardNavigation = true;
                if (e.Key == Key.Escape && Get<Border>("SettingsPanel").Visibility == Visibility.Visible)
                { ToggleSettings(); e.Handled = true; }
                else if (e.Key == Key.Escape && Get<Border>("BatterySourcePanel").Visibility == Visibility.Visible)
                { ShowAirPods(); e.Handled = true; }
            };
            window.PreviewMouseDown += (sender, e) => keyboardNavigation = false;
            var statusPanel = Get<Border>("StatusPanel");
            statusPanel.GotKeyboardFocus += (sender, e) => ShowStatusDetails();
            statusPanel.LostKeyboardFocus += (sender, e) => {
                if (keyboardStatusTip != null) keyboardStatusTip.IsOpen = false;
            };
            Picker.SelectionChanged += (sender, e) => {
                if (!refreshing)
                {
                    if (Selected != null)
                    {
                        settings.Favorite = Selected.ContainerId;
                        if (!Selected.RequiresPairingCheck) settings.SavedPair = SavedAirPods.FromTarget(Selected);
                        SavePreferences();
                    }
                    SetStatus(Selected == null ? "Choose your headphones" : "Headphones selected",
                        Selected == null ? "Select your saved pair, or pair your headphones in Windows from Settings." :
                        "The selected paired identity will be used for your next connection.", false, false);
                }
                UpdateDevice(); UpdateBattery();
            };
            Get<CheckBox>("AutoRouteCheck").IsChecked = settings.RouteAudio;
            Get<CheckBox>("PowerOffBluetoothCheck").IsChecked = settings.PowerOffBluetoothOnDisconnect;
            Get<CheckBox>("TrayCheck").IsChecked = settings.CloseToTray;
            Get<CheckBox>("HotkeyCheck").IsChecked = settings.Hotkey;
            foreach (string name in new[] { "AutoRouteCheck", "PowerOffBluetoothCheck", "TrayCheck", "HotkeyCheck" })
            {
                Get<CheckBox>(name).Checked += PreferencesChanged;
                Get<CheckBox>(name).Unchecked += PreferencesChanged;
            }
            preferencesLoaded = true;
            window.SourceInitialized += (sender, e) => {
                hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                hwndSource.AddHook(WindowMessage); UpdateHotkey();
            };
            window.Closing += (sender, e) => {
                if (!exiting && settings.CloseToTray && capturePath == null) { e.Cancel = true; window.Hide(); }
                else { exiting = true; app.Shutdown(); }
            };
            window.Loaded += async (sender, e) => {
                if (initialized) return; initialized = true;
                if (capturePath == null) { CreateTray(); battery.Start(); }
                await RefreshAsync(false);
                if (capturePath != null)
                {
                    await Task.Delay(350); Capture(capturePath); Quit();
                }
                else timer.Start();
            };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            connectionTimer.Tick += (sender, e) => { connectionTrace.Tick(); Text("ConnectionElapsed", connectionTrace.Elapsed); };
            connectionTrace.Changed += () => {
                if (connectionTrace.Steps.Count > 0 && connectionTrace.Steps[0].State == ConnectionStepState.Complete)
                { radioState = "On"; Text("RadioLabel", "Bluetooth on"); Indicator("RadioDot", true); }
                if (!connectionTrace.Running) { connectionExpanded = connectionTrace.Failed; connectionTimer.Stop(); }
                RenderConnectionTrace(); UpdateDevice();
            };
            timer.Tick += async (sender, e) => {
                if (busy || refreshing) return;
                UpdateBattery();
                if (Get<Border>("BatterySourcePanel").Visibility == Visibility.Visible) UpdateBatterySources();
                if (++refreshTick >= 10) { refreshTick = 0; await RefreshAsync(true); }
                else await PollStatesAsync();
            };
            var coldSaved = settings.SavedPair == null ? null : settings.SavedPair.Restore(settings.Favorite);
            if (coldSaved != null)
            {
                refreshing = true; targets.Add(coldSaved); Picker.ItemsSource = targets; Picker.SelectedItem = coldSaved;
                refreshing = false;
            }
            UpdateDevice();
        }

        internal void Show() { window.Show(); }
        private T Get<T>(string name) where T : class { return (T)window.FindName(name); }
        private Button Button(string name) { return Get<Button>(name); }
        private void FocusControl(Control control) { if (window.ShowActivated) control.Focus(); }
        private void FocusNavigationControl(Control control)
        {
            if (!window.ShowActivated) return;
            if (keyboardNavigation) { control.Focus(); return; }
            // Mouse navigation must not leave the destination button focused.
            // Keep the explicit focus transfer for Tab/Enter/Escape navigation.
            FocusManager.SetFocusedElement(window, null);
            Keyboard.ClearFocus();
        }
        private void Text(string name, string text) { Get<TextBlock>(name).Text = text; }
        private static string DeviceTerm(AudioTarget target)
        { return target == null || target.ModelId == 0 ? "headphones" : AirPodsModels.IsBeats(target.ModelId) ? "Beats" : "AirPods"; }

        private void ToggleSettings()
        {
            var panel = Get<Border>("SettingsPanel");
            bool show = panel.Visibility != Visibility.Visible;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("BatterySourcePanel").Visibility = Visibility.Collapsed;
            Get<StackPanel>("MainPanel").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            Button("SettingsButton").ToolTip = show ? "Back to headphones" : "Settings and connection tools";
            Get<ScrollViewer>("ContentScroll").ScrollToTop();
            FocusNavigationControl(Button(show ? "BackButton" : "SettingsButton"));
        }

        private void ShowAirPods()
        {
            if (Get<Border>("SettingsPanel").Visibility == Visibility.Visible) ToggleSettings();
            if (Get<Border>("BatterySourcePanel").Visibility == Visibility.Visible)
            {
                Get<Border>("BatterySourcePanel").Visibility = Visibility.Collapsed;
                Get<StackPanel>("MainPanel").Visibility = Visibility.Visible;
                Get<ScrollViewer>("ContentScroll").ScrollToTop();
                FocusNavigationControl(Button("BatterySourceButton"));
            }
        }

        private void ShowBatterySources()
        {
            if (Selected == null) return;
            Get<Border>("SettingsPanel").Visibility = Visibility.Collapsed;
            Get<StackPanel>("MainPanel").Visibility = Visibility.Collapsed;
            Get<Border>("BatterySourcePanel").Visibility = Visibility.Visible;
            UpdateBatterySources();
            Get<ScrollViewer>("ContentScroll").ScrollToTop();
            FocusNavigationControl(Button("BatteryBackButton"));
        }

        private void UpdateBatterySources()
        {
            var list = Get<ListBox>("BatterySourcesList");
            var selected = list.SelectedItem as BatterySourceOption;
            var items = battery.CandidatesForTarget(Selected).Select(r => new BatterySourceOption(r)).ToList();
            list.ItemsSource = items;
            list.SelectedItem = selected == null ? null : items.FirstOrDefault(i => i.Reading.Address == selected.Reading.Address);
            Text("BatterySourceTarget", Selected == null ? "No Headphones selected" : "For " + Selected.Name);
            Text("BatterySourceEmpty", Selected == null ? "Select your paired headphones first." :
                "No readable nearby broadcasts yet. Keep the case open beside the laptop; the list updates automatically.");
            Get<TextBlock>("BatterySourceEmpty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Button("UseBatterySourceButton").IsEnabled = Selected != null && list.SelectedItem != null;
        }

        private void UseBatterySource()
        {
            var option = Get<ListBox>("BatterySourcesList").SelectedItem as BatterySourceOption;
            if (option == null || !battery.SelectSource(Selected, option.Reading.Address))
            { UpdateBatterySources(); return; }
            ShowAirPods(); UpdateBattery();
        }

        private void ShowStatusDetails()
        {
            var panel = Get<Border>("StatusPanel");
            var detail = panel.ToolTip as TextBlock;
            if (detail == null) return;
            if (keyboardStatusTip == null)
                keyboardStatusTip = new ToolTip { PlacementTarget = panel,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    Style = (Style)window.FindResource(typeof(ToolTip)), StaysOpen = true };
            keyboardStatusTip.Content = new TextBlock { Text = detail.Text, MaxWidth = 300, TextWrapping = TextWrapping.Wrap };
            keyboardStatusTip.IsOpen = true;
        }

        private async Task RefreshAsync(bool quiet)
        {
            if (refreshing || busy) return;
            refreshing = true; long discoveryRevision = ++stateRevision; UpdateDevice();
            if (!quiet) SetStatus("Finding your headphones", "Reading paired devices. No nearby device will be paired automatically.", true, false);
            try
            {
                var result = await Task.Run(() => discoverDevices());
                if (busy || discoveryRevision != stateRevision) return;
                string selectedId = Selected == null ? settings.Favorite : Selected.ContainerId;
                targets = result.Targets; radioState = result.Radio;
                if (settings.SavedPair != null && !targets.Any(t => DeviceDiscovery.SameGuid(t.ContainerId, settings.Favorite)))
                {
                    var saved = settings.SavedPair.Restore(settings.Favorite);
                    if (saved != null) targets.Add(saved);
                }
                battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList());
                Text("RadioLabel", radioState == "On" ? "Bluetooth on" : "Bluetooth: " + radioState.ToLowerInvariant());
                Indicator("RadioDot", radioState == "On" ? (bool?)true : radioState == "Off" || radioState == "No adapter" ? (bool?)false : null);
                Picker.ItemsSource = targets;
                Picker.SelectedItem = selectedId != null ? targets.FirstOrDefault(t => t.ContainerId == selectedId) :
                    targets.Count == 1 ? targets[0] : null;
                if (Selected != null && settings.Favorite == null)
                {
                    settings.Favorite = Selected.ContainerId; SavePreferences();
                }
                if (Selected != null && !Selected.RequiresPairingCheck)
                { settings.SavedPair = SavedAirPods.FromTarget(Selected); SavePreferences(); }
                if (!quiet)
                {
                    if (radioState == "Off") SetStatus("Bluetooth is off", "Use Connect to turn Bluetooth on and connect your saved pair.", false, false);
                    else if (radioState == "No adapter") SetStatus("No Bluetooth adapter", "Connect or enable your Bluetooth adapter, then press Refresh.", false, true);
                    else if (Selected == null) SetStatus(targets.Count > 0 ? "Choose your headphones" : "Pair your headphones once",
                        targets.Count > 0 ? "Select your paired headphones above. A missing saved pair will never be replaced automatically." :
                        "Open the case in pairing mode and use Pair in Windows. After that, Connect is one click.", false, false);
                    else SetStatus(Selected.Connected ? "Headphones connected" : "Ready when you are",
                        Selected.Connected ? (Selected.IsDefault ? "Your headphones are the Windows media output." : "Use the audio switch action to select your headphones as the Windows output.") :
                        "Your saved headphones only. Keep them awake nearby, then connect.", false, false);
                    if (result.Warning != null) SetStatus("Device details partially unavailable", result.Warning + " You can still try connecting a listed device.", false, true);
                }
            }
            catch (Exception ex) { if (discoveryRevision == stateRevision) SetStatus("Could not read devices", ex.Message, false, true); Log.Write(ex.ToString()); }
            finally { if (discoveryRevision == stateRevision) refreshing = false; UpdateDevice(); UpdateBattery(); }
        }

        private async Task ConnectAsync(bool explicitAudioSwitch = false)
        {
            if (busy || Selected == null) return;
            ShowAirPods();
            var target = Selected;
            busy = true; refreshing = false; stateRevision++; connectionExpanded = true; UpdateDevice();
            connectionTimer.Start();
            try
            {
                Exception failure = null;
                try
                {
                    await new ConnectionFlow(connectionPlatform).ConnectAsync(target, settings.RouteAudio, explicitAudioSwitch, connectionTrace);
                    ApplyRadioState("On"); battery.Start();
                    SetStatus(connectionTrace.Title, connectionTrace.Detail + ". Completed in " + connectionTrace.Elapsed + ". Apps with a fixed output keep their own selection.", false, false);
                }
                catch (Exception error) { failure = error; Log.Write("Connect: " + error); }
                if (failure != null)
                {
                    try { ApplyRadioState(await connectionPlatform.ReadRadioStateAsync()); }
                    catch (Exception error) { Log.Write("Connect radio verification: " + error.Message); }
                    SetStatus(target.Connected ? "Connected · output needs attention" : "Could not connect", failure.Message, false, true);
                }
            }
            finally
            {
                busy = false; connectionTimer.Stop();
                if (!target.RequiresPairingCheck) { settings.SavedPair = SavedAirPods.FromTarget(target); SavePreferences(); }
                battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList());
                RenderConnectionTrace(); UpdateDevice(); UpdateBattery();
            }
        }
        private void RenderConnectionTrace()
        {
            bool expanded = connectionTrace.HasStarted && (connectionTrace.Running || connectionExpanded);
            Get<StackPanel>("ConnectionStream").Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("BatterySection").Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            Button("ConnectionDetailsButton").Visibility = connectionTrace.HasStarted && !connectionTrace.Running ? Visibility.Visible : Visibility.Collapsed;
            Button("ConnectionDetailsButton").Content = expanded ? "Hide connection details ‹" : "Connection details · " + connectionTrace.Elapsed + " ›";
            Text("ConnectionStreamLabel", connectionTrace.Running ? "LIVE CONNECTION" : connectionTrace.Failed ? "CONNECTION PAUSED" : "CONNECTION COMPLETE");
            Text("ConnectionTitle", connectionTrace.Title ?? ""); Text("ConnectionDetail", connectionTrace.Detail ?? "");
            Text("ConnectionElapsed", connectionTrace.Elapsed);
            Get<TextBlock>("ConnectionDetail").ToolTip = connectionTrace.Detail;
            Get<TextBlock>("ConnectionTitle").Foreground = DarkBrush(connectionTrace.Failed ? "#F87171" : "#F2F2F2");
        }
        private async Task AwaitDriverAsync(Task operation)
        {
            if (await Task.WhenAny(operation, Task.Delay(12000)) != operation)
                SetStatus("Windows is still responding", "The Bluetooth driver is taking longer than expected. Controls will return when its request finishes; you can still move or close this window.", true, true);
            await operation;
        }

        private async Task DisconnectAsync()
        {
            if (busy) return;
            if (!settings.PowerOffBluetoothOnDisconnect) { await DisconnectHeadphonesAsync(); return; }
            busy = true; refreshing = false; stateRevision++;
            connectionExpanded = false; RenderConnectionTrace(); UpdateDevice();
            SetStatus("Turning Bluetooth off", "Disconnecting all Bluetooth devices on this laptop. Your headphones stay paired.", true, false);
            Exception failure = null;
            try { await connectionPlatform.PowerOffAsync(); }
            catch (Exception error) { failure = error; Log.Write("Bluetooth off: " + error); }
            try
            {
                ApplyRadioState(await connectionPlatform.ReadRadioStateAsync());
                if (failure == null && radioState != "Off" && radioState != "No adapter")
                    failure = new InvalidOperationException("Windows has not confirmed that Bluetooth is off. Check radio access or your administrator's policy.");
                if (failure == null) SetStatus("Bluetooth off", "All laptop Bluetooth connections are off. Your headphones remain paired for the next connection.", false, false);
                else SetStatus("Could not turn Bluetooth off", failure.Message, false, true);
            }
            catch (Exception error)
            {
                SetStatus("Bluetooth state could not be verified", (failure == null ? "" : failure.Message + " ") + error.Message, false, true);
                Log.Write("Bluetooth off verification: " + error);
            }
            finally
            {
                busy = false; battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList());
                UpdateDevice(); UpdateBattery();
            }
        }

        private async Task DisconnectHeadphonesAsync()
        {
            var target = Selected;
            if (target == null || target.RequiresPairingCheck) return;
            busy = true; refreshing = false; stateRevision++;
            connectionExpanded = false; connectionTrace.Reset(); UpdateDevice();
            SetStatus("Disconnecting headphones", "Disconnecting this pair's audio profiles. Laptop Bluetooth stays enabled.", true, false);
            Exception failure = null;
            try
            {
                await connectionPlatform.DisconnectAsync(target);
                var watch = Stopwatch.StartNew();
                do
                {
                    await connectionPlatform.ReadAudioAsync(target);
                    if (!target.Endpoints.Any(e => e.Active)) break;
                    if (watch.ElapsedMilliseconds >= 8000)
                        throw new InvalidOperationException("Windows still reports an active audio profile for this pair. Bluetooth has been left enabled.");
                    await connectionPlatform.DelayAsync(150);
                } while (true);
                target.IsDefault = false;
            }
            catch (Exception error)
            {
                failure = error;
                Log.Write("Headphones disconnect: " + error);
            }
            try
            {
                if (failure != null) await connectionPlatform.ReadAudioAsync(target);
                ApplyRadioState(await connectionPlatform.ReadRadioStateAsync());
                if (failure == null) SetStatus("Headphones disconnected", "This pair is disconnected and stays paired. Laptop Bluetooth is unchanged.", false, false);
                else SetStatus("Could not disconnect headphones", failure.Message, false, true);
            }
            catch (Exception observationError)
            {
                Log.Write("Disconnect state observation: " + observationError.Message);
                SetStatus("Headphone state could not be verified", (failure == null ? "" : failure.Message + " ") + observationError.Message, false, true);
            }
            finally
            {
                busy = false; battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList());
                UpdateDevice(); UpdateBattery();
            }
        }

        private void ApplyRadioState(string state)
        {
            radioState = state;
            Text("RadioLabel", state == "On" ? "Bluetooth on" : "Bluetooth: " + state.ToLowerInvariant());
            Indicator("RadioDot", state == "On" ? (bool?)true : state == "Off" || state == "No adapter" ? (bool?)false : null);
            if (state == "Off" || state == "No adapter")
                foreach (var target in targets)
                {
                    target.IsDefault = false;
                    target.Endpoints = target.Endpoints.Select(e => new AudioEndpoint { Id = e.Id, Name = e.Name,
                        ControllerId = e.ControllerId, Flow = e.Flow, State = 8 }).ToList();
                }
        }
        private async Task RouteAsync()
        {
            if (busy || refreshing || Selected == null || !Selected.Connected) return;
            var target = Selected; busy = true; stateRevision++; UpdateDevice();
            SetStatus("Routing your audio", "Setting and verifying your headphone audio output…", true, false);
            try
            {
                await Task.Run(() => AudioService.Route(target)); target.IsDefault = true;
                SetStatus("Audio output verified", "Your headphones are the default output for media and calls. An app with a fixed output may need its own output changed.", false, false);
            }
            catch (Exception ex) { target.IsDefault = false; SetStatus("Audio route needs attention", ex.Message, false, true); }
            finally { busy = false; UpdateDevice(); }
        }

        private async Task PollStatesAsync()
        {
            if (polling || busy || refreshing || Selected == null) return;
            var target = Selected;
            polling = true;
            long revision = stateRevision;
            try
            {
                var snapshot = await Task.Run(() => {
                    var endpoints = AudioService.ReadStates(target);
                    return new { Endpoints = endpoints, IsDefault = AudioService.IsDefaultOutput(endpoints) };
                });
                string currentRadio = await connectionPlatform.ReadRadioStateAsync();
                // A user action may have begun while the background read was running.
                if (!busy && !refreshing && revision == stateRevision && Selected == target)
                {
                    bool wasConnected = target.Connected;
                    target.Endpoints = snapshot.Endpoints;
                    target.IsDefault = snapshot.IsDefault;
                    ApplyRadioState(currentRadio);
                    if (wasConnected != target.Connected) battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList());
                    UpdateDevice();
                }
            }
            catch (Exception ex) { Log.Write("State poll: " + ex.Message); }
            finally { polling = false; }
        }

        private void UpdateDevice()
        {
            var target = Selected;
            bool radioAvailable = radioState != "Off" && radioState != "No adapter";
            bool ready = !busy && target != null && radioState != "No adapter";
            bool needsSetup = target == null || radioState == "No adapter";
            bool listening = !needsSetup && radioAvailable && target.Connected && target.IsDefault && !busy;
            Button("ConnectButton").IsEnabled = ready;
            Button("ConnectButton").Content = busy ? "Working…" : radioAvailable && target != null && target.Connected ? "Switch audio to " + DeviceTerm(target) : "Connect " + DeviceTerm(target);
            System.Windows.Automation.AutomationProperties.SetName(Button("ConnectButton"), Button("ConnectButton").Content.ToString());
            Button("ConnectButton").Visibility = needsSetup || listening || connectionTrace.Running ? Visibility.Collapsed : Visibility.Visible;
            Get<Border>("ReadyPanel").Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
            Button("SetupButton").Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
            Button("SetupButton").IsEnabled = !busy && !refreshing;
            Button("SetupButton").Content = radioState == "No adapter" ? "Open Bluetooth settings" : targets.Count > 0 ? "Choose headphones" : "Pair headphones in Windows";
            System.Windows.Automation.AutomationProperties.SetName(Button("SetupButton"), Button("SetupButton").Content.ToString());
            Button("RouteButton").IsEnabled = ready && !refreshing && radioAvailable && target.Connected;
            Button("DisconnectButton").IsEnabled = !busy && radioState == "On" &&
                (settings.PowerOffBluetoothOnDisconnect || target != null && target.Connected && !target.RequiresPairingCheck);
            Button("DisconnectButton").Content = settings.PowerOffBluetoothOnDisconnect ? "Turn Bluetooth off" : "Disconnect headphones";
            Button("DisconnectButton").ToolTip = settings.PowerOffBluetoothOnDisconnect ?
                "Disconnects all Bluetooth devices on this laptop" : "Disconnects only the selected headphones; Bluetooth stays enabled";
            Get<CheckBox>("PowerOffBluetoothCheck").IsEnabled = !busy;
            string disconnectScope = settings.PowerOffBluetoothOnDisconnect ?
                "Turns off laptop Bluetooth, disconnecting every device." :
                "Only these headphones disconnect. Bluetooth stays on.";
            Text("DisconnectScopeHint", disconnectScope);
            System.Windows.Automation.AutomationProperties.SetHelpText(Get<CheckBox>("PowerOffBluetoothCheck"), disconnectScope);
            Button("MainDisconnectButton").Visibility = Visibility.Collapsed;
            Button("MainDisconnectButton").IsEnabled = ready && !refreshing && radioAvailable && target.Connected;
            var powerSwitch = Get<System.Windows.Controls.Primitives.ToggleButton>("ConnectionToggle");
            bool switchOn = settings.PowerOffBluetoothOnDisconnect ? radioState == "On" :
                radioAvailable && target != null && target.Connected;
            powerSwitch.IsChecked = switchOn;
            powerSwitch.IsEnabled = !busy && radioState != "No adapter" &&
                (settings.PowerOffBluetoothOnDisconnect && radioState == "On" || target != null);
            string switchHelp = settings.PowerOffBluetoothOnDisconnect ?
                "On: turn Bluetooth on and connect the selected headphones.\nOff: turn Bluetooth off for the entire laptop, including all other Bluetooth devices." :
                "On: turn Bluetooth on if needed and connect the selected headphones.\nOff: disconnect only the selected headphones; laptop Bluetooth stays enabled.";
            powerSwitch.ToolTip = switchHelp;
            System.Windows.Automation.AutomationProperties.SetHelpText(powerSwitch, switchHelp);
            System.Windows.Automation.AutomationProperties.SetName(powerSwitch,
                (settings.PowerOffBluetoothOnDisconnect ? "Bluetooth power. " : "Headphone connection. ") +
                (switchOn ? "On. " : "Off. ") + (target != null && radioAvailable && target.Connected ? "Headphones connected." : "Headphones not connected."));
            Picker.IsEnabled = !busy && !refreshing;
            Button("RefreshButton").IsEnabled = !busy && !refreshing;
            Text("ConnectionBadge", target == null ? refreshing ? "Checking" : "No device" : radioAvailable && target.Connected ? "Connected" : connectionTrace.Running ? "Connecting" : "Disconnected");
            Indicator("ConnectionDot", target == null ? (bool?)null : radioAvailable && target.Connected);
            if (!busy && target != null && radioAvailable && target.Connected && Get<TextBlock>("StatusTitle").Text == "Could not connect")
            {
                connectionExpanded = false; RenderConnectionTrace();
                SetStatus("Headphones connected", target.IsDefault ? "Windows now reports an active headphone output. Previous attempt details remain available." :
                    "Your headphones are connected. Use the audio switch action to select them as the Windows output. Previous attempt details remain available.", false, false);
            }
            System.Windows.Automation.AutomationProperties.SetName(Get<Border>("StatusPanel"), Get<TextBlock>("ConnectionBadge").Text + ". " + Get<TextBlock>("StatusTitle").Text);
            Text("DeviceSubtitle", !radioAvailable ? radioState == "Off" ? "Connect turns Bluetooth on automatically." : "No Bluetooth adapter is available. Check Windows Bluetooth settings." :
                target == null ? targets.Count > 0 ? "Choose your paired headphones above." : "Pair once in Windows, then connect here." : "");
            Get<TextBlock>("DeviceSubtitle").Visibility = needsSetup || !radioAvailable ? Visibility.Visible : Visibility.Collapsed;
            Text("ModelLabel", target == null ? "APPLE AUDIO" : target.ModelName.ToUpperInvariant());
            UpdateArtwork(target);
            if (tray != null) tray.Text = target != null && target.Connected ? "Karmac · Headphones connected" : "Karmac · Click to open";
        }

        private void Indicator(string name, bool? connected)
        {
            Get<System.Windows.Shapes.Ellipse>(name).Fill = DarkBrush(connected.HasValue ? connected.Value ? "#4ADE80" : "#F87171" : "#808080");
        }

        private void UpdateArtwork(AudioTarget target)
        {
            UpdateArtworkMotion();
            bool compact = connectionTrace.HasStarted && (connectionTrace.Running || connectionExpanded);
            Get<Grid>("ArtworkStage").Height = compact ? 135 : 210;
            int model = target == null ? 0 : target.ModelId;
            string key = AirPodsModels.ArtworkKey(model), family = AirPodsModels.Family(model);
            ImageSource source;
            if (!artwork.TryGetValue(key, out source))
            {
                string resource = "GenericAudioArtwork";
                if (family == "max") resource = "MaxArtwork";
                else if (family == "pro") resource = "ProPodsArtwork";
                else if (family == "pro3") resource = "PodsArtwork";
                else if (family == "pods") resource = model == 0x2002 || model == 0x200F ? "StandardPodsArtwork" : "ShortPodsArtwork";
                else if (family == "beats-headphones") resource = "BeatsOverEarArtwork";
                else if (family == "beats-neckband") resource = "BeatsNeckbandArtwork";
                else if (family == "beats-buds") resource = model == 0x200B || model == 0x201D ? "BeatsEarhookArtwork" : "BeatsBudsArtwork";
                source = (ImageSource)window.FindResource(resource);
                if (family == "pro3")
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirPodsPro3.png"))
                        if (stream != null)
                        {
                            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 480; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); source = bitmap;
                        }
                artwork[key] = source;
            }
            string bluetoothId = target == null || target.RequiresPairingCheck ? null : target.BluetoothId;
            string cacheKey = key + "|" + (bluetoothId ?? "");
            ArtworkImage supplied;
            var image = Get<Image>("DeviceArtwork");
            if (deviceArtwork.TryGetValue(cacheKey, out supplied))
            { source = supplied.Source; image.ToolTip = supplied.Description + " · " + (target == null ? "Apple audio" : target.ModelName); }
            else image.ToolTip = "Original illustrative artwork · " + (target == null ? "Apple audio" : target.ModelName);
            image.Source = source;
            image.Width = compact ? 160 : family == "pro3" ? 270 : 238;
            image.Height = compact ? 105 : family == "pro3" ? 182 : 175;
            DateTime requested;
            if (!artworkRequested.TryGetValue(cacheKey, out requested) || DateTime.UtcNow - requested > TimeSpan.FromSeconds(60))
            {
                artworkRequested[cacheKey] = DateTime.UtcNow;
                LoadDeviceArtwork(target, key, bluetoothId, cacheKey, artworkRevision);
            }
        }

        private void UpdateArtworkMotion()
        {
            bool connected = Selected != null && Selected.Connected && radioState != "Off" && radioState != "No adapter";
            bool animate = !exiting && capturePath == null && SystemParameters.ClientAreaAnimation &&
                window.IsVisible && window.WindowState != WindowState.Minimized && Get<StackPanel>("MainPanel").IsVisible;
            artworkMotion.Update(connected, animate);
        }

        private void AnimationPreferenceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "ClientAreaAnimation" || exiting || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(new Action(() => { UpdateArtworkMotion(); UpdateBusyIndicator(); }));
        }

        private async void LoadDeviceArtwork(AudioTarget target, string modelKey, string bluetoothId, string cacheKey, int revision)
        {
            try
            {
                var loaded = await AudioArtwork.LoadAsync(modelKey, bluetoothId);
                if (loaded == null || exiting || revision != artworkRevision) return;
                deviceArtwork[cacheKey] = loaded;
                if (Selected == target && AirPodsModels.ArtworkKey(target == null ? 0 : target.ModelId) == modelKey)
                    UpdateArtwork(target);
            }
            catch (Exception error) { Log.Write("Device artwork: " + error.GetType().Name); }
        }
        private void UpdateBattery()
        {
            var target = Selected;
            var value = battery.ForTarget(target);
            var overall = battery.GetDeviceBattery(target);
            int? windowsPercent = target != null && target.Connected ? overall != null && overall.Fresh ? (int?)overall.Percent : target.Battery : null;
            if (value != null && value.UserSelected && windowsPercent.HasValue) value = null;
            string modelLayout = AirPodsModels.ComponentLayout(target == null ? 0 : target.ModelId);
            bool components = value != null && modelLayout != "single";
            int? singlePercent = windowsPercent ?? (value == null ? null : value.Left ?? value.Right);
            bool single = !components;
            var levels = Get<System.Windows.Controls.Primitives.UniformGrid>("BatteryLevels");
            levels.Columns = single ? 1 : modelLayout == "dual" ? 2 : 3;
            levels.Visibility = components || singlePercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("LeftBatteryCell").Padding = single ? new Thickness(0) : new Thickness(0, 0, 18, 0);
            Get<Border>("LeftBatteryCell").BorderThickness = single ? new Thickness(0) : new Thickness(0, 0, 1, 0);
            Get<Border>("RightBatteryCell").Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            Get<Border>("CaseBatteryCell").Visibility = !single && modelLayout == "dual-case" ? Visibility.Visible : Visibility.Collapsed;
            Text("LeftBatteryLabel", single ? AirPodsModels.IsOverEar(target == null ? 0 : target.ModelId) ? "Headphones" : "Overall" : "Left");
            System.Windows.Automation.AutomationProperties.SetName(Get<TextBlock>("LeftBattery"), single ? "Overall headphone charge" : "Left earbud battery");
            Button("BatterySourceButton").Visibility = target != null && BatteryMonitor.IsSupportedModel(target.ModelId) ? Visibility.Visible : Visibility.Collapsed;
            Button("BatterySourceButton").IsEnabled = target != null && BatteryMonitor.IsSupportedModel(target.ModelId);
            Button("BatterySourceButton").Content = value != null && value.UserSelected ? "Change source ›" : "Choose source ›";
            Get<TextBlock>("BatteryStatus").Foreground = DarkBrush(value != null && value.UserSelected ? "#EABD78" : "#9A9A9A");
            BatteryCell("Left", single ? singlePercent : value == null ? null : value.Left,
                value != null && !windowsPercent.HasValue && (single ? value.LeftCharging || value.RightCharging : value.LeftCharging), !single && value != null && value.LeftInEar);
            BatteryCell("Right", single || value == null ? null : value.Right, !single && value != null && value.RightCharging, !single && value != null && value.RightInEar);
            BatteryCell("Case", single || value == null ? null : value.Case, !single && value != null && value.CaseCharging, false);
            if (single && windowsPercent.HasValue) Text("LeftDetail", "Last reported");
            if (value != null && (!single || !windowsPercent.HasValue))
            {
                Text("BatteryStatus", value.UserSelected ? "User-selected" : "Automatic");
                Text("BatteryNote", (value.UserSelected ? "Source identity not verified. " : "") + "Updated " + Math.Max(0, (int)(DateTime.UtcNow - value.Seen).TotalSeconds) + " s ago · Approx. 10% steps.");
                Get<TextBlock>("BatteryNote").ToolTip = value.Source;
            }
            else if (windowsPercent.HasValue)
            {
                Text("BatteryStatus", "Windows");
                Text("BatteryNote", "Last reported by Windows. Charge updates automatically when available.");
                Get<TextBlock>("BatteryNote").ToolTip = overall == null ? "Windows may cache the last reported device battery." : overall.Source;
            }
            else
            {
                Text("BatteryStatus", target == null ? "Select headphones" : target.Connected ? "Not reported" : "Not connected");
                Text("BatteryNote", target == null ? "Choose your paired headphones to see battery information." : target.Connected ?
                    "Audio is connected. A verified battery reading hasn't been shared for this pair." :
                    "Battery readings appear automatically when your headphones report them.");
                Get<TextBlock>("BatteryNote").ToolTip = target != null && target.Connected ?
                    "Battery availability varies by model and firmware. Windows has not exposed a value for this pair. Broadcasts are shown only when their identity is matched or explicitly selected. Percentages are never guessed. " + battery.Status : battery.Status;
            }
        }
        private void BatteryCell(string prefix, int? percent, bool charging, bool inEar)
        {
            Text(prefix + "Battery", BatteryReading.Percent(percent));
            Get<ProgressBar>(prefix + "Bar").Value = percent ?? 0;
            Get<ProgressBar>(prefix + "Bar").Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(percent.HasValue && percent.Value <= 20 ? "#EABD78" : "#F5F5F5"));
            Get<ProgressBar>(prefix + "Bar").Visibility = percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            Text(prefix + "Detail", percent.HasValue ? charging ? "Charging" : inEar ? "In ear" : "Last broadcast" : "No reading");
            Get<TextBlock>(prefix + "Detail").Visibility = percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBusyIndicator()
        {
            var bar = Get<ProgressBar>("BusyBar");
            // Collapsing a WPF progress bar alone can leave its template clocks ticking.
            bar.IsIndeterminate = !exiting && capturePath == null && SystemParameters.ClientAreaAnimation &&
                bar.IsVisible && window.WindowState != WindowState.Minimized;
        }

        private void SetStatus(string title, string message, bool progress, bool warning)
        {
            Text("StatusTitle", title);
            Get<TextBlock>("StatusTitle").Visibility = progress || warning ? Visibility.Visible : Visibility.Collapsed;
            Get<ProgressBar>("BusyBar").Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            UpdateBusyIndicator();
            var panel = Get<Border>("StatusPanel");
            panel.ToolTip = new TextBlock { Text = title + Environment.NewLine + message, MaxWidth = 300, TextWrapping = TextWrapping.Wrap };
            Get<TextBlock>("StatusTitle").Foreground = DarkBrush(warning ? "#EABD78" : "#F2F2F2");
            System.Windows.Automation.AutomationProperties.SetName(panel, Get<TextBlock>("ConnectionBadge").Text + ". " + title);
            System.Windows.Automation.AutomationProperties.SetHelpText(panel, message);
            if (panel.IsKeyboardFocused) ShowStatusDetails();
        }

        private void PreferencesChanged(object sender, RoutedEventArgs e)
        {
            if (!preferencesLoaded) return;
            settings.RouteAudio = Get<CheckBox>("AutoRouteCheck").IsChecked == true;
            settings.PowerOffBluetoothOnDisconnect = Get<CheckBox>("PowerOffBluetoothCheck").IsChecked == true;
            settings.CloseToTray = Get<CheckBox>("TrayCheck").IsChecked == true;
            settings.Hotkey = Get<CheckBox>("HotkeyCheck").IsChecked == true;
            SavePreferences(); UpdateHotkey(); UpdateDevice();
        }
        private void SavePreferences()
        {
            if (capturePath != null) return;
            try { settings.Save(); }
            catch (Exception ex) { SetStatus("Could not save preferences", ex.Message, false, true); }
        }
        private void UpdateHotkey()
        {
            if (hwndSource == null || capturePath != null) return;
            UnregisterHotKey(hwndSource.Handle, 0x4F43);
            bool registered = settings.Hotkey && RegisterHotKey(hwndSource.Handle, 0x4F43, 0x4006, 0x41);
            Text("ShortcutHint", registered ? "Ctrl + Shift + A to connect" : settings.Hotkey ? "Shortcut in use by another app" : "Keyboard shortcut is off");
            Get<TextBlock>("ShortcutHint").Visibility = settings.Hotkey && !registered ? Visibility.Visible : Visibility.Collapsed;
        }
        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == 0x4F43)
            {
                handled = true; window.Dispatcher.BeginInvoke(new Action(async () => await ConnectAsync()));
            }
            return IntPtr.Zero;
        }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "Karmac · Click to open", Visible = true };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Karmac.ico"))
                tray.Icon = stream == null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Connect saved headphones", null, async (sender, e) => await ConnectAsync());
            menu.Items.Add("Open Karmac", null, (sender, e) => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (sender, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (sender, e) => {
                if (e.Button == Forms.MouseButtons.Left) { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
            };
        }

        private void ShowNotices()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices.txt"))
            using (var reader = new StreamReader(stream))
            {
                var text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    Background = DarkBrush("#000000"), Foreground = DarkBrush("#F2F2F2"), BorderBrush = DarkBrush("#303030"),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontSize = 12 };
                var dialog = new Window { Title = "Karmac · Open-source notices", Owner = window, Width = 600, Height = 480,
                    Background = DarkBrush("#000000"), Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                SetDarkTitleBarOnLoad(dialog);
                dialog.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), window.FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)));
                dialog.ShowDialog();
            }
        }
        private void OpenBluetoothSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Could not open Windows Settings", ex.Message, false, true); }
        }
        private void Capture(string path)
        {
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
        private void Quit() { exiting = true; timer.Stop(); connectionTimer.Stop(); app.Shutdown(); }
        private static SolidColorBrush DarkBrush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
        private static void SetDarkTitleBarOnLoad(Window dialog)
        {
            dialog.SourceInitialized += (sender, e) => {
                try
                {
                    int enabled = 1;
                    IntPtr handle = new WindowInteropHelper(dialog).Handle;
                    DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
                    int black = 0, white = 0x00F2F2F2;
                    DwmSetWindowAttribute(handle, 35, ref black, sizeof(int));
                    DwmSetWindowAttribute(handle, 36, ref white, sizeof(int));
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
        }
        public void Dispose()
        {
            SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged;
            artworkMotion.Update(false, false);
            timer.Stop(); connectionTimer.Stop(); battery.Dispose();
            if (keyboardStatusTip != null) keyboardStatusTip.IsOpen = false;
            if (activationWait != null) activationWait.Unregister(null);
            if (hwndSource != null) { UnregisterHotKey(hwndSource.Handle, 0x4F43); hwndSource.RemoveHook(WindowMessage); }
            if (tray != null) { tray.Visible = false; tray.Icon.Dispose(); tray.Dispose(); }
        }
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    }
}
