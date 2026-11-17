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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Karmac")]
[assembly: AssemblyProduct("Karmac")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AY-Karma and contributors")]
[assembly: AssemblyInformationalVersion("1.0.0-preview.2")]
[assembly: AssemblyDescription("One-click AirPods for Windows, from the taskbar")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.1")]

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
                    if (capture == null) AppController.PrefetchDiscovery();
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

    // Karmac lives in the notification area. The flyout rises from the taskbar on a
    // tray click; the connection card rises on its own when the AirPods connect or
    // their case opens nearby; three battery icons sit beside the clock while connected.
    internal sealed class AppController : IDisposable
    {
        private readonly Application app;
        private readonly Window window, card;
        private readonly TrayPopup flyout, cardPopup;
        private readonly Preferences settings;
        private readonly BatteryMonitor battery = new BatteryMonitor();
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer connectionTimer;
        private readonly DispatcherTimer cardTimer;
        private readonly ConnectionTrace connectionTrace;
        private readonly ArtworkMotion artworkMotion = new ArtworkMotion();
        private IConnectionPlatform connectionPlatform;
        private readonly DeviceEvents deviceEvents = new DeviceEvents();
        // Coalesces bursts of device events into one state read / one discovery.
        private readonly DispatcherTimer audioSettle, devicesSettle;
        private CancellationTokenSource connectCancel;
        // The output that was active before Karmac switched to the headphones.
        private AudioService.OutputSnapshot previousOutput;
        // Last seen "ready for media" state of the selected pair; null until first observed.
        private bool? lastReady;
        private bool suppressAutoSwitch;
        private DateTime quietUntil = DateTime.MinValue;
        private readonly Dictionary<string, int> batteryWarned = new Dictionary<string, int>();
        private DateTime nextCaseCard = DateTime.MinValue;
        private Action balloonAction;
        private bool keyboardNavigation;
        private readonly string capturePath;
        private Forms.NotifyIcon tray;
        private HwndSource hwndSource;
        private RegisteredWaitHandle activationWait;
        private bool busy, refreshing, polling, exiting, initialized, preferencesLoaded;
        private long stateRevision;
        private int safetyTick;
        private List<AudioTarget> targets = new List<AudioTarget>();
        private Func<Task<DiscoveryResult>> discoverDevices = DeviceDiscovery.DiscoverAsync;
        private readonly Dictionary<string, ImageSource> artwork = new Dictionary<string, ImageSource>();
        private readonly Dictionary<string, ArtworkImage> deviceArtwork = new Dictionary<string, ArtworkImage>();
        private readonly Dictionary<string, DateTime> artworkRequested = new Dictionary<string, DateTime>();
        private int artworkRevision;
        private string radioState = "Unknown";
        // The last status reported by an operation; shown when it's a warning or in progress.
        private string statusTitle = "", statusMessage = "";
        private bool statusProgress, statusWarning;
        private bool caseCardShowing;
        private ComboBox Picker { get { return Get<ComboBox>("DevicePicker"); } }
        private AudioTarget Selected { get { return Picker.SelectedItem as AudioTarget; } }

        internal AppController(Application application, string capture, EventWaitHandle activation)
        {
            app = application; capturePath = capture; settings = Preferences.Load();
            connectionTrace = new ConnectionTrace(SystemParameters.ClientAreaAnimation);
            if (capture != null) Motion.Suppressed = true;
            Theme.Read();
            var artworkResources = Load<ResourceDictionary>("Artwork.xaml");
            window = Load<Window>("Flyout.xaml");
            card = Load<Window>("Card.xaml");
            foreach (var surface in new[] { window, card })
            {
                surface.Resources.MergedDictionaries.Add(artworkResources);
                Theme.Apply(surface.Resources);
            }
            flyout = new TrayPopup(window, Get<Border>("Sheet"), 12, 0.86);
            cardPopup = new TrayPopup(card, (Border)card.FindName("Sheet"), 12, 0.7);
            Theme.Changed += () => { Theme.Apply(window.Resources); Theme.Apply(card.Resources); UpdateDevice(); UpdateBattery(); UpdateTray(); };
            if (capture == null) Theme.Watch();
            app.MainWindow = window;
            Motion.AttachSwitches();
            Get<Image>("DeviceArtwork").RenderTransform = artworkMotion.Transform;
            window.IsVisibleChanged += (sender, e) => UpdateArtworkMotion();
            window.Deactivated += (sender, e) => { if (!exiting && capturePath == null && !Picker.IsDropDownOpen) flyout.Close(); };
            SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged;
            if (capturePath == null)
                activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (state, timedOut) => {
                    if (exiting || window.Dispatcher.HasShutdownStarted) return;
                    window.Dispatcher.BeginInvoke(new Action(() => { if (!exiting) OpenFlyout(); }));
                }, null, Timeout.Infinite, false);
            connectionPlatform = new WindowsConnectionPlatform { NearbyProvider = battery.Nearby };

            Get<ItemsControl>("ConnectionSteps").ItemsSource = connectionTrace.Steps;
            Get<ItemsControl>("ConnectionStepLabels").ItemsSource = connectionTrace.Steps;
            // Each figure counts with its ring's sweep.
            foreach (string part in new[] { "Left", "Right", "Case" })
            {
                var ring = Get<RingGauge>(part + "Ring"); var figure = Get<TextBlock>(part + "Pct");
                ring.ValueChanged += (sender, e) => figure.Text = ring.Figure;
                var cardRing = (RingGauge)card.FindName("Card" + part + "Ring"); var cardFigure = (TextBlock)card.FindName("Card" + part + "Pct");
                cardRing.ValueChanged += (sender, e) => cardFigure.Text = cardRing.Figure;
            }
            Button("PrimaryButton").Click += async (sender, e) => await PrimaryActionAsync();
            Button("SecondaryButton").Click += async (sender, e) => await SecondaryActionAsync();
            Get<ToggleButton>("ConnectionToggle").Click += async (sender, e) => {
                // Switching off while a connection is in progress cancels it.
                if (connectCancel != null) { CancelConnect(); UpdateDevice(); return; }
                if (busy) { UpdateDevice(); return; }
                if (Get<ToggleButton>("ConnectionToggle").IsChecked == true) await ConnectAsync(true);
                else await DisconnectAsync();
            };
            Button("RefreshButton").Click += async (sender, e) => {
                AudioArtwork.Invalidate(); deviceArtwork.Clear(); artworkRequested.Clear(); artworkRevision++;
                await RefreshAsync(false);
            };
            Button("BluetoothButton").Click += (sender, e) => OpenBluetoothSettings();
            Button("PairButton").Click += (sender, e) => OpenBluetoothSettings();
            Button("SettingsButton").Click += (sender, e) => ShowSettings(Get<Grid>("SettingsPage").Visibility != Visibility.Visible);
            Button("BackButton").Click += (sender, e) => ShowSettings(false);
            Button("QuitButton").Click += (sender, e) => Quit();
            Button("LicenseButton").Click += (sender, e) => ShowNotices();
            Text("VersionLabel", "Karmac " + Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion);
            window.PreviewKeyDown += (sender, e) => {
                keyboardNavigation = true;
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (connectCancel != null) CancelConnect();
                else if (Get<Grid>("SettingsPage").Visibility == Visibility.Visible) ShowSettings(false);
                else flyout.Close();
            };
            window.PreviewMouseDown += (sender, e) => keyboardNavigation = false;
            // Focus rings are for keyboard users: a mouse click shouldn't leave one behind.
            window.PreviewMouseLeftButtonUp += (sender, e) => window.Dispatcher.BeginInvoke(new Action(() => {
                if (!keyboardNavigation && !Picker.IsDropDownOpen && window.IsActive) Keyboard.ClearFocus();
            }), DispatcherPriority.Input);
            Picker.SelectionChanged += (sender, e) => {
                if (!refreshing)
                {
                    if (Selected != null)
                    {
                        settings.Favorite = Selected.ContainerId;
                        if (!Selected.RequiresPairingCheck) settings.SavedPair = SavedAirPods.FromTarget(Selected);
                        SavePreferences();
                    }
                    SetStatus(Selected == null ? "Choose your AirPods" : "AirPods selected",
                        Selected == null ? "Select your saved pair, or pair your AirPods in Windows." :
                        "Karmac connects this pair next time.", false, false);
                }
                WatchSelectedLink();
                // A different pair chosen by the user is a fresh start, not a transition.
                // A refresh re-selecting the same pair keeps the last observation.
                if (!refreshing) lastReady = Selected == null ? (bool?)null : Selected.PlaybackReady;
                UpdateDevice(); UpdateBattery();
            };

            Get<CheckBox>("AutoRouteCheck").IsChecked = settings.RouteAudio;
            Get<CheckBox>("SwitchWhenConnectedCheck").IsChecked = settings.SwitchWhenConnected;
            Get<CheckBox>("NotificationsCheck").IsChecked = settings.Notifications;
            Get<CheckBox>("HotkeyCheck").IsChecked = settings.Hotkey;
            foreach (string name in new[] { "AutoRouteCheck", "SwitchWhenConnectedCheck",
                "NotificationsCheck", "HotkeyCheck" })
            {
                Get<CheckBox>(name).Checked += PreferencesChanged;
                Get<CheckBox>(name).Unchecked += PreferencesChanged;
            }
            preferencesLoaded = true;

            ((Button)card.FindName("CardClose")).Click += (sender, e) => { e.Handled = true; CloseCard(); };
            ((Button)card.FindName("CardAction")).Click += async (sender, e) => { e.Handled = true; await ConnectFromCardAsync(); };
            ((Border)card.FindName("Sheet")).MouseLeftButtonUp += (sender, e) => {
                if (e.Handled) return;
                CloseCard(); OpenFlyout();
            };
            card.MouseEnter += (sender, e) => cardTimer.Stop();
            card.MouseLeave += (sender, e) => { if (cardPopup.IsOpen) Restart(cardTimer); };
            cardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            cardTimer.Tick += (sender, e) => CloseCard();

            // Device state arrives through DeviceEvents. This 1 s tick only refreshes
            // UI-side values (battery age, ear detection, low-battery checks) and runs a
            // slow safety-net read in case Windows drops an event.
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            audioSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            audioSettle.Tick += async (sender, e) => { audioSettle.Stop(); await PollStatesAsync(); };
            devicesSettle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            devicesSettle.Tick += async (sender, e) => { devicesSettle.Stop(); await RefreshAsync(true); };
            connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            connectionTimer.Tick += (sender, e) => { connectionTrace.Tick(); Text("ConnectionElapsed", connectionTrace.Elapsed); };
            connectionTrace.Changed += () => {
                if (connectionTrace.Steps.Count > 0 && connectionTrace.Steps[0].State == ConnectionStepState.Complete)
                    ApplyRadioState("On");
                if (!connectionTrace.Running) connectionTimer.Stop();
                UpdateDevice(); UpdateBattery();
            };
            timer.Tick += async (sender, e) => {
                CheckBatteryEvents();
                safetyTick++;
                if (window.IsVisible && safetyTick % 3 == 0) UpdateBattery();
                if (busy || refreshing) return;
                if (safetyTick % 300 == 0) await RefreshAsync(true);
                else if (safetyTick % 30 == 0) await PollStatesAsync();
            };
            var coldSaved = settings.SavedPair == null ? null : settings.SavedPair.Restore(settings.Favorite);
            if (coldSaved != null)
            {
                refreshing = true; targets.Add(coldSaved); Picker.ItemsSource = targets; Picker.SelectedItem = coldSaved;
                refreshing = false;
            }
            // The flyout's handle exists before it's ever shown, for the hotkey.
            hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
            hwndSource.AddHook(WindowMessage); UpdateHotkey();
            UpdateDevice(); UpdateBattery();
        }

        // Loads embedded XAML; the "karmac" namespace maps to this assembly's controls.
        private static T Load<T>(string resource)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream))
            {
                string xaml = reader.ReadToEnd().Replace("xmlns:k=\"karmac\"",
                    "xmlns:k=\"clr-namespace:Karmac;assembly=" + typeof(RingGauge).Assembly.GetName().Name + "\"");
                return (T)XamlReader.Parse(xaml);
            }
        }

        internal void Show()
        {
            if (capturePath != null)
            {
                window.Left = SystemParameters.VirtualScreenLeft - window.Width - 256;
                window.ShowActivated = false; window.Show();
                window.Dispatcher.BeginInvoke(new Action(async () => await StartAsync()));
                return;
            }
            window.Dispatcher.BeginInvoke(new Action(async () => {
                OpenFlyout();
                await StartAsync();
            }));
        }

        private async Task StartAsync()
        {
            if (initialized) return; initialized = true;
            // Discovery runs on a worker thread: start it first and set up the tray and
            // listeners while it works, instead of one after the other.
            var firstRefresh = RefreshAsync(false);
            if (capturePath == null) { CreateTray(); battery.Start(); StartDeviceEvents(); }
            await firstRefresh;
            if (capturePath != null) { await Task.Delay(350); Capture(capturePath); Quit(); }
            else timer.Start();
        }

        private T Get<T>(string name) where T : class { return (T)window.FindName(name); }
        private Button Button(string name) { return Get<Button>(name); }
        private void Text(string name, string text) { Get<TextBlock>(name).Text = text; }
        private Brush Brush(string key) { return (Brush)window.FindResource(key); }
        private static string DeviceTerm(AudioTarget target)
        { return target == null || target.ModelId == 0 ? "headphones" : "AirPods"; }

        // ---- Surfaces -------------------------------------------------------------------

        private void ToggleFlyout()
        {
            if (flyout.IsOpen) { flyout.Close(); return; }
            if (flyout.JustHidden) return; // this click is the one that dismissed it
            OpenFlyout();
        }

        private void OpenFlyout()
        {
            if (flyout.IsOpen) { window.Activate(); return; }
            if (cardPopup.IsOpen) CloseCard();
            ShowSettings(false, false);
            UpdateDevice(); UpdateBattery();
            flyout.Open(true);
            // The page settles in after the sheet: art first, then the charge sweeps.
            Motion.Enter(Get<Image>("DeviceArtwork"), 0, 10, 260, 20);
            Motion.Enter(Get<Grid>("BatteryRow"), 0, 6, 220, 50);
            UpdateBattery(true);
        }

        private void ShowSettings(bool show, bool animate = true)
        {
            var page = Get<Grid>("SettingsPage");
            var home = Get<StackPanel>("HomePage");
            if ((page.Visibility == Visibility.Visible) == show) return;
            Morph(() => {
                page.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                home.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }, animate);
            if (animate) Motion.Page(show ? (UIElement)page : home, show ? 1 : -1);
            if (show) Get<ScrollViewer>("SettingsScroll").ScrollToTop();
            Button("SettingsButton").Tag = show ? "Active" : null;
            Button("SettingsButton").ToolTip = show ? "Close settings" : "Settings";
            if (keyboardNavigation) (show ? Button("BackButton") : Button("SettingsButton")).Focus();
        }

        // Applies a change and lets the sheet's height glide to the new content size. The
        // sheet is anchored to the taskbar, so it grows and shrinks upward.
        private void Morph(Action change, bool animate = true)
        {
            var host = Get<Grid>("PageHost");
            double from = host.ActualHeight;
            change();
            if (!animate || Motion.Suppressed || !window.IsVisible || !SystemParameters.ClientAreaAnimation || from <= 0) return;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.Measure(new Size(host.ActualWidth, double.PositiveInfinity));
            double to = host.DesiredSize.Height;
            if (Math.Abs(to - from) < 1) return;
            var glide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new SpringEase { Damping = 0.92 }, FillBehavior = FillBehavior.Stop };
            host.BeginAnimation(FrameworkElement.HeightProperty, glide);
        }

        // ---- Actions ----------------------------------------------------------------------

        private async Task PrimaryActionAsync()
        {
            var target = Selected;
            if (radioState == "No adapter" || target == null && !Picker.HasItems) { OpenBluetoothSettings(); return; }
            if (target == null) { Picker.Focus(); Picker.IsDropDownOpen = true; return; }
            if (target.Connected && radioState == "On" && !target.IsDefault) { await RouteAsync(); return; }
            await ConnectAsync(true);
        }

        private async Task SecondaryActionAsync()
        {
            if (connectCancel != null) { CancelConnect(); UpdateDevice(); return; }
            await SwitchBackAsync();
        }

        // Discovery started in Main, in parallel with window setup. Used once, by the first refresh.
        private static Task<DiscoveryResult> prefetchedDiscovery;
        internal static void PrefetchDiscovery() { prefetchedDiscovery = Task.Run(() => DeviceDiscovery.DiscoverAsync()); }
        private Task<DiscoveryResult> TakeDiscoveryAsync()
        {
            var early = prefetchedDiscovery; prefetchedDiscovery = null;
            if (early != null && !early.IsFaulted && !early.IsCanceled) return early;
            return Task.Run(() => discoverDevices());
        }

        private async Task RefreshAsync(bool quiet)
        {
            if (refreshing || busy) return;
            refreshing = true; long discoveryRevision = ++stateRevision; UpdateDevice();
            if (!quiet) SetStatus("Finding your AirPods", "Reading paired devices. No nearby device will be paired automatically.", true, false);
            try
            {
                var result = await TakeDiscoveryAsync();
                if (busy || discoveryRevision != stateRevision) return;
                string selectedId = Selected == null ? settings.Favorite : Selected.ContainerId;
                targets = result.Targets; radioState = result.Radio;
                if (settings.SavedPair != null && !targets.Any(t => DeviceDiscovery.SameGuid(t.ContainerId, settings.Favorite)))
                {
                    var saved = settings.SavedPair.Restore(settings.Favorite);
                    if (saved != null) targets.Add(saved);
                }
                RegisterBatteryTargets();
                ApplyRadioState(radioState);
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
                    if (radioState == "No adapter") SetStatus("No Bluetooth adapter", "Connect or enable your Bluetooth adapter, then refresh.", false, true);
                    else SetStatus("", "", false, false);
                    if (result.Warning != null) SetStatus("Some device details are unavailable", result.Warning + " You can still try connecting a listed device.", false, true);
                }
                if (Selected != null) await ObserveReadyAsync(Selected);
            }
            catch (Exception ex) { if (discoveryRevision == stateRevision) SetStatus("Couldn't read devices", ex.Message, false, true); Log.Write(ex.ToString()); }
            finally { if (discoveryRevision == stateRevision) refreshing = false; UpdateDevice(); UpdateBattery(); UpdateTray(); }
        }

        private async Task ConnectAsync(bool explicitAudioSwitch = false)
        {
            if (busy || Selected == null) return;
            var target = Selected;
            busy = true; refreshing = false; stateRevision++;
            connectCancel = new CancellationTokenSource();
            UpdateDevice();
            connectionTimer.Start();
            try
            {
                Exception failure = null;
                try
                {
                    var result = await new ConnectionFlow(connectionPlatform).ConnectAsync(target, settings.RouteAudio,
                        explicitAudioSwitch, connectionTrace, connectCancel.Token);
                    if (result.PreviousOutput != null) RememberPreviousOutput(result.PreviousOutput, target);
                    ApplyRadioState("On"); battery.Start(); WatchSelectedLink();
                    SetStatus(result.CallsOnly ? connectionTrace.Title : "", connectionTrace.Detail + ". Took " + connectionTrace.Elapsed + ".",
                        false, result.CallsOnly);
                    if (!result.AlreadyConnected && !result.CallsOnly) NotifyConnected(target);
                }
                catch (Exception error)
                {
                    // A driver request already sent can't be recalled, so the pair may still
                    // finish connecting. Don't move audio or announce it if that happens.
                    quietUntil = DateTime.UtcNow.AddSeconds(20);
                    if (error is OperationCanceledException && connectCancel.IsCancellationRequested)
                        SetStatus("Connection cancelled", "Stopped trying. If they were already partway connected, they may still finish.", false, false);
                    else { failure = error; Log.Write("Connect: " + error); }
                }
                if (failure != null)
                {
                    try { ApplyRadioState(await connectionPlatform.ReadRadioStateAsync()); }
                    catch (Exception error) { Log.Write("Connect radio verification: " + error.Message); }
                    SetStatus(target.Connected ? "Connected, but audio needs attention" : "Couldn't connect", failure.Message, false, true);
                }
            }
            finally
            {
                // Not disposed: a page still in flight may hold the token for a few seconds.
                busy = false; connectionTimer.Stop(); connectCancel = null;
                if (Selected == target) lastReady = target.PlaybackReady;
                if (!target.RequiresPairingCheck) { settings.SavedPair = SavedAirPods.FromTarget(target); SavePreferences(); }
                RegisterBatteryTargets();
                UpdateDevice(); UpdateBattery(); UpdateTray();
            }
        }

        private void CancelConnect()
        {
            if (connectCancel == null || connectCancel.IsCancellationRequested) return;
            connectCancel.Cancel();
            connectionTrace.Note("Cancelling");
        }

        private Task DisconnectAsync() { return DisconnectHeadphonesAsync(); }

        private async Task DisconnectHeadphonesAsync()
        {
            var target = Selected;
            if (busy || target == null || target.RequiresPairingCheck) return;
            busy = true; refreshing = false; stateRevision++;
            connectionTrace.Reset(); UpdateDevice();
            SetStatus("Disconnecting", "Only your AirPods disconnect. Bluetooth stays on for your other devices.", true, false);
            Exception failure = null;
            bool linkDropped = false;
            // A disconnect we asked for must not trigger "switch when they connect" or
            // restore logic on the way down.
            suppressAutoSwitch = true;
            try
            {
                linkDropped = await new ConnectionFlow(connectionPlatform).DisconnectAsync(target, CancellationToken.None);
                RestorePreviousOutput();
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
                if (failure != null) SetStatus("Couldn't disconnect", failure.Message, false, true);
                else if (linkDropped) SetStatus("", "Your AirPods are still paired. Bluetooth is still on.", false, false);
                else SetStatus("Audio disconnected", "Your AirPods still hold a Bluetooth link and may reconnect on their own. Put them in the case to fully disconnect.", false, true);
            }
            catch (Exception observationError)
            {
                Log.Write("Disconnect state observation: " + observationError.Message);
                SetStatus("Couldn't verify their state", (failure == null ? "" : failure.Message + " ") + observationError.Message, false, true);
            }
            finally
            {
                busy = false; suppressAutoSwitch = false;
                if (Selected == target) lastReady = target.PlaybackReady;
                RegisterBatteryTargets();
                UpdateDevice(); UpdateBattery(); UpdateTray();
            }
        }

        private void ApplyRadioState(string state)
        {
            radioState = state;
            bool? on = state == "On" ? (bool?)true : state == "Off" || state == "No adapter" ? (bool?)false : null;
            Button("BluetoothButton").ToolTip = (state == "On" ? "Bluetooth on" : state == "Off" ? "Bluetooth off" : state == "No adapter" ? "No Bluetooth adapter" : "Bluetooth") + " · open settings";
            Motion.Tint(Get<System.Windows.Shapes.Ellipse>("RadioDot"), ((SolidColorBrush)Brush(on == true ? "On" : on == false ? "Danger" : "Off")).Color);
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
            SetStatus("Switching audio", "Making your AirPods the Windows output…", true, false);
            try
            {
                RememberPreviousOutput(await connectionPlatform.RouteAsync(target), target);
                target.IsDefault = true;
                SetStatus("", "Your AirPods are now the Windows output. Apps set to a specific output keep using it.", false, false);
            }
            catch (Exception ex) { target.IsDefault = false; SetStatus("Couldn't switch audio", ex.Message, false, true); }
            finally { busy = false; UpdateDevice(); UpdateTray(); }
        }

        // Keep the output that was active before the first switch to these headphones,
        // so a later switch or reconnect doesn't overwrite it with the headphones themselves.
        private void RememberPreviousOutput(AudioService.OutputSnapshot snapshot, AudioTarget target)
        {
            if (snapshot == null || snapshot.Media == null || target.Endpoints.Any(e => String.Equals(e.Id, snapshot.Media, StringComparison.OrdinalIgnoreCase)))
                return;
            previousOutput = snapshot;
        }

        // After the headphones leave, Windows picks some other output on its own. Put back
        // the one the user had before Karmac switched, then check Windows reports it. If
        // that output is gone, forget it rather than applying it at some later disconnect.
        private void RestorePreviousOutput()
        {
            var snapshot = previousOutput;
            previousOutput = null;
            if (snapshot == null) return;
            Task.Run(() => {
                if (!AudioService.IsActiveEndpoint(snapshot.Media)) return;
                ApplyOutput(snapshot); // SetDefault verifies every role and throws otherwise
            }).ContinueWith(task => {
                if (task.IsFaulted) Log.Write("Restore output: " + task.Exception.GetBaseException().Message);
            });
        }

        private static void ApplyOutput(AudioService.OutputSnapshot snapshot)
        {
            AudioService.SetDefault(snapshot.Media, AudioService.IsActiveEndpoint(snapshot.Calls) ? snapshot.Calls : snapshot.Media);
        }

        private void RegisterBatteryTargets() { battery.RegisterTargets(targets.Where(t => !t.RequiresPairingCheck).ToList()); }

        private async Task SwitchBackAsync()
        {
            var snapshot = previousOutput;
            if (busy || snapshot == null) return;
            try
            {
                await Task.Run(() => ApplyOutput(snapshot));
                previousOutput = null;
                if (Selected != null) Selected.IsDefault = false;
                SetStatus("", "Windows audio is back on your previous output.", false, false);
            }
            catch (Exception error) { SetStatus("Couldn't switch back", error.Message, false, true); }
            UpdateDevice(); UpdateTray();
        }

        private void StartDeviceEvents()
        {
            deviceEvents.AudioChanged += () => Post(() => Restart(audioSettle));
            deviceEvents.LinkChanged += () => Post(() => Restart(audioSettle));
            deviceEvents.DevicesChanged += () => Post(() => Restart(devicesSettle));
            deviceEvents.RadioChanged += () => Post(async () => await RadioChangedAsync());
            battery.Appeared += reading => Post(() => ShowCaseCard(reading));
            deviceEvents.Start();
            WatchSelectedLink();
        }

        private void Post(Action action)
        {
            if (exiting || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(action);
        }

        private static void Restart(DispatcherTimer timerToRestart) { timerToRestart.Stop(); timerToRestart.Start(); }

        private void WatchSelectedLink()
        {
            if (capturePath == null) deviceEvents.WatchLink(Selected == null ? null : Selected.BluetoothId);
        }

        // Bluetooth turned on or off from Windows (or another app): restart the battery
        // listener and refresh devices when it comes back.
        private async Task RadioChangedAsync()
        {
            if (busy) return;
            try { ApplyRadioState(await connectionPlatform.ReadRadioStateAsync()); }
            catch (Exception error) { Log.Write("Radio change: " + error.Message); return; }
            if (radioState == "On") { battery.Start(); WatchSelectedLink(); Restart(devicesSettle); }
            else if (lastReady == true) { lastReady = false; RestorePreviousOutput(); }
            UpdateDevice(); UpdateBattery(); UpdateTray();
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
                    // A cold saved pair has no known endpoints yet; find them by container.
                    var endpoints = target.Endpoints.Count > 0 ? AudioService.ReadStates(target) :
                        AudioService.Enumerate().Where(t => DeviceDiscovery.SameGuid(t.ContainerId, target.ContainerId))
                            .SelectMany(t => t.Endpoints).ToList();
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
                    if (wasConnected != target.Connected) RegisterBatteryTargets();
                    await ObserveReadyAsync(target);
                    UpdateDevice(); UpdateBattery(); UpdateTray();
                }
            }
            catch (Exception ex) { Log.Write("State poll: " + ex.Message); }
            finally { polling = false; }
        }

        // Reacts once to the selected pair becoming ready for media or dropping out,
        // whichever path noticed first (device event, safety net, or a refresh). The
        // first observation only records state, so startup never counts as a transition.
        private async Task ObserveReadyAsync(AudioTarget target)
        {
            bool ready = target.PlaybackReady && radioState == "On";
            bool? before = lastReady;
            lastReady = ready;
            if (before == null || before.Value == ready || suppressAutoSwitch) return;
            if (!ready) RestorePreviousOutput();
            else if (DateTime.UtcNow >= quietUntil) await ConnectedOnTheirOwnAsync(target);
        }

        // The pair connected without a click (case opened, handed over from a phone).
        private async Task ConnectedOnTheirOwnAsync(AudioTarget target)
        {
            if (settings.SwitchWhenConnected && !target.IsDefault && !target.RequiresPairingCheck)
            {
                try
                {
                    RememberPreviousOutput(await connectionPlatform.RouteAsync(target), target);
                    target.IsDefault = true;
                }
                catch (Exception error) { Log.Write("Automatic switch: " + error.Message); }
            }
            SetStatus("", target.IsDefault ? "Your AirPods connected and are now the Windows output." :
                "Your AirPods connected. Use Play on this PC to hear them here.", false, false);
            NotifyConnected(target);
        }

        // ---- Rendering ----------------------------------------------------------------------

        private void SetStatus(string title, string message, bool progress, bool warning)
        {
            statusTitle = title ?? ""; statusMessage = message ?? ""; statusProgress = progress; statusWarning = warning;
            UpdateDevice();
        }

        private void UpdateDevice()
        {
            var target = Selected;
            bool radioAvailable = radioState != "Off" && radioState != "No adapter";
            bool connecting = connectCancel != null;
            bool connected = target != null && radioAvailable && target.Connected;
            bool needsSetup = target == null || radioState == "No adapter";
            bool showSteps = connectionTrace.HasStarted && (connectionTrace.Running || connectionTrace.Failed);

            // Status chip: the state in one word, with the signature dot.
            string badge = target == null ? refreshing ? "Looking" : "No AirPods" : connecting ? "Connecting" :
                connected ? target.CallsOnly ? "Calls only" : "Connected" : radioState == "Off" ? "Bluetooth off" : "Not connected";
            Text("ConnectionBadge", badge);
            string dot = connected ? "On" : connecting ? "Accent" : target == null ? "Off" : "Danger";
            var dotColor = ((SolidColorBrush)Brush(dot)).Color;
            Motion.Tint(Get<System.Windows.Shapes.Ellipse>("ConnectionDot"), dotColor);
            Motion.Tint(Get<System.Windows.Shapes.Ellipse>("DotHalo"), dotColor);

            // One line under the name says what is true right now.
            string line;
            bool warn = false;
            if (statusWarning && !String.IsNullOrEmpty(statusTitle) && !(connected && statusTitle == "Couldn't connect")) { line = statusTitle; warn = true; }
            else if (statusProgress && !String.IsNullOrEmpty(statusTitle)) line = statusTitle + "…";
            else if (connecting) line = "Connecting to your " + DeviceTerm(target);
            else if (radioState == "No adapter") { line = "No Bluetooth adapter found"; warn = true; }
            else if (target == null) line = refreshing ? "Looking for your AirPods…" : targets.Count > 0 ? "Choose your AirPods above" : "Pair your AirPods in Windows to begin";
            else if (radioState == "Off") line = target.ModelName + " · Bluetooth off";
            else if (connected && target.CallsOnly) line = "Calls only";
            else if (connected && !target.IsDefault) line = "Another output is playing";
            else line = target.ModelName;
            var status = Get<TextBlock>("StatusLine");
            status.Text = line;
            status.Foreground = Brush(warn ? "Warn" : "Muted2");
            status.ToolTip = String.IsNullOrEmpty(statusMessage) ? null : (statusTitle.Length > 0 ? statusTitle + Environment.NewLine : "") + statusMessage;
            System.Windows.Automation.AutomationProperties.SetName(status, badge + ". " + line);

            // The switch always follows the AirPods; switching it off while connecting cancels.
            var toggle = Get<ToggleButton>("ConnectionToggle");
            toggle.IsChecked = connecting || connected;
            toggle.IsEnabled = connecting || !busy && radioState != "No adapter" && target != null;
            toggle.Visibility = target == null ? Visibility.Collapsed : Visibility.Visible;
            string help = connecting ? "Off stops connecting." : "On connects your AirPods, turning Bluetooth on if needed. Off disconnects only them.";
            toggle.ToolTip = help;
            System.Windows.Automation.AutomationProperties.SetHelpText(toggle, help);
            System.Windows.Automation.AutomationProperties.SetName(toggle, "AirPods connection. " + (toggle.IsChecked == true ? "On." : "Off."));
            Picker.IsEnabled = !busy && !refreshing;
            Button("RefreshButton").IsEnabled = !busy && !refreshing;

            // Primary action, only when there's something worth doing.
            string primary = null;
            if (!connecting && !busy)
            {
                if (radioState == "No adapter") primary = "Open Bluetooth settings";
                else if (target == null) primary = targets.Count > 0 ? "Choose your AirPods" : "Pair AirPods";
                else if (!connected) primary = radioState == "Off" ? "Turn on Bluetooth and connect" : "Connect";
                else if (!target.IsDefault && target.PlaybackReady) primary = "Play on this PC";
            }
            string secondary = connecting && !connectCancel.IsCancellationRequested ? "Cancel" :
                !busy && connected && target.IsDefault && previousOutput != null ? "Switch back to previous output" : null;

            Morph(() => {
                Motion.SetShown(Get<Border>("ConnectionStream"), showSteps, 8);
                Motion.SetShown(Button("PrimaryButton"), primary != null, 6);
                Motion.SetShown(Button("SecondaryButton"), secondary != null, 6);
                Get<StackPanel>("Actions").Visibility = primary != null || secondary != null ? Visibility.Visible : Visibility.Collapsed;
            });
            if (primary != null) { Button("PrimaryButton").Content = primary; System.Windows.Automation.AutomationProperties.SetName(Button("PrimaryButton"), primary); }
            if (secondary != null) Button("SecondaryButton").Content = secondary;
            Button("SecondaryButton").Margin = new Thickness(0, primary != null ? 8 : 0, 0, 0);

            if (showSteps)
            {
                Text("ConnectionTitle", connectionTrace.Running ? connectionTrace.Title ?? "Connecting" : "Couldn't connect");
                Text("ConnectionDetail", connectionTrace.Detail ?? "");
                Text("ConnectionElapsed", connectionTrace.Elapsed);
                Get<TextBlock>("ConnectionTitle").Foreground = Brush(connectionTrace.Failed && !connectionTrace.Running ? "Danger" : "Ink");
            }
            UpdateArtwork(target);
        }

        private void UpdateArtwork(AudioTarget target)
        {
            UpdateArtworkMotion();
            var image = Get<Image>("DeviceArtwork");
            var source = ArtworkFor(target, image);
            bool changed = image.Source != null && !ReferenceEquals(image.Source, source);
            image.Source = source;
            if (changed && image.IsVisible) Motion.FadeIn(image, 260);
            image.Opacity = target == null ? 0.55 : 1;
        }

        // Windows-supplied artwork for this exact pair when available, else the bundled
        // family illustration, else the vector fallback.
        private ImageSource ArtworkFor(AudioTarget target, FrameworkElement tooltipOwner)
        {
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
                source = (ImageSource)window.FindResource(resource);
                if (family == "pro3")
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirPodsPro3.png"))
                        if (stream != null)
                        {
                            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 520; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); source = bitmap;
                        }
                artwork[key] = source;
            }
            string bluetoothId = target == null || target.RequiresPairingCheck ? null : target.BluetoothId;
            string cacheKey = key + "|" + (bluetoothId ?? "");
            ArtworkImage supplied;
            string name = target == null ? "AirPods" : target.ModelName;
            if (deviceArtwork.TryGetValue(cacheKey, out supplied)) { source = supplied.Source; tooltipOwner.ToolTip = supplied.Description + " · " + name; }
            else tooltipOwner.ToolTip = "Illustration · " + name;
            DateTime requested;
            if (target != null && (!artworkRequested.TryGetValue(cacheKey, out requested) || DateTime.UtcNow - requested > TimeSpan.FromSeconds(60)))
            {
                artworkRequested[cacheKey] = DateTime.UtcNow;
                LoadDeviceArtwork(target, key, bluetoothId, cacheKey, artworkRevision);
            }
            return source;
        }

        private void UpdateArtworkMotion()
        {
            bool connected = Selected != null && Selected.Connected && radioState != "Off" && radioState != "No adapter";
            bool animate = !exiting && capturePath == null && SystemParameters.ClientAreaAnimation && window.IsVisible;
            artworkMotion.Update(connected, animate);
        }

        private void AnimationPreferenceChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "ClientAreaAnimation" || exiting || window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(new Action(UpdateArtworkMotion));
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

        // What's known about the selected pair's charge, from the matched broadcast or Windows.
        private sealed class Charge
        {
            internal int? Left, Right, Case, Overall;
            internal bool LeftCharging, RightCharging, CaseCharging, Components, Inferred;
            internal string Note;
        }

        private Charge ReadCharge(AudioTarget target)
        {
            var charge = new Charge();
            if (target == null) return charge;
            var value = battery.ForTarget(target);
            var overall = battery.GetDeviceBattery(target);
            charge.Overall = target.Connected ? overall != null && overall.Fresh ? (int?)overall.Percent : target.Battery : null;
            if (value != null && AirPodsModels.ComponentLayout(target.ModelId) != "single")
            {
                charge.Components = true;
                charge.Left = value.Left; charge.Right = value.Right; charge.Case = value.Case;
                charge.LeftCharging = value.LeftCharging; charge.RightCharging = value.RightCharging; charge.CaseCharging = value.CaseCharging;
                charge.Inferred = value.Inferred;
                int age = Math.Max(0, (int)(DateTime.UtcNow - value.Seen).TotalSeconds);
                charge.Note = (value.Inferred ? "Matched automatically · " : "") + (age < 5 ? "Live" : "Updated " + age + " s ago");
            }
            else if (value != null) { charge.Overall = charge.Overall ?? value.Left ?? value.Right; charge.Note = "From your AirPods"; }
            else if (charge.Overall.HasValue) charge.Note = "Reported by Windows";
            else charge.Note = target.Connected ? "Charge appears once your AirPods share it" : "Open the case nearby to see charge";
            return charge;
        }

        private void UpdateBattery(bool sweep = false)
        {
            var target = Selected;
            var charge = ReadCharge(target);
            bool single = !charge.Components;
            bool hasCase = target != null && AirPodsModels.HasCase(target.ModelId);
            bool any = charge.Components || charge.Overall.HasValue;
            bool steps = connectionTrace.HasStarted && (connectionTrace.Running || connectionTrace.Failed);
            // Tiles only for real readings. Connected without one gets an honest note instead
            // of empty gauges; a disconnected pair with nothing to show gets neither.
            bool tiles = target != null && any && !steps;
            bool waiting = target != null && !any && target.Connected && !steps;
            // A closed case doesn't report; like iOS, show it only when it does.
            bool showCase = !single && hasCase && charge.Case.HasValue;
            Morph(() => {
                Get<Grid>("BatteryRow").Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
                Get<Border>("NoBattery").Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
                Get<Border>("RightCell").Visibility = single ? Visibility.Collapsed : Visibility.Visible;
                Get<Border>("CaseCell").Visibility = showCase ? Visibility.Visible : Visibility.Collapsed;
            });
            Grid.SetColumnSpan(Get<Border>("LeftCell"), single ? 5 : 1);
            Columns(Get<Grid>("BatteryRow"), showCase);
            Text("LeftLabel", single ? "Battery" : "Left");
            int delay = sweep ? 180 : 0;
            Ring("Left", single ? charge.Overall : charge.Left, !single && charge.LeftCharging, delay);
            Ring("Right", charge.Right, charge.RightCharging, delay + 70);
            Ring("Case", charge.Case, charge.CaseCharging, delay + 140);
            Get<Grid>("BatteryRow").ToolTip = tiles ? charge.Note : null;
            Get<Border>("NoBattery").ToolTip = waiting ? "Battery availability varies by model and firmware. Broadcasts are shown only when their identity is matched. Percentages are never guessed. " + battery.Status : null;
        }

        // Tiles share the row evenly: three with the case, two without it.
        private static void Columns(Grid row, bool withCase)
        {
            row.ColumnDefinitions[3].Width = new GridLength(withCase ? 8 : 0);
            row.ColumnDefinitions[4].Width = withCase ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        private void Ring(string part, int? percent, bool charging, int delay)
        {
            var ring = Get<RingGauge>(part + "Ring");
            ring.Show(percent, charging, Brush(charging ? "On" : percent.HasValue && percent.Value <= 20 ? "Danger" : "Ink"), delay);
            Get<TextBlock>(part + "Pct").Text = ring.Figure;
            Get<TextBlock>(part + "Pct").Foreground = Brush(percent.HasValue ? "Ink" : "Muted2");
            Get<Grid>(part + "Bolt").Visibility = charging && percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(ring, part + " " + (percent.HasValue ? percent + "%" + (charging ? ", charging" : "") : "unknown"));
        }

        // ---- Connection card -----------------------------------------------------------------

        private T CardPart<T>(string name) where T : class { return (T)card.FindName(name); }

        // Rises from the taskbar like the iPhone's AirPods card: the art settles on a
        // spring, the name follows, then each ring sweeps up to its charge.
        private void ShowCard(AudioTarget target, string status, bool offerConnect, BatteryReading nearby)
        {
            if (capturePath != null || flyout.IsOpen) return;
            caseCardShowing = offerConnect;
            var image = CardPart<Image>("CardArtwork");
            image.Source = ArtworkFor(target, image);
            CardPart<TextBlock>("CardName").Text = target == null ? AirPodsModels.Name(nearby.Model) : target.Name ?? target.ModelName;
            CardPart<TextBlock>("CardStatus").Text = status;
            var action = CardPart<Button>("CardAction");
            action.Visibility = offerConnect ? Visibility.Visible : Visibility.Collapsed;
            action.Content = "Connect";
            RenderCardBattery(target, nearby, true);
            cardPopup.Open(false);
            Motion.Pop(CardPart<Grid>("CardStage"), 0);
            Motion.Enter(CardPart<TextBlock>("CardName"), 0, 6, 240, 70);
            Motion.Enter(CardPart<TextBlock>("CardStatus"), 0, 6, 240, 100);
            if (offerConnect) Motion.Enter(action, 0, 6, 240, 150);
            cardTimer.Interval = TimeSpan.FromSeconds(offerConnect ? 12 : 6);
            Restart(cardTimer);
        }

        private void RenderCardBattery(AudioTarget target, BatteryReading nearby, bool sweep)
        {
            var charge = nearby == null ? ReadCharge(target) : new Charge {
                Components = true, Left = nearby.Left, Right = nearby.Right, Case = nearby.Case,
                LeftCharging = nearby.LeftCharging, RightCharging = nearby.RightCharging, CaseCharging = nearby.CaseCharging };
            bool single = !charge.Components;
            CardPart<Grid>("CardBattery").Visibility = charge.Components || charge.Overall.HasValue ? Visibility.Visible : Visibility.Collapsed;
            CardPart<Border>("CardRightCell").Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            bool showCase = !single && charge.Case.HasValue;
            CardPart<Border>("CardCaseCell").Visibility = showCase ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(CardPart<Border>("CardLeftCell"), single ? 5 : 1);
            Columns(CardPart<Grid>("CardBattery"), showCase);
            int delay = sweep ? 380 : 0;
            CardRing("Left", single ? charge.Overall : charge.Left, charge.LeftCharging, delay);
            CardRing("Right", charge.Right, charge.RightCharging, delay + 90);
            CardRing("Case", charge.Case, charge.CaseCharging, delay + 180);
        }

        private void CardRing(string part, int? percent, bool charging, int delay)
        {
            var ring = CardPart<RingGauge>("Card" + part + "Ring");
            if (delay > 0) { ring.BeginAnimation(RingGauge.ValueProperty, null); ring.Value = 0; }
            ring.Show(percent, charging, (Brush)card.FindResource(charging ? "On" : percent.HasValue && percent.Value <= 20 ? "Danger" : "Ink"), delay);
            CardPart<TextBlock>("Card" + part + "Pct").Text = ring.Figure;
            CardPart<Grid>("Card" + part + "Bolt").Visibility = charging && percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CloseCard() { cardTimer.Stop(); caseCardShowing = false; cardPopup.Close(); }

        private async Task ConnectFromCardAsync()
        {
            var action = CardPart<Button>("CardAction");
            cardTimer.Stop();
            action.IsEnabled = false; action.Content = "Connecting…";
            CardPart<TextBlock>("CardStatus").Text = "Connecting your saved pair";
            await ConnectAsync(true);
            action.IsEnabled = true;
            var target = Selected;
            if (target != null && target.Connected && radioState == "On")
            {
                caseCardShowing = false;
                action.Visibility = Visibility.Collapsed;
                CardPart<TextBlock>("CardStatus").Text = target.IsDefault ? "Connected · playing on this PC" : "Connected";
                RenderCardBattery(target, null, false);
                cardTimer.Interval = TimeSpan.FromSeconds(4); Restart(cardTimer);
            }
            else { action.Content = "Try again"; CardPart<TextBlock>("CardStatus").Text = statusTitle.Length > 0 ? statusTitle : "Couldn't connect"; Restart(cardTimer); }
        }

        // ---- Preferences, hotkey ----------------------------------------------------------------

        private void PreferencesChanged(object sender, RoutedEventArgs e)
        {
            if (!preferencesLoaded) return;
            settings.RouteAudio = Get<CheckBox>("AutoRouteCheck").IsChecked == true;
            settings.SwitchWhenConnected = Get<CheckBox>("SwitchWhenConnectedCheck").IsChecked == true;
            settings.Notifications = Get<CheckBox>("NotificationsCheck").IsChecked == true;
            settings.Hotkey = Get<CheckBox>("HotkeyCheck").IsChecked == true;
            SavePreferences(); UpdateHotkey(); UpdateDevice(); UpdateTray();
        }
        private void SavePreferences()
        {
            if (capturePath != null) return;
            try { settings.Save(); }
            catch (Exception ex) { SetStatus("Couldn't save settings", ex.Message, false, true); }
        }
        private void UpdateHotkey()
        {
            if (hwndSource == null || capturePath != null) return;
            UnregisterHotKey(hwndSource.Handle, 0x4F43);
            // Ctrl + Alt + K (MOD_CONTROL | MOD_ALT | MOD_NOREPEAT). Ctrl + Shift + A
            // clashed with common app shortcuts such as Chrome's tab search.
            bool registered = settings.Hotkey && RegisterHotKey(hwndSource.Handle, 0x4F43, 0x4003, 0x4B);
            var hint = Get<TextBlock>("ShortcutHint");
            hint.Text = registered || !settings.Hotkey ? "Ctrl + Alt + K connects or disconnects" : "Another app is using Ctrl + Alt + K";
            hint.Foreground = Brush(settings.Hotkey && !registered ? "Warn" : "Muted");
        }
        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == 0x4F43)
            {
                handled = true; window.Dispatcher.BeginInvoke(new Action(async () => await ToggleConnectionAsync()));
            }
            return IntPtr.Zero;
        }

        private async Task ToggleConnectionAsync()
        {
            if (connectCancel != null) { CancelConnect(); return; }
            if (Selected != null && Selected.Connected && radioState == "On") await DisconnectAsync();
            else await ConnectAsync();
        }

        // ---- Tray ----------------------------------------------------------------------------------

        private System.Drawing.Icon trayIcon, trayConnectedIcon;
        private IntPtr trayConnectedHandle;

        private void CreateTray()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Karmac.ico"))
                trayIcon = stream == null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone() : new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            trayConnectedIcon = WithStatusDot(trayIcon, out trayConnectedHandle);
            tray = new Forms.NotifyIcon { Text = "Karmac", Icon = trayIcon, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Opening += (sender, e) => BuildTrayMenu(menu);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (sender, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleFlyout(); };
            tray.BalloonTipClicked += (sender, e) => { var action = balloonAction; balloonAction = null; if (action != null) action(); };
            tray.BalloonTipClosed += (sender, e) => balloonAction = null;
            PromoteTrayIconSoon();
            UpdateTray();
        }

        private void BuildTrayMenu(Forms.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var target = Selected;
            var header = new Forms.ToolStripMenuItem(TrayStatus()) { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new Forms.ToolStripSeparator());
            if (connectCancel != null) menu.Items.Add("Stop connecting", null, (sender, e) => CancelConnect());
            else if (target != null && target.Connected && radioState == "On")
            {
                if (!target.IsDefault && target.PlaybackReady) menu.Items.Add("Play on this PC", null, async (sender, e) => await RouteAsync());
                menu.Items.Add("Disconnect", null, async (sender, e) => await DisconnectAsync());
            }
            else if (target != null) menu.Items.Add("Connect", null, async (sender, e) => await ConnectAsync());
            if (previousOutput != null && target != null && target.IsDefault)
                menu.Items.Add("Switch back to previous output", null, async (sender, e) => await SwitchBackAsync());
            menu.Items.Add("Open Karmac", null, (sender, e) => OpenFlyout());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (sender, e) => Quit());
        }

        private string TrayStatus()
        {
            var target = Selected;
            if (target == null) return "No AirPods selected";
            string name = target.Name ?? "AirPods";
            if (connectCancel != null) return "Connecting " + name;
            if (radioState != "On" || !target.Connected) return name + " · Not connected";
            string level = BatterySummary(target);
            return name + (target.CallsOnly ? " · Calls only" : " · Connected") + (level == null ? "" : " · " + level);
        }

        // "85%" from Windows, or "L 80% R 70%" from a matched broadcast; null when unknown.
        private string BatterySummary(AudioTarget target)
        {
            var device = battery.GetDeviceBattery(target);
            if (device != null) return device.Percent + "%";
            var value = battery.ForTarget(target);
            if (value == null) return null;
            if (value.Left.HasValue && value.Right.HasValue) return "L " + value.Left + "% R " + value.Right + "%";
            var one = value.Left ?? value.Right;
            return one.HasValue ? one + "%" : null;
        }

        private void UpdateTray()
        {
            if (tray == null) return;
            string text = "Karmac · " + TrayStatus();
            tray.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text; // NotifyIcon limit
            var target = Selected;
            bool connected = target != null && target.Connected && radioState == "On";
            var icon = connected ? trayConnectedIcon : trayIcon;
            if (!ReferenceEquals(tray.Icon, icon)) tray.Icon = icon;
        }

        // Windows 11 parks new tray icons behind the ^ overflow. Karmac is meant to be one
        // click away, so the first time Explorer records its icon, promote it onto the
        // taskbar. An explicit choice the user already made (IsPromoted set) is left alone.
        private void PromoteTrayIconSoon()
        {
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            wait.Tick += (sender, e) => { wait.Stop(); PromoteTrayIcon(); };
            wait.Start();
        }

        private static void PromoteTrayIcon()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly().Location;
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                        using (var key = root.OpenSubKey(name, true))
                        {
                            if (key == null || !String.Equals(key.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!(key.GetValue("IsPromoted") is int)) key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                }
            }
            catch (Exception error) { Log.Write("Promote tray icon: " + error.Message); }
        }

        // The app icon with a small green dot, for the "connected" tray state.
        private static System.Drawing.Icon WithStatusDot(System.Drawing.Icon source, out IntPtr handle)
        {
            int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            using (var bitmap = new System.Drawing.Bitmap(size, size))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.DrawIcon(source, new System.Drawing.Rectangle(0, 0, size, size));
                    float dot = size * 0.42f, at = size - dot;
                    using (var ring = new System.Drawing.SolidBrush(System.Drawing.Color.Black))
                        graphics.FillEllipse(ring, at - 1, at - 1, dot + 1, dot + 1);
                    using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x30, 0xD1, 0x58)))
                        graphics.FillEllipse(fill, at + 1, at + 1, dot - 3, dot - 3);
                }
                handle = bitmap.GetHicon();
                return System.Drawing.Icon.FromHandle(handle);
            }
        }

        private void Notify(string title, string text, Action clicked)
        {
            if (tray == null || !settings.Notifications || window.IsActive) return;
            balloonAction = clicked;
            tray.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
        }

        private void NotifyConnected(AudioTarget target)
        {
            if (!settings.Notifications || capturePath != null || flyout.IsOpen) return;
            ShowCard(target, target.IsDefault ? "Connected · playing on this PC" : "Connected", false, null);
        }

        // Case-open card: the saved model just started advertising close by while
        // disconnected. Connect acts on the saved, verified pair only; nothing connects
        // on its own, and the card doesn't claim a nearby pair of this model is the user's.
        private void ShowCaseCard(BatteryReading reading)
        {
            var target = Selected;
            if (target == null || busy || radioState != "On" || target.Connected || target.ModelId != reading.Model ||
                DateTime.UtcNow < nextCaseCard || !settings.Notifications) return;
            // Appeared only fires after the address was silent for 90 s, so reopening the case
            // later shows the card again, like the iPhone.
            nextCaseCard = DateTime.UtcNow.AddSeconds(30);
            ShowCard(target, AirPodsModels.Name(reading.Model) + " nearby · if they're yours", true, reading);
        }

        // Runs every second: low-battery notifications.
        private void CheckBatteryEvents()
        {
            var target = Selected;
            if (target == null || capturePath != null) return;
            UpdateTray();
            if (!settings.Notifications || !target.PlaybackReady) return;
            var value = battery.ForTarget(target);
            var device = battery.GetDeviceBattery(target);
            if (device != null) WarnLowBattery("Battery", device.Percent, false);
            else if (value != null)
            {
                WarnLowBattery("Left", value.Left, value.LeftCharging);
                WarnLowBattery("Right", value.Right, value.RightCharging);
                WarnLowBattery("Case", value.Case, value.CaseCharging);
            }
        }

        private void WarnLowBattery(string part, int? percent, bool charging)
        {
            if (!percent.HasValue) return;
            int warned;
            if (!batteryWarned.TryGetValue(part, out warned)) warned = 100;
            // Re-arm only after charging or a clear recovery, so a reading that wobbles
            // between 10% and 20% doesn't notify again on every step.
            if (charging || percent.Value >= 40) { batteryWarned[part] = 100; return; }
            int threshold = percent.Value <= 10 ? 10 : percent.Value <= 20 ? 20 : 100;
            if (threshold >= warned || threshold == 100) return;
            batteryWarned[part] = threshold;
            string what = part == "Battery" ? "Your AirPods are" : part == "Case" ? "The case is" : "The " + part.ToLowerInvariant() + " AirPod is";
            Notify("Low battery · " + percent.Value + "%", what + " running low.", OpenFlyout);
        }

        // ---- Misc ------------------------------------------------------------------------------------

        private void ShowNotices()
        {
            flyout.Close();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Notices.txt"))
            using (var reader = new StreamReader(stream))
            {
                bool light = Theme.Light;
                var ink = new SolidColorBrush(light ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Color.FromRgb(0xF2, 0xF2, 0xF2));
                var paper = new SolidColorBrush(light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x1F, 0x1F, 0x1F));
                var text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    Background = paper, Foreground = ink, BorderThickness = new Thickness(0), FontFamily = window.FontFamily,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20), FontSize = 12 };
                var dialog = new Window { Title = "Karmac · Open-source notices", Width = 600, Height = 480,
                    Background = paper, Content = text, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                dialog.SourceInitialized += (sender, e) => {
                    try { int dark = light ? 0 : 1; DwmSetWindowAttribute(new WindowInteropHelper(dialog).Handle, 20, ref dark, sizeof(int)); }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                };
                dialog.Show(); dialog.Activate();
            }
        }
        private void OpenBluetoothSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); flyout.Close(); }
            catch (Exception ex) { SetStatus("Couldn't open Windows Settings", ex.Message, false, true); }
        }
        private void Capture(string path)
        {
            var root = Get<Grid>("Root");
            var sheet = Get<Border>("Sheet");
            root.UpdateLayout();
            double scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            full.Render(root);
            // Crop to the sheet plus a little of its shadow.
            Rect bounds = sheet.TransformToAncestor(root).TransformBounds(new Rect(sheet.RenderSize));
            bounds.Inflate(8, 8);
            bounds.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            var crop = new CroppedBitmap(full, new Int32Rect((int)(bounds.X * scale), (int)(bounds.Y * scale),
                (int)(bounds.Width * scale), (int)(bounds.Height * scale)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            using (var file = File.Create(path)) encoder.Save(file);
        }
        private void Quit()
        {
            exiting = true; timer.Stop(); connectionTimer.Stop(); audioSettle.Stop(); devicesSettle.Stop(); cardTimer.Stop();
            app.Shutdown();
        }
        public void Dispose()
        {
            SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged;
            artworkMotion.Update(false, false);
            timer.Stop(); connectionTimer.Stop(); audioSettle.Stop(); devicesSettle.Stop(); cardTimer.Stop();
            if (connectCancel != null) connectCancel.Cancel();
            deviceEvents.Dispose(); BluetoothLink.ForgetAll(); battery.Dispose();
            if (activationWait != null) activationWait.Unregister(null);
            if (hwndSource != null) { UnregisterHotKey(hwndSource.Handle, 0x4F43); hwndSource.RemoveHook(WindowMessage); }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
            if (trayConnectedIcon != null) { trayConnectedIcon.Dispose(); DestroyIcon(trayConnectedHandle); }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    }
}
