using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SidePage
{
    internal static partial class DragCheck
    {
        private static string previewFrames;
        private static int frameIndex;
        private static readonly Stopwatch previewClock = new Stopwatch();

        private static void MakePreview(string storage, string frames)
        {
            previewFrames = frames;
            Directory.CreateDirectory(frames);
            var notes = new ThoughtJournal(panel, storage, delegate { return board.IsDraggingOrSettling; }, board.FinishEditing);
            // This preview keeps the drawer pinned while capturing and reviewing several examples.
            Find<Button>("PinButton").Tag = true;
            AddExample("Urgent", "确认明天的评审时间");
            AddExample("Urgent", "发出修改后的提案");
            AddExample("Later", "整理工作坊的手写笔记");
            AddExample("Later", "挑几张照片冲印");
            Find<TextBox>("ThoughtEntry").Text = "找找上次听到的那张唱片";
            Press("ThoughtSubmit");
            Layout();
            Pump(1100);
            Layout();
            SaveFrame(Path.Combine(frames, "home.png"), 1.5);
            previewClock.Start();
            Record(640, null);

            FrameworkElement card = Card(Later, "整理工作坊的手写笔记");
            Point start = card.PointToScreen(new Point(card.ActualWidth * .5, card.ActualHeight * .5));
            Drag(Later, "整理工作坊的手写笔记", route =>
            {
                Point finish = TrashPoint();
                route.Over(start, DragDropEffects.Move);
                Record(160, null);
                Record(640, progress =>
                {
                    double t = progress * progress * (3 - 2 * progress);
                    Point at = new Point(start.X + (finish.X - start.X) * t, start.Y + (finish.Y - start.Y) * t);
                    bool accepts = In(at, "TrashTarget") || In(at, "UrgentZone") || In(at, "LaterZone");
                    route.Over(at, accepts ? DragDropEffects.Move : DragDropEffects.None);
                });
                Record(450, null);
                SaveFrame(Path.Combine(frames, "bin-open.png"), 1.5);
                return route.Drop(finish);
            });
            Record(560, null);
            Press("UndoButton");
            Record(380, null);

            TextBox input = Find<TextBox>("ThoughtEntry");
            foreach (string segment in new[] {"查一下", "周末", "展览的", "开放时间"})
            {
                input.AppendText(segment);
                Record(170, null);
            }
            Record(250, null);
            Press("ThoughtSubmit");
            Record(600, null);
            Press("ThoughtToggle");
            Record(500, null);
            Assert(notes.IsReviewOpen, "The complete interaction demo did not open the journal.");
            SaveFrame(Path.Combine(frames, "review.png"), 1.5);
            Record(650, null);
            Button finishThought = FindAction(Find<StackPanel>("ThoughtItems"));
            Assert(finishThought != null, "The review is missing a completion action.");
            finishThought.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, finishThought));
            Record(680, null);
            Press("ThoughtHistoryToggle");
            Record(730, null);
            Press("ThoughtBack");
            Record(500, null);
            Console.WriteLine("PASS recorded " + frameIndex + " native UI frames with real card discard, capture, review and completion");
        }

        private static Button FindAction(DependencyObject node)
        {
            var button = node as Button;
            if (button != null) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var match = FindAction(VisualTreeHelper.GetChild(node, i));
                if (match != null) return match;
            }
            return null;
        }

        private static bool In(Point screen, string name)
        {
            var element = Find<FrameworkElement>(name);
            var at = element.PointFromScreen(screen);
            return element.IsVisible && at.X >= 0 && at.X <= element.ActualWidth && at.Y >= 0 && at.Y <= element.ActualHeight;
        }

        private static void AddExample(string group, string title)
        {
            Press(group + "Add");
            Find<TextBox>(group + "Entry").Text = title;
            board.FinishEditing();
        }

        private static void Press(string name)
        {
            var button = Find<Button>(name);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Record(int milliseconds, Action<double> step)
        {
            var segment = Stopwatch.StartNew();
            while (true)
            {
                long elapsed = segment.ElapsedMilliseconds;
                double progress = Math.Min(1, (double)elapsed / milliseconds);
                if (step != null) step(progress);
                Layout();
                SaveFrame(Path.Combine(previewFrames, string.Format("f-{0:0000}-{1:000000}.png", frameIndex++, previewClock.ElapsedMilliseconds)), 1);
                if (progress >= 1) break;
                int remaining = (int)Math.Min(milliseconds - elapsed, 33) - (int)(segment.ElapsedMilliseconds - elapsed);
                Pump(Math.Max(1, remaining));
            }
        }

        private static void SaveFrame(string path, double scale)
        {
            var bitmap = new RenderTargetBitmap((int)(372*scale), (int)(610*scale), 96*scale, 96*scale, PixelFormats.Pbgra32);
            bitmap.Render(visualRoot);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
    }
}
