using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Karmac
{
    // Karmac is always black, like the hardware's own dark UI. From Windows it follows the
    // taskbar's mode (tray icon ink) and the accent colour. Brushes live in resource
    // dictionaries, so an accent change in Windows Settings restyles open surfaces at once.
    internal static class Theme
    {
        internal static bool Light { get; private set; }
        internal static bool TaskbarLight { get; private set; }
        internal static Color Accent { get; private set; }
        internal static event Action Changed;
        private static bool watching;

        internal static void Read()
        {
            Light = false; // by design: pure black in every Windows mode
            TaskbarLight = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;
            int abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD77800));
            Accent = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }

        internal static void Watch()
        {
            if (watching) return;
            watching = true;
            SystemEvents.UserPreferenceChanged += (sender, e) => {
                if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color &&
                    e.Category != UserPreferenceCategory.VisualStyle) return;
                Read();
                var handler = Changed;
                if (handler != null && Application.Current != null) Application.Current.Dispatcher.BeginInvoke(handler);
            };
        }

        internal static void Apply(ResourceDictionary resources)
        {
            bool light = Light;
            Set(resources, "Sheet", light ? "#F9F9F9" : "#000000");
            Set(resources, "Raised", light ? "#FFFFFF" : "#0E0E0E");
            Set(resources, "Well", light ? "#F0F0F0" : "#000000");
            Set(resources, "Hover", light ? "#0A000000" : "#14FFFFFF");
            Set(resources, "Press", light ? "#14000000" : "#1FFFFFFF");
            Set(resources, "Stroke", light ? "#14000000" : "#1CFFFFFF");
            Set(resources, "StrokeStrong", light ? "#29000000" : "#33FFFFFF");
            Set(resources, "Ink", light ? "#1B1B1B" : "#FFFFFF");
            Set(resources, "Secondary", light ? "#5C5C5C" : "#B4B4B4");
            Set(resources, "Muted", light ? "#858585" : "#8F8F8F");
            Set(resources, "RingTrack", light ? "#17000000" : "#2BFFFFFF");
            Set(resources, "Contrast", light ? "#1B1B1B" : "#FFFFFF");
            Set(resources, "ContrastInk", light ? "#FFFFFF" : "#000000");
            Set(resources, "On", light ? "#1E9E47" : "#30D158");
            Set(resources, "Danger", light ? "#D92D20" : "#FF453A");
            Set(resources, "Warn", light ? "#B25E00" : "#FF9F0A");
            Set(resources, "Off", light ? "#9A9A9A" : "#7A7A7A");
            Color accent = AccentFor(light);
            resources["Accent"] = Frozen(new SolidColorBrush(accent));
            resources["AccentInk"] = Frozen(new SolidColorBrush(Luminance(accent) > 0.45 ? Colors.Black : Colors.White));
            resources["ShadowColor"] = light ? Color.FromArgb(0x38, 0, 0, 0) : Color.FromArgb(0x80, 0, 0, 0);
            // A pool of light under the product in dark mode, a soft contact shadow in light mode.
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(light ? Color.FromArgb(0x24, 0, 0, 0) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0));
            glow.GradientStops.Add(new GradientStop(light ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1));
            resources["HeroGlow"] = Frozen(glow);
        }

        // Windows draws "on" controls with a lighter accent in dark mode.
        private static Color AccentFor(bool light)
        {
            Color c = Accent;
            if (light) return c;
            return Color.FromRgb((byte)(c.R + (255 - c.R) * 0.42), (byte)(c.G + (255 - c.G) * 0.42), (byte)(c.B + (255 - c.B) * 0.42));
        }

        internal static Brush Get(FrameworkElement scope, string key) { return (Brush)scope.FindResource(key); }

        private static double Luminance(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }
        private static void Set(ResourceDictionary resources, string key, string color)
        {
            resources[key] = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
        }
        private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
        private static int ReadDword(string path, string name, int fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path))
                {
                    object value = key == null ? null : key.GetValue(name);
                    return value is int ? (int)value : fallback;
                }
            }
            catch (Exception) { return fallback; }
        }
    }

    internal enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    internal static class Taskbar
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData { public int Size; public IntPtr Window; public int Message; public int Edge; public NativeRect Bounds; public IntPtr Param; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int message, ref AppBarData data);

        internal static TaskbarEdge Edge()
        {
            try
            {
                var data = new AppBarData { Size = Marshal.SizeOf(typeof(AppBarData)) };
                if (SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref data) != IntPtr.Zero && data.Edge >= 0 && data.Edge <= 3)
                    return (TaskbarEdge)data.Edge;
            }
            catch (Exception) { }
            return TaskbarEdge.Bottom;
        }
    }

    // A transparent, borderless window docked beside the notification area. Its sheet
    // rises out of the taskbar edge on a spring and sinks back into it when dismissed,
    // the way Windows' own flyouts and iOS sheets behave. The window's root clips at the
    // edge, so the sheet appears to come from behind the taskbar.
    internal sealed class TrayPopup
    {
        private readonly Window window;
        private readonly FrameworkElement sheet;
        private readonly TranslateTransform shift = new TranslateTransform();
        private readonly double gap;
        private readonly double damping;
        private TaskbarEdge edge = TaskbarEdge.Bottom;
        private DateTime hiddenAt = DateTime.MinValue;
        private int revision;
        internal bool IsOpen { get; private set; }
        internal event Action Hidden;

        internal TrayPopup(Window window, FrameworkElement sheet, double gap, double damping)
        {
            this.window = window; this.sheet = sheet; this.gap = gap; this.damping = damping;
            sheet.RenderTransform = shift;
        }

        // True just after a dismissal, so the tray click that caused it doesn't reopen it.
        internal bool JustHidden { get { return (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300; } }

        internal void Place()
        {
            edge = Taskbar.Edge();
            Rect area = SystemParameters.WorkArea;
            window.Height = Math.Min(window.Height, area.Height);
            bool vertical = edge == TaskbarEdge.Top || edge == TaskbarEdge.Bottom;
            window.Left = edge == TaskbarEdge.Left ? area.Left : area.Right - window.Width;
            window.Top = edge == TaskbarEdge.Top ? area.Top : area.Bottom - window.Height;
            sheet.VerticalAlignment = edge == TaskbarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            sheet.HorizontalAlignment = edge == TaskbarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            sheet.Margin = new Thickness(edge == TaskbarEdge.Left ? gap : 0, edge == TaskbarEdge.Top ? gap : 0,
                edge == TaskbarEdge.Left ? 0 : gap, edge == TaskbarEdge.Top ? 0 : gap);
            if (!vertical) sheet.Margin = new Thickness(edge == TaskbarEdge.Left ? gap : 0, 0, edge == TaskbarEdge.Right ? gap : 0, gap);
        }

        internal void Open(bool activate)
        {
            ++revision;
            Place();
            IsOpen = true;
            window.ShowActivated = activate;
            if (!Motion.Suppressed) { SetOffset(HiddenOffset()); sheet.Opacity = 0; }
            window.Show();
            if (activate) window.Activate();
            if (Motion.Suppressed) { SetOffset(0); sheet.Opacity = 1; return; }
            // Lay out first so the off-screen distance uses the sheet's real size.
            sheet.UpdateLayout();
            double from = HiddenOffset();
            bool moves = SystemParameters.ClientAreaAnimation;
            var duration = TimeSpan.FromMilliseconds(moves ? 300 : 140);
            Animate(moves ? from : 0, 0, duration, new SpringEase { Damping = damping });
            sheet.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(moves ? 70 : 140)));
        }

        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            int current = ++revision;
            hiddenAt = DateTime.UtcNow;
            if (Motion.Suppressed || !window.IsVisible) { window.Hide(); OnHidden(); return; }
            bool moves = SystemParameters.ClientAreaAnimation;
            var duration = TimeSpan.FromMilliseconds(moves ? 150 : 120);
            var sink = Animate(moves ? CurrentOffset() : 0, moves ? HiddenOffset() : 0, duration, new CubicEase { EasingMode = EasingMode.EaseIn });
            var fade = new DoubleAnimation(sheet.Opacity, 0, duration) { BeginTime = moves ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero };
            fade.Completed += (s, e) => {
                if (current != revision) return;
                window.Hide();
                OnHidden();
            };
            sheet.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void OnHidden()
        {
            hiddenAt = DateTime.UtcNow;
            var handler = Hidden;
            if (handler != null) handler();
        }

        // Distance that puts the sheet fully behind the taskbar edge.
        private double HiddenOffset()
        {
            double size = edge == TaskbarEdge.Left || edge == TaskbarEdge.Right ? sheet.ActualWidth : sheet.ActualHeight;
            double travel = (size > 0 ? size : 480) + gap + 24;
            return edge == TaskbarEdge.Top || edge == TaskbarEdge.Left ? -travel : travel;
        }

        private bool Horizontal { get { return edge == TaskbarEdge.Left || edge == TaskbarEdge.Right; } }
        private double CurrentOffset() { return Horizontal ? shift.X : shift.Y; }
        private void SetOffset(double value)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.X = Horizontal ? value : 0; shift.Y = Horizontal ? 0 : value;
        }
        private DoubleAnimation Animate(double from, double to, TimeSpan duration, IEasingFunction ease)
        {
            SetOffset(to);
            var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(animation, 120);
            shift.BeginAnimation(Horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty, animation);
            return animation;
        }
    }

    // A damped spring normalised to settle at t = 1. Damping 1 is critically damped;
    // 0.7 gives the small, quick overshoot of an iOS sheet.
    internal sealed class SpringEase : EasingFunctionBase
    {
        public double Damping { get; set; }
        public SpringEase() { Damping = 0.8; EasingMode = EasingMode.EaseIn; }
        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            double zeta = Math.Max(0.3, Math.Min(Damping, 0.999)), omega = 9.0;
            double decay = Math.Exp(-zeta * omega * t), damped = omega * Math.Sqrt(1 - zeta * zeta);
            return 1 - decay * (Math.Cos(damped * t) + zeta * omega / damped * Math.Sin(damped * t));
        }
        protected override Freezable CreateInstanceCore() { return new SpringEase(); }
    }

    // Apple-style battery ring. The ring and the number share one animated Value, so a
    // reading that arrives sweeps the arc and counts the figure up together.
    public sealed class RingGauge : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((RingGauge)d).OnValueChanged()));
        public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register("ShowLabel", typeof(bool), typeof(RingGauge),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        // Raised on every animated step, so a figure outside the ring can count with the sweep.
        public event EventHandler ValueChanged;
        private void OnValueChanged() { var handler = ValueChanged; if (handler != null) handler(this, EventArgs.Empty); }
        public bool ShowLabel { get { return (bool)GetValue(ShowLabelProperty); } set { SetValue(ShowLabelProperty, value); } }
        // The figure for the current (possibly animating) value.
        public string Figure { get { return Known ? Math.Round(Math.Max(0, Math.Min(1, Value)) * 100).ToString(CultureInfo.InvariantCulture) + "%" : "—"; } }
        public static readonly DependencyProperty KnownProperty = DependencyProperty.Register("Known", typeof(bool), typeof(RingGauge),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register("Charging", typeof(bool), typeof(RingGauge),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register("StrokeWidth", typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register("Track", typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink", typeof(Brush), typeof(RingGauge),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register("FontSize", typeof(double), typeof(RingGauge),
            new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public bool Known { get { return (bool)GetValue(KnownProperty); } set { SetValue(KnownProperty, value); } }
        public bool Charging { get { return (bool)GetValue(ChargingProperty); } set { SetValue(ChargingProperty, value); } }
        public double StrokeWidth { get { return (double)GetValue(StrokeWidthProperty); } set { SetValue(StrokeWidthProperty, value); } }
        public Brush Track { get { return (Brush)GetValue(TrackProperty); } set { SetValue(TrackProperty, value); } }
        public Brush Fill { get { return (Brush)GetValue(FillProperty); } set { SetValue(FillProperty, value); } }
        public Brush Ink { get { return (Brush)GetValue(InkProperty); } set { SetValue(InkProperty, value); } }
        public double FontSize { get { return (double)GetValue(FontSizeProperty); } set { SetValue(FontSizeProperty, value); } }

        private static readonly Typeface Figures = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface Glyphs = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        protected override void OnRender(DrawingContext context)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            double stroke = StrokeWidth, radius = (size - stroke) / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var trackPen = new Pen(Track, stroke);
            if (!Known) trackPen.DashStyle = new DashStyle(new[] { 0.1, 2.4 }, 0);
            trackPen.DashCap = PenLineCap.Round;
            context.DrawEllipse(null, trackPen, center, radius, radius);
            double value = Math.Max(0, Math.Min(1, Value));
            if (Known && value > 0.001)
            {
                var pen = new Pen(Fill, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (value >= 0.999) context.DrawEllipse(null, pen, center, radius, radius);
                else
                {
                    double angle = value * 2 * Math.PI - Math.PI / 2;
                    var start = new Point(center.X, center.Y - radius);
                    var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(start, false, false);
                        g.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true, false);
                    }
                    geometry.Freeze();
                    context.DrawGeometry(null, pen, geometry);
                }
            }
            if (!ShowLabel) return;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            string label = Known ? Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) : "—";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Figures, FontSize, Ink, dip);
            if (Known && Charging)
            {
                // Charging: the figure lifts a little and a small bolt sits beneath it.
                var bolt = new FormattedText("\uE945", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyphs, FontSize * 0.55, Fill, dip);
                double block = text.Height * 0.82 + bolt.Height * 0.8, top = center.Y - block / 2;
                context.DrawText(text, new Point(center.X - text.Width / 2, top - text.Height * 0.09));
                context.DrawText(bolt, new Point(center.X - bolt.Width / 2, top + text.Height * 0.74));
            }
            else context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }

        // Sweeps from the current value to the new one; Known flips immediately.
        internal void Show(int? percent, bool charging, Brush fill, int delayMs)
        {
            Known = percent.HasValue; Charging = charging; Fill = fill;
            OnValueChanged();
            double to = percent.HasValue ? percent.Value / 100.0 : 0;
            double from = Value;
            Value = to;
            if (Motion.Suppressed || !SystemParameters.ClientAreaAnimation || !IsVisible || Math.Abs(from - to) < 0.005)
            { BeginAnimation(ValueProperty, null); return; }
            var sweep = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
            sweep.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 560)),
                new QuinticEase { EasingMode = EasingMode.EaseOut }));
            BeginAnimation(ValueProperty, sweep);
        }
    }
}
