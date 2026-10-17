using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Karmac
{
    // Animate only the render transform: layout, artwork decoding and Bluetooth
    // work remain independent. State changes replace the clock at its current Y.
    internal sealed class ArtworkMotion
    {
        internal readonly TranslateTransform Transform = new TranslateTransform();
        private bool floating, suspended = true;
        private int revision;
        internal bool Floating { get { return floating && !suspended; } }

        internal void Update(bool connected, bool animate)
        {
            bool desired = connected && animate;
            if (desired == Floating && suspended == !animate) return;
            floating = connected; suspended = !animate;
            int currentRevision = ++revision;
            double current = Transform.Y;
            Transform.BeginAnimation(TranslateTransform.YProperty, null);
            Transform.Y = current;
            if (!animate)
            {
                Transform.Y = 0;
                return;
            }
            if (Math.Abs(current) < 0.01)
            {
                Transform.Y = 0;
                if (desired) BeginFloat();
                return;
            }
            var settle = new DoubleAnimation(current, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            settle.Completed += (sender, e) =>
            {
                if (currentRevision != revision) return;
                Transform.BeginAnimation(TranslateTransform.YProperty, null);
                Transform.Y = 0;
                if (Floating) BeginFloat();
            };
            Transform.BeginAnimation(TranslateTransform.YProperty, settle, HandoffBehavior.SnapshotAndReplace);
        }

        private void BeginFloat()
        {
            var motion = new DoubleAnimation(0, -3, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(motion, 30);
            Transform.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
