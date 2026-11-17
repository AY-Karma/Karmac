using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Karmac
{
    // Code-driven transitions. Only opacity, render transforms and brush colour
    // animate, so layout never runs per frame. Entrances ease out; exits are
    // instant. With Windows animation effects off, movement is dropped and only
    // a short fade remains.
    internal static class Motion
    {
        // Deterministic captures and tests set this to apply end states immediately.
        internal static bool Suppressed;

        // QuinticEase.EaseOut is easeOutQuint, cubic-bezier(0.22, 1, 0.36, 1).
        private static readonly IEasingFunction EaseOut = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
        private const int ReducedFadeMs = 140;

        private static bool Moves { get { return SystemParameters.ClientAreaAnimation; } }

        // Collapsed -> visible rises into place; visible -> collapsed is immediate.
        internal static void SetShown(UIElement element, bool show, double rise = 6, int ms = 170)
        {
            bool wasShown = element.Visibility == Visibility.Visible;
            element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !wasShown) Enter(element, 0, rise, ms);
        }

        // Pages slide in from the side they logically live on (+1 forward, -1 back).
        internal static void Page(UIElement page, int direction)
        {
            Enter(page, 20 * direction, 0, 200);
        }

        // An object arriving: it settles from slightly small on a lively spring.
        internal static void Pop(UIElement element, int delayMs)
        {
            if (Suppressed) return;
            bool moves = Moves;
            element.BeginAnimation(UIElement.OpacityProperty, Animation(0, 1, TimeSpan.FromMilliseconds(moves ? 200 : ReducedFadeMs), TimeSpan.FromMilliseconds(moves ? delayMs : 0)));
            if (!moves) return;
            var scale = element.RenderTransform as ScaleTransform;
            if (scale == null || scale.IsFrozen) { scale = new ScaleTransform(); element.RenderTransform = scale; element.RenderTransformOrigin = new Point(0.5, 0.8); }
            var grow = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 450)), new SpringEase { Damping = 0.55 }));
            grow.Freeze();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        internal static void FadeIn(UIElement element, int ms = 200)
        {
            Enter(element, 0, 0, ms);
        }

        // The window's first view rises in as one composition, 40ms apart.
        internal static void Stagger(params UIElement[] elements)
        {
            int delay = 0;
            foreach (var element in elements)
            {
                if (element.Visibility != Visibility.Visible) continue;
                Enter(element, 0, 10, 300, delay);
                delay += 40;
            }
        }

        internal static void Enter(UIElement element, double dx, double dy, int ms, int delayMs = 0)
        {
            if (Suppressed) return;
            bool moves = Moves;
            var duration = TimeSpan.FromMilliseconds(moves ? ms : ReducedFadeMs);
            var delay = TimeSpan.FromMilliseconds(moves ? delayMs : 0);
            element.BeginAnimation(UIElement.OpacityProperty, Animation(0, 1, duration, delay));
            if (!moves || dx == 0 && dy == 0) return;
            var shift = OwnedShift(element);
            if (shift == null) return;
            shift.BeginAnimation(TranslateTransform.XProperty, dx == 0 ? null : Animation(dx, 0, duration, delay));
            shift.BeginAnimation(TranslateTransform.YProperty, dy == 0 ? null : Animation(dy, 0, duration, delay));
        }

        // Switch templates name their knob "Thumb" (Scale + Translate group) and
        // their lit track "TrackOn". The first layout snaps to the current state;
        // later Checked/Unchecked changes slide, so opening Settings never replays.
        internal static void AttachSwitches()
        {
            var toggleType = typeof(System.Windows.Controls.Primitives.ToggleButton);
            EventManager.RegisterClassHandler(toggleType, FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, e) => SyncSwitch(s, false)));
            EventManager.RegisterClassHandler(toggleType, System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => SyncSwitch(s, true)));
            EventManager.RegisterClassHandler(toggleType, System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => SyncSwitch(s, true)));
        }

        private static void SyncSwitch(object sender, bool animate)
        {
            var toggle = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (toggle == null || toggle.Template == null) return;
            var thumb = toggle.Template.FindName("Thumb", toggle) as FrameworkElement;
            var track = toggle.Template.FindName("TrackOn", toggle) as UIElement;
            var parent = thumb == null ? null : thumb.Parent as FrameworkElement;
            var group = thumb == null ? null : thumb.RenderTransform as TransformGroup;
            if (track == null || parent == null || group == null || group.Children.Count < 2 || parent.ActualWidth <= 0) return;
            if (group.IsFrozen) { group = group.Clone(); thumb.RenderTransform = group; } // template values are shared and sealed
            var shift = (TranslateTransform)group.Children[1];
            bool on = toggle.IsChecked == true;
            double x = on ? parent.ActualWidth - thumb.ActualWidth - thumb.Margin.Left - thumb.Margin.Right : 0;
            if (!animate || Suppressed || !toggle.IsVisible)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = x;
                track.BeginAnimation(UIElement.OpacityProperty, null); track.Opacity = on ? 1 : 0;
                return;
            }
            var duration = TimeSpan.FromMilliseconds(Moves ? 180 : ReducedFadeMs);
            double fromX = shift.X, fromOpacity = track.Opacity;
            shift.X = x; track.Opacity = on ? 1 : 0;
            if (Moves) shift.BeginAnimation(TranslateTransform.XProperty, Animation(fromX, x, duration));
            else shift.BeginAnimation(TranslateTransform.XProperty, null);
            track.BeginAnimation(UIElement.OpacityProperty, Animation(fromOpacity, on ? 1 : 0, duration));
        }

        // Glides a progress value; the base value is set first so reads stay truthful.
        internal static void Glide(System.Windows.Controls.Primitives.RangeBase bar, double value)
        {
            double from = bar.Value;
            bar.Value = value;
            if (Suppressed || !Moves || !bar.IsVisible || Math.Abs(from - value) < 0.5) { bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); return; }
            bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                Animation(from, value, TimeSpan.FromMilliseconds(260)));
        }

        // Crossfades a status dot's colour instead of swapping brushes.
        internal static void Tint(Shape shape, Color color)
        {
            var brush = shape.Fill as SolidColorBrush;
            if (brush != null && !brush.IsFrozen && brush.Color == color && !brush.HasAnimatedProperties) return;
            if (brush == null || brush.IsFrozen || Suppressed || !shape.IsVisible)
            {
                shape.Fill = new SolidColorBrush(color);
                return;
            }
            Color from = brush.Color;
            brush.Color = color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, color, TimeSpan.FromMilliseconds(150))
            { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        }

        private static TranslateTransform OwnedShift(UIElement element)
        {
            var shift = element.RenderTransform as TranslateTransform;
            if (shift != null && !shift.IsFrozen) return shift;
            if (element.RenderTransform != null && element.RenderTransform != Transform.Identity) return null;
            shift = new TranslateTransform();
            element.RenderTransform = shift;
            return shift;
        }

        private static DoubleAnimation Animation(double from, double to, TimeSpan duration)
        {
            var animation = new DoubleAnimation(from, to, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
            animation.Freeze();
            return animation;
        }

        private static AnimationTimeline Animation(double from, double to, TimeSpan duration, TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return Animation(from, to, duration);
            // Hold the start frame through the delay so nothing flashes at its final state first.
            var staged = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            staged.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            staged.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
            staged.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), EaseOut));
            staged.Freeze();
            return staged;
        }

        private static IEasingFunction Freeze(QuinticEase ease) { ease.Freeze(); return ease; }
    }
}
