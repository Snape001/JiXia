using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SidePage
{
    internal sealed class DiscardMotion
    {
        private readonly BinVisual bin;

        internal DiscardMotion(Grid art)
        {
            if (art != null)
            {
                bin = new BinVisual();
                art.Children.Add(bin);
            }
        }

        internal void Lift()
        {
            if (bin != null)
                bin.Lift();
        }

        internal void SetOpen(bool open)
        {
            if (bin != null)
                bin.SetOpen(open);
        }

        internal bool TryGetMouth(FrameworkElement panel, out Rect mouth)
        {
            if (bin == null || bin.ActualWidth < 1 || bin.ActualHeight < 1)
            {
                mouth = Rect.Empty;
                return false;
            }

            mouth = bin.TransformToVisual(panel).TransformBounds(bin.MouthBounds);
            return !mouth.IsEmpty;
        }

        internal bool Play(Action<double> update, Action finished)
        {
            if (bin == null || !SystemParameters.ClientAreaAnimation)
                return false;

            bin.Play(update, finished);
            return true;
        }

        internal void Reset()
        {
            if (bin != null)
                bin.Reset();
        }

        internal static double Ease(double progress, double start, double end)
        {
            double t = Math.Max(0, Math.Min(1, (progress - start) / (end - start)));
            return t * t * (3 - 2 * t);
        }

        private sealed class BinVisual : FrameworkElement
        {
            private const double WidthUnits = 42;
            private const double HeightUnits = 40;
            private static readonly Point Mouth = new Point(20.7, 15.1);
            private static readonly Point Hinge = new Point(32.4, 13.8);
            private static readonly Brush Shadow = MakeShadow();
            private static readonly Brush Shell = Metal(0, 0.5, 1, 0.5,
                0, 0x51463F, 0.12, 0x877562, 0.24, 0xA38A72, 0.34, 0x786556,
                0.49, 0x6F6052, 0.62, 0x786654, 0.79, 0x5D5147, 1, 0x3B332E);
            private static readonly Brush Flange = Metal(0, 0, 0, 1,
                0, 0x968574, 0.5, 0x5A4D43, 0.75, 0x7F6C5A, 1, 0xA68B70);
            private static readonly Brush Cavity = Metal(0, 0, 0, 1,
                0, 0x392D26, 0.37, 0x211A17, 1, 0x100E0D);
            private static readonly Brush LidPlate = Metal(0.12, 0, 0.86, 1,
                0, 0xBCAB93, 0.21, 0x9D8975, 0.52, 0x776657, 0.85, 0x5F4F42, 1, 0x736151);
            private static readonly Brush LidEdge = Metal(0, 0, 0, 1,
                0, 0x4A3C33, 0.56, 0x625142, 1, 0x998065);
            private static readonly Brush HingeFill = Metal(0, 0, 1, 1,
                0, 0x927D66, 0.48, 0x5D4F44, 1, 0x372F29);
            private static readonly Pen ShellEdge = Line(0x382F2A, 0.85);
            private static readonly Pen TopEdge = Line(0xC0A58A, 0.7, 178);
            private static readonly Pen DarkFlange = Line(0x44362D, 0.8);
            private static readonly Pen GrooveShade = Line(0x342922, 0.78, 188);
            private static readonly Pen GrooveLight = Line(0xD0AA83, 0.66, 102);
            private static readonly Pen BrushLines = Line(0xC6AC95, 0.36, 85);
            private static readonly Pen HandleShade = Line(0x392F28, 2.5);
            private static readonly Pen HandleLight = Line(0xC2AB90, 1.25);
            private static readonly Brush Rivet = Solid(0xB09980);
            private static readonly Brush WrappedLight = Metal(0, 0, 1, 1,
                0, 0xA68B72, 0.54, 0x5F4D40, 1, 0x332923);

            private static readonly Geometry Body = Shape("M 7.4,15 C 7.4,12.7 34,12.7 34,15 L 30.7,32.4 C 29.8,36.6 12,36.4 10.8,32.6 Z");
            private static readonly Geometry Base = Shape("M 11,31.65 C 14.1,34.75 27.9,34.72 30.9,31.58 L 30.45,33.1 C 28.7,36.27 13.1,36.18 11.22,33.25 Z");
            private static readonly Geometry FaceLip = Shape("M 7.65,15.1 C 9.05,20.1 31.85,20.68 34,15.18 C 33.7,18.4 27.15,21 20.8,21 C 14.4,21 8.15,18.88 7.65,15.1 Z");
            private static readonly Geometry FrontGlint = Shape("M 8.3,16.7 C 12.7,20.86 29.45,20.65 33.16,16.8");
            private static readonly Geometry Lid = Shape("M 6.7,13.4 L 6.7,12.35 C 7.07,9.25 13.3,7.72 20.7,7.72 C 28.34,7.72 34.8,9.22 34.9,12.45 L 34.9,13.8 C 34.16,17.32 26.9,18.1 20.8,18.1 C 14,18.1 7.04,16.7 6.7,13.4 Z");
            private static readonly Geometry LidTop = Shape("M 6.8,12.4 C 7.2,9.46 13.78,7.7 20.72,7.7 C 28.02,7.7 34.61,9.47 34.8,12.4 C 34.65,15.34 28.03,16.92 20.72,16.92 C 13.38,16.92 6.92,15.29 6.8,12.4 Z");
            private static readonly Geometry LidPolish = Shape("M 9.1,11.38 C 13.1,8.58 27.9,8.42 32.53,11.5 M 12.35,14.84 C 17.3,16.12 25.28,16.08 29.4,14.86");
            private static readonly Geometry Handle = Shape("M 16.42,11.17 L 17.55,8.18 Q 17.86,7.65 18.66,7.61 L 22.94,7.61 Q 23.65,7.72 24.02,8.4 L 25.01,11.12");
            private static readonly Geometry Brushing = Shape("M 11.18,21.24 L 12.55,29.42 M 11.8,21.83 L 13.0,29.7 M 12.54,21.95 L 13.47,29.22 M 29.9,22.46 L 29.07,29.79 M 30.5,21.78 L 29.57,29.95");

            private static readonly DependencyProperty EntryProperty = DependencyProperty.Register("Entry", typeof(double), typeof(BinVisual),
                new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
            private static readonly DependencyProperty LidProperty = DependencyProperty.Register("LidProgress", typeof(double), typeof(BinVisual),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
            private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("DiscardProgress", typeof(double), typeof(BinVisual),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, ProgressChanged));

            private Action<double> update;
            private int release;

            internal BinVisual()
            {
                IsHitTestVisible = false;
                Focusable = false;
            }

            internal Rect MouthBounds
            {
                get
                {
                    double scale = Scale;
                    Point center = new Point((ActualWidth - WidthUnits * scale) / 2 + Mouth.X * scale,
                        (ActualHeight - HeightUnits * scale) / 2 + Mouth.Y * scale);
                    return new Rect(center.X - 10.9 * scale, center.Y - 2.8 * scale, 21.8 * scale, 5.6 * scale);
                }
            }

            private double Scale { get { return Math.Min(ActualWidth / WidthUnits, ActualHeight / HeightUnits); } }

            internal void Lift()
            {
                Reset();
                if (SystemParameters.ClientAreaAnimation)
                    Animate(EntryProperty, 0.03, 1, 160, new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });
            }

            internal void SetOpen(bool open)
            {
                double to = open ? 1 : 0;
                if (SystemParameters.ClientAreaAnimation)
                    Animate(LidProperty, (double)GetValue(LidProperty), to, open ? 110 : 135,
                        new QuadraticEase { EasingMode = EasingMode.EaseOut });
                else
                    Snap(LidProperty, to);
            }

            internal void Play(Action<double> frame, Action finished)
            {
                int version = ++release;
                Snap(EntryProperty, 1);
                SetOpen(true);
                update = frame;
                frame(0);
                DoubleAnimation motion = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(410)) { FillBehavior = FillBehavior.HoldEnd };
                motion.Completed += delegate
                {
                    if (version == release)
                        finished();
                };
                BeginAnimation(ProgressProperty, motion, HandoffBehavior.SnapshotAndReplace);
            }

            internal void Reset()
            {
                ++release;
                update = null;
                Snap(ProgressProperty, 0);
                Snap(EntryProperty, 1);
                Snap(LidProperty, 0);
            }

            private void Snap(DependencyProperty property, double value)
            {
                BeginAnimation(property, null);
                SetValue(property, value);
            }

            private void Animate(DependencyProperty property, double from, double to, int duration, IEasingFunction easing)
            {
                SetValue(property, to);
                BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                }, HandoffBehavior.SnapshotAndReplace);
            }

            private static void ProgressChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
            {
                Action<double> handler = ((BinVisual)owner).update;
                if (handler != null)
                    handler((double)args.NewValue);
            }

            protected override void OnRender(DrawingContext drawing)
            {
                double scale = Scale;
                if (scale <= 0)
                    return;

                double entry = (double)GetValue(EntryProperty);
                double progress = (double)GetValue(ProgressProperty);
                double lid = (double)GetValue(LidProperty) * (1 - Ease(progress, 0.73, 0.907));
                double settle = Ease(progress, 0.61, 0.70) * (1 - Ease(progress, 0.70, 0.81));
                settle -= 0.22 * Ease(progress, 0.77, 0.85) * (1 - Ease(progress, 0.85, 0.96));

                drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale,
                    (ActualWidth - WidthUnits * scale) / 2, (ActualHeight - HeightUnits * scale) / 2));
                drawing.PushOpacity(Math.Min(1, 0.22 + entry * 0.78));
                drawing.DrawEllipse(Shadow, null, new Point(21, 36.2), 14.1 + settle * 0.8, 2.85);
                drawing.PushTransform(new TranslateTransform(0, -5.2 * (1 - entry) + settle * 1.35));
                drawing.PushTransform(new ScaleTransform(1 + 0.034 * settle, 1 - 0.064 * settle, 20.7, 34));
                DrawBody(drawing);
                DrawLid(drawing, lid);
                drawing.Pop();
                drawing.Pop();
                drawing.Pop();
                drawing.Pop();
            }

            private static void DrawBody(DrawingContext drawing)
            {
                drawing.DrawGeometry(Shell, ShellEdge, Body);
                drawing.DrawGeometry(WrappedLight, ShellEdge, Base);
                drawing.DrawGeometry(null, BrushLines, Brushing);
                for (int groove = 0; groove < 4; groove++)
                {
                    double top = 14.3 + groove * 4.4;
                    double bottom = 15.45 + groove * 3.55;
                    double y = 21 + (groove == 1 || groove == 2 ? 0.9 : 0);
                    drawing.DrawLine(GrooveShade, new Point(top, y), new Point(bottom, 30.8));
                    drawing.DrawLine(GrooveLight, new Point(top + 0.7, y + 0.34), new Point(bottom + 0.65, 30.86));
                }

                drawing.DrawEllipse(Flange, ShellEdge, Mouth, 13.45, 5.1);
                drawing.DrawEllipse(Cavity, DarkFlange, new Point(Mouth.X, Mouth.Y - 0.35), 11.52, 3.7);
                drawing.DrawGeometry(Flange, null, FaceLip);
                drawing.DrawGeometry(null, TopEdge, FrontGlint);
            }

            private static void DrawLid(DrawingContext drawing, double opened)
            {
                drawing.PushTransform(new RotateTransform(24 * opened, Hinge.X, Hinge.Y));
                drawing.DrawGeometry(LidEdge, ShellEdge, Lid);
                drawing.DrawGeometry(LidPlate, TopEdge, LidTop);
                drawing.DrawGeometry(null, BrushLines, LidPolish);
                drawing.DrawGeometry(null, HandleShade, Handle);
                drawing.PushTransform(new TranslateTransform(0, -0.32));
                drawing.DrawGeometry(null, HandleLight, Handle);
                drawing.Pop();
                drawing.Pop();
                drawing.DrawRoundedRectangle(HingeFill, ShellEdge, new Rect(31.5, 12.4, 4.25, 4.36), 1.1, 1.1);
                drawing.DrawEllipse(Rivet, null, new Point(33.62, 14.42), 0.64, 0.6);
            }

            private static Pen Line(int rgb, double thickness, byte alpha = 255)
            {
                Pen pen = new Pen(Solid(rgb, alpha), thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();
                return pen;
            }

            private static Geometry Shape(string data)
            {
                Geometry geometry = Geometry.Parse(data);
                geometry.Freeze();
                return geometry;
            }

            private static Color Rgb(int rgb, byte alpha = 255)
            {
                return Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }

            private static Brush Solid(int rgb, byte alpha = 255)
            {
                SolidColorBrush brush = new SolidColorBrush(Rgb(rgb, alpha));
                brush.Freeze();
                return brush;
            }

            private static Brush Metal(double x0, double y0, double x1, double y1, params double[] stops)
            {
                LinearGradientBrush gradient = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
                for (int i = 0; i < stops.Length; i += 2)
                    gradient.GradientStops.Add(new GradientStop(Rgb((int)stops[i + 1]), stops[i]));
                gradient.Freeze();
                return gradient;
            }

            private static Brush MakeShadow()
            {
                RadialGradientBrush brush = new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop(Rgb(0x070606, 196), 0));
                brush.GradientStops.Add(new GradientStop(Rgb(0x0C0807, 103), 0.57));
                brush.GradientStops.Add(new GradientStop(Rgb(0x0C0807, 0), 1));
                brush.Freeze();
                return brush;
            }
        }
    }
}
