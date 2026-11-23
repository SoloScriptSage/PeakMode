using System;
using System.Windows.Media.Animation;

namespace PeakModeTray.Controls
{
    // CSS cubic-bezier(x1, y1, x2, y2) timing function. The gauge needle uses
    // (.25, 1.9, .5, 1): a throttle blip that overshoots and settles.
    public sealed class CubicBezierEase : IEasingFunction
    {
        private readonly double _x1, _y1, _x2, _y2;

        public CubicBezierEase(double x1, double y1, double x2, double y2)
        {
            if (x1 < 0 || x1 > 1 || x2 < 0 || x2 > 1)
                throw new ArgumentOutOfRangeException(nameof(x1), "x control points must be in [0, 1]");
            (_x1, _y1, _x2, _y2) = (x1, y1, x2, y2);
        }

        public static CubicBezierEase NeedleBlip { get; } = new(0.25, 1.9, 0.5, 1);

        public double Ease(double normalizedTime)
        {
            double t = Math.Clamp(normalizedTime, 0, 1);
            if (t == 0 || t == 1) return t;
            return Bezier(SolveForX(t), _y1, _y2);
        }

        private static double Bezier(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
        }

        private static double BezierSlope(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
        }

        // Parameter s where x(s) == x: Newton first, bisection if it stalls.
        private double SolveForX(double x)
        {
            double s = x;
            for (int i = 0; i < 8; i++)
            {
                double err = Bezier(s, _x1, _x2) - x;
                if (Math.Abs(err) < 1e-7) return s;
                double d = BezierSlope(s, _x1, _x2);
                if (Math.Abs(d) < 1e-6) break;
                s -= err / d;
            }
            double lo = 0, hi = 1;
            s = x;
            for (int i = 0; i < 40; i++)
            {
                double v = Bezier(s, _x1, _x2);
                if (Math.Abs(v - x) < 1e-7) break;
                if (v < x) lo = s; else hi = s;
                s = (lo + hi) / 2;
            }
            return s;
        }
    }
}
