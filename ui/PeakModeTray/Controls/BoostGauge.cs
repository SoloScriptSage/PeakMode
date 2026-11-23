using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
// ImplicitUsings + UseWindowsForms imports System.Drawing / System.Windows.Forms
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace PeakModeTray.Controls
{
    public enum GaugeMode { Idle, Applying, Active, Restoring, RestorePending }

    // The Boost hero: the PeakMode logo (assets/peakmode-logo.svg, 100 x 100
    // space) as a live tachometer. Driven by Mode and Progress (steps done /
    // steps enabled), which the Boost view model sets from core events, so
    // the needle moves when the core moves. Decorative: the Boost screen
    // wraps it in a Button that carries the automation name.
    //
    // Idle: needle at -120, ticks dim. Applying/Restoring: needle at
    // -120 + 215 * Progress with an overshoot blip, ticks up to it lit, the
    // hex shakes. Active: needle around +95 revving, redline flickers, pink
    // glow pulses. With Windows animations off it jumps between states.
    public sealed class BoostGauge : Grid
    {
        private const double Cx = 50, Cy = 53;          // pivot
        private const double MinAngle = -120, Sweep = 215, RevBase = 95;

        // Redline tick colors from the logo, +40 to +120 degrees
        private static readonly Color[] RedlineColors =
        {
            Rgb(0xFF3D7F), Rgb(0xFF4F63), Rgb(0xFF6A45), Rgb(0xFF8A30), Rgb(0xFFB020),
        };
        private static readonly Color TickDim    = Rgb(0x3A4050);
        private static readonly Color TickLit    = Rgb(0xE9E7F2);
        private static readonly Color RedDim     = Rgb(0x4A1D33);
        private static readonly Color InnerHex   = Rgb(0x2A3040);
        private static readonly Color PivotFill  = Rgb(0x0A0B10);
        private static readonly Color GlowPink   = Rgb(0xFF3D7F);

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(GaugeMode), typeof(BoostGauge),
            new PropertyMetadata(GaugeMode.Idle, (d, _) => ((BoostGauge)d).Update()));

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(BoostGauge),
            new PropertyMetadata(0.0, (d, _) => ((BoostGauge)d).Update(), (_, v) => Math.Clamp((double)v, 0, 1)));

        // null = follow Windows "Show animations" (SystemParameters.ClientAreaAnimation)
        public static readonly DependencyProperty AnimationsOverrideProperty = DependencyProperty.Register(
            nameof(AnimationsOverride), typeof(bool?), typeof(BoostGauge),
            new PropertyMetadata(null, (d, _) => ((BoostGauge)d).Update()));

        public GaugeMode Mode     { get => (GaugeMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
        public double    Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
        public bool?     AnimationsOverride
        {
            get => (bool?)GetValue(AnimationsOverrideProperty);
            set => SetValue(AnimationsOverrideProperty, value);
        }

        public bool   AnimationsOn => AnimationsOverride ?? SystemParameters.ClientAreaAnimation;
        public double NeedleTarget { get; private set; } = MinAngle;

        private readonly Viewbox            _box;
        private readonly TranslateTransform _shake = new();
        private readonly DropShadowEffect   _glow  = new() { Color = GlowPink, ShadowDepth = 0, BlurRadius = 22, Opacity = 0 };
        private readonly Canvas             _redLit;
        private readonly Line[]             _greyLit = new Line[8];
        private readonly Line[]             _redLitLines = new Line[5];
        private readonly double[]           _tickAngles = new double[13];
        private readonly RotateTransform    _base = new(MinAngle, Cx, Cy);
        private readonly RotateTransform    _rev  = new(0, Cx, Cy);
        private readonly TextBlock          _label;
        private (GaugeMode mode, bool anim)? _motionFor;   // what the loops are running for

        public BoostGauge()
        {
            var canvas = new Canvas { Width = 100, Height = 100, ClipToBounds = false };
            var heat   = (Brush?)TryFindResource("HeatBrush") ?? HeatFallback();
            var tile   = (Brush?)TryFindResource("LogoTileBrush") ?? new SolidColorBrush(Rgb(0x12141C));

            // Hexagon with heat rim, inner hex line
            canvas.Children.Add(new Polygon
            {
                Points = Pts("50,4 89.84,27 89.84,73 50,96 10.16,73 10.16,27"),
                Fill = tile, Stroke = heat, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round,
            });
            canvas.Children.Add(new Polygon
            {
                Points = Pts("50,11 83.77,30.5 83.77,69.5 50,89 16.23,69.5 16.23,30.5"),
                Stroke = new SolidColorBrush(InnerHex), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
            });

            // 13 ticks, -120..+120 every 20; grey below +40, redline from +40
            _redLit = new Canvas
            {
                Effect = new DropShadowEffect { Color = GlowPink, ShadowDepth = 0, BlurRadius = 7, Opacity = 0.9 },
            };
            int g = 0, r = 0;
            for (int i = 0; i < 13; i++)
            {
                double a = MinAngle + 20 * i;
                _tickAngles[i] = a;
                bool red = a >= 40;
                double r2 = red ? 21 : 25;
                double w  = red ? 4.5 : 3;
                canvas.Children.Add(Tick(a, r2, w, red ? RedDim : TickDim, 1));
                if (red) { _redLitLines[r] = Tick(a, r2, w, RedlineColors[r], 0); _redLit.Children.Add(_redLitLines[r]); r++; }
                else     { _greyLit[g] = Tick(a, r2, w, TickLit, 0); canvas.Children.Add(_greyLit[g]); g++; }
            }
            canvas.Children.Add(_redLit);

            // Needle (white, glow) on two rotations: base angle + rev wobble.
            // The logo's kite (80.91,44.72 51.04,56.86 42.27,55.07 48.96,49.14,
            // drawn at +75 degrees) rotated back to pointing straight up.
            var rot = new TransformGroup();
            rot.Children.Add(_rev);
            rot.Children.Add(_base);
            canvas.Children.Add(new Polygon
            {
                Points = Pts("50,21 54,53 50,61 46,53"),
                Fill = Brushes.White,
                RenderTransform = rot,
                Effect = new DropShadowEffect { Color = Colors.White, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.85 },
            });

            // Pivot ring (Path in canvas space so the absolute heat gradient lines up)
            canvas.Children.Add(new Path
            {
                Data = new EllipseGeometry(new Point(Cx, Cy), 5.5, 5.5),
                Fill = new SolidColorBrush(PivotFill), Stroke = heat, StrokeThickness = 2.8,
            });

            _label = new TextBlock
            {
                Width = 100, TextAlignment = TextAlignment.Center,
                FontFamily = (FontFamily?)TryFindResource("DisplayFont") ?? new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush?)TryFindResource("TextBrush") ?? Brushes.White,
            };
            canvas.Children.Add(_label);

            _box = new Viewbox
            {
                Child = canvas, Stretch = Stretch.Uniform,
                RenderTransform = _shake, Effect = _glow,
            };
            Children.Add(_box);

            Loaded   += (_, _) => { SystemParameters.StaticPropertyChanged += OnSystemParameterChanged; Update(); };
            Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            Update();
        }

        private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Update();
        }

        // ── state → visuals ─────────────────────────────────────────────────
        private void Update()
        {
            if (_label == null) return; // during construction
            bool anim = AnimationsOn;
            var mode  = Mode;

            NeedleTarget = mode switch
            {
                GaugeMode.Applying or GaugeMode.Restoring => MinAngle + Sweep * Progress,
                GaugeMode.Active => RevBase,
                _ => MinAngle,
            };
            AnimateNeedle(NeedleTarget, anim);
            UpdateTicks(mode, anim);
            UpdateLabel(mode);
            UpdateMotion(mode, anim);
        }

        private void AnimateNeedle(double to, bool anim)
        {
            if (!anim)
            {
                _base.BeginAnimation(RotateTransform.AngleProperty, null);
                _base.Angle = to;
                return;
            }
            // From the current (possibly mid-animation) angle: handoff
            _base.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(450)) { EasingFunction = CubicBezierEase.NeedleBlip },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void UpdateTicks(GaugeMode mode, bool anim)
        {
            double upto = mode switch
            {
                GaugeMode.Active => 120,
                GaugeMode.Applying or GaugeMode.Restoring => NeedleTarget,
                _ => double.NegativeInfinity,
            };
            int g = 0, r = 0;
            foreach (double a in _tickAngles)
            {
                double on = a <= upto + 0.5 ? 1 : 0;
                Line l = a >= 40 ? _redLitLines[r++] : _greyLit[g++];
                Fade(l, on, anim);
            }
        }

        private static void Fade(UIElement e, double to, bool anim)
        {
            if (!anim) { e.BeginAnimation(OpacityProperty, null); e.Opacity = to; return; }
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200)),
                             HandoffBehavior.SnapshotAndReplace);
        }

        private void UpdateLabel(GaugeMode mode)
        {
            (string text, double size) = mode switch
            {
                GaugeMode.Applying       => ("BOOSTING", 7.0),
                GaugeMode.Active         => ("ACTIVE", 8.5),
                GaugeMode.Restoring      => ("RESTORING", 6.2),
                GaugeMode.RestorePending => ("RESTORE", 7.4),
                _                        => ("BOOST", 9.5),
            };
            _label.Text     = text;
            _label.FontSize = size;
            // SVG text y = 79 is the baseline
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(_label.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size, Brushes.White, 1.0);
            Canvas.SetTop(_label, 79 - ft.Baseline);
        }

        // Loops: shake while busy; rev + flicker + glow pulse + vibe while active
        private void UpdateMotion(GaugeMode mode, bool anim)
        {
            if (_motionFor == (mode, anim)) return;
            _motionFor = (mode, anim);

            StopLoop(_shake, TranslateTransform.XProperty, 0);
            StopLoop(_shake, TranslateTransform.YProperty, 0);
            StopLoop(_rev, RotateTransform.AngleProperty, 0);
            StopLoop(_redLit, OpacityProperty, 1);
            StopLoop(_glow, DropShadowEffect.BlurRadiusProperty, 22);
            StopLoop(_glow, DropShadowEffect.OpacityProperty, 0);

            bool busy = mode is GaugeMode.Applying or GaugeMode.Restoring;
            if (mode == GaugeMode.Active)
            {
                if (!anim) { _glow.BlurRadius = 30; _glow.Opacity = 0.6; return; }
                _rev.BeginAnimation(RotateTransform.AngleProperty, RevLoop());
                _redLit.BeginAnimation(OpacityProperty, Keys(0.35, (0, 1), (0.30, 0.5), (0.45, 0.95), (0.70, 0.6), (1, 1)));
                _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, Pulse(22, 38, 1.8));
                _glow.BeginAnimation(DropShadowEffect.OpacityProperty, Pulse(0.45, 0.8, 1.8));
                _shake.BeginAnimation(TranslateTransform.XProperty, Keys(0.09, (0, 0), (0.5, 0.4), (1, 0)));
                _shake.BeginAnimation(TranslateTransform.YProperty, Keys(0.09, (0, 0), (0.5, 0.3), (1, 0)));
            }
            else if (busy && anim)
            {
                _shake.BeginAnimation(TranslateTransform.XProperty, Keys(0.12, (0, 0), (0.25, 0.6), (0.75, -0.6), (1, 0)));
                _shake.BeginAnimation(TranslateTransform.YProperty, Keys(0.12, (0, 0), (0.25, -0.6), (0.75, 0.6), (1, 0)));
            }
        }

        private static void StopLoop(Animatable target, DependencyProperty p, double rest)
        {
            target.BeginAnimation(p, null);
            target.SetValue(p, rest);
        }

        private static void StopLoop(UIElement target, DependencyProperty p, double rest)
        {
            target.BeginAnimation(p, null);
            target.SetValue(p, rest);
        }

        // pmRev: +19, +9, +23, +3, +17, -3, +21, +6, 0 over 1.4 s, ease-in-out per segment
        private static DoubleAnimationUsingKeyFrames RevLoop()
        {
            var a = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(1.4), RepeatBehavior = RepeatBehavior.Forever,
            };
            var ease = new KeySpline(0.42, 0, 0.58, 1);
            (double t, double v)[] k =
            {
                (0, 0), (0.10, 19), (0.18, 9), (0.28, 23), (0.40, 3), (0.52, 17),
                (0.62, -3), (0.74, 21), (0.86, 6), (1, 0),
            };
            foreach (var (t, v) in k)
                a.KeyFrames.Add(new SplineDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4 * t)), ease));
            return a;
        }

        // Linear keyframes at fractions of `seconds`, repeating
        private static DoubleAnimationUsingKeyFrames Keys(double seconds, params (double t, double v)[] k)
        {
            var a = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever,
            };
            foreach (var (t, v) in k)
                a.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * t))));
            return a;
        }

        private static DoubleAnimation Pulse(double from, double to, double period) =>
            new(from, to, TimeSpan.FromSeconds(period / 2))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };

        // ── geometry helpers ────────────────────────────────────────────────
        private static Line Tick(double angle, double rInner, double width, Color c, double opacity)
        {
            double t = angle * Math.PI / 180;
            return new Line
            {
                X1 = Cx + 30 * Math.Sin(t), Y1 = Cy - 30 * Math.Cos(t),
                X2 = Cx + rInner * Math.Sin(t), Y2 = Cy - rInner * Math.Cos(t),
                Stroke = new SolidColorBrush(c), StrokeThickness = width,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Opacity = opacity,
            };
        }

        private static PointCollection Pts(string s) => PointCollection.Parse(s);

        private static Color Rgb(int rgb) =>
            Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static Brush HeatFallback() => new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(12, 88), EndPoint = new Point(88, 12),
            GradientStops = { new(Rgb(0x7C5CFF), 0), new(Rgb(0xFF3D7F), 0.55), new(Rgb(0xFFB020), 1) },
        };
    }
}
