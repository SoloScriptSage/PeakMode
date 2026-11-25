using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace PeakModeTray.Controls
{
    // Cut corners from DESIGN.md: cards get a diagonal cut at the top right,
    // the hero card also at the bottom left. Same as the mockups'
    // clip-path, so the border is clipped with the card (no diagonal line).
    //   <Border c:CutCorners.TopRight="14" />
    public static class CutCorners
    {
        public static readonly DependencyProperty TopRightProperty = DependencyProperty.RegisterAttached(
            "TopRight", typeof(double), typeof(CutCorners), new PropertyMetadata(0.0, OnChanged));

        public static readonly DependencyProperty BottomLeftProperty = DependencyProperty.RegisterAttached(
            "BottomLeft", typeof(double), typeof(CutCorners), new PropertyMetadata(0.0, OnChanged));

        public static double GetTopRight(DependencyObject o) => (double)o.GetValue(TopRightProperty);
        public static void SetTopRight(DependencyObject o, double v) => o.SetValue(TopRightProperty, v);
        public static double GetBottomLeft(DependencyObject o) => (double)o.GetValue(BottomLeftProperty);
        public static void SetBottomLeft(DependencyObject o, double v) => o.SetValue(BottomLeftProperty, v);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.SizeChanged -= OnSizeChanged;
            if (GetTopRight(fe) > 0 || GetBottomLeft(fe) > 0) fe.SizeChanged += OnSizeChanged;
            Update(fe);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

        private static void Update(FrameworkElement fe)
        {
            double tr = GetTopRight(fe), bl = GetBottomLeft(fe);
            fe.Clip = tr > 0 || bl > 0 ? Geometry(fe.ActualWidth, fe.ActualHeight, tr, bl) : null;
        }

        // Polygon clockwise from the top left; a cut never exceeds half the side
        internal static Geometry Geometry(double w, double h, double topRight, double bottomLeft)
        {
            double tr = System.Math.Min(topRight, System.Math.Min(w, h) / 2);
            double bl = System.Math.Min(bottomLeft, System.Math.Min(w, h) / 2);
            var fig = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new PolyLineSegment(new[]
            {
                new Point(w - tr, 0), new Point(w, tr),
                new Point(w, h),
                new Point(bl, h), new Point(0, h - bl),
            }, isStroked: false));
            var g = new PathGeometry(new[] { fig });
            g.Freeze();
            return g;
        }
    }
}
