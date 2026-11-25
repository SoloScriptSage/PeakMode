using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeakModeTray.Controls
{
    // Letter spacing for single-line TextBlocks, which WPF does not have.
    // Each character after the first is shifted right by i * Spacing with a
    // TextEffect, and the right padding grows by the added width so layout,
    // alignment and neighbours see the spaced width. The text itself is not
    // changed, so screen readers and copy read it normally.
    //   <Setter Property="c:Tracking.Spacing" Value="1.5" />
    // Only for text that does not wrap: offsets would carry across lines.
    public static class Tracking
    {
        public static readonly DependencyProperty SpacingProperty = DependencyProperty.RegisterAttached(
            "Spacing", typeof(double), typeof(Tracking), new PropertyMetadata(0.0, OnSpacingChanged));

        public static double GetSpacing(DependencyObject o) => (double)o.GetValue(SpacingProperty);
        public static void SetSpacing(DependencyObject o, double v) => o.SetValue(SpacingProperty, v);

        // Padding before tracking was added, so updates don't accumulate
        private static readonly DependencyProperty BasePaddingProperty = DependencyProperty.RegisterAttached(
            "BasePadding", typeof(Thickness?), typeof(Tracking), new PropertyMetadata(null));

        private static readonly DependencyPropertyDescriptor TextDescriptor =
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

        // The descriptor holds the TextBlock strongly, so it only listens while
        // the TextBlock is loaded (rebuilt list items must not leak).
        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;
            tb.Loaded   -= OnLoaded;
            tb.Unloaded -= OnUnloaded;
            TextDescriptor.RemoveValueChanged(tb, OnTextChanged);
            if (GetSpacing(tb) != 0)
            {
                tb.Loaded   += OnLoaded;
                tb.Unloaded += OnUnloaded;
                if (tb.IsLoaded) TextDescriptor.AddValueChanged(tb, OnTextChanged);
            }
            Apply(tb);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var tb = (TextBlock)sender;
            TextDescriptor.RemoveValueChanged(tb, OnTextChanged);
            TextDescriptor.AddValueChanged(tb, OnTextChanged);
            Apply(tb);   // text may have changed while unloaded
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e) =>
            TextDescriptor.RemoveValueChanged((TextBlock)sender, OnTextChanged);

        private static void OnTextChanged(object? sender, System.EventArgs e)
        {
            if (sender is TextBlock tb) Apply(tb);
        }

        internal static void Apply(TextBlock tb)
        {
            double s = GetSpacing(tb);
            var basePad = (Thickness?)tb.GetValue(BasePaddingProperty) ?? tb.Padding;
            tb.SetValue(BasePaddingProperty, basePad);

            var effects = new TextEffectCollection();
            int n = tb.Text?.Length ?? 0;
            for (int i = 1; i < n && s != 0; i++)
                effects.Add(new TextEffect
                {
                    PositionStart = i, PositionCount = 1,
                    Transform = new TranslateTransform(i * s, 0),
                });
            tb.TextEffects = effects;
            double extra = n > 1 ? (n - 1) * s : 0;
            tb.Padding = new Thickness(basePad.Left, basePad.Top, basePad.Right + extra, basePad.Bottom);
        }
    }
}
