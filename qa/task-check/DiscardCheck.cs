using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SidePage
{
    internal static class DiscardCheck
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static FrameworkElement panel;
        private static AdornerDecorator root;
        private static TaskBoard board;
        private static string storage;

        private static void Check(bool condition, string title)
        {
            if (!condition) throw new InvalidOperationException(title);
            Console.WriteLine("PASS " + title);
        }

        private static void Invoke(string name, params object[] args)
        {
            typeof(TaskBoard).GetMethod(name, Instance).Invoke(board, args);
        }

        private static void Set(string name, object value)
        {
            typeof(TaskBoard).GetField(name, Instance).SetValue(board, value);
        }

        private static object Get(string name)
        {
            return typeof(TaskBoard).GetField(name, Instance).GetValue(board);
        }

        private static T Find<T>(string name) where T : FrameworkElement
        {
            return (T)panel.FindName(name);
        }

        private static void Click(string name)
        {
            Button element = Find<Button>(name);
            element.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, element));
        }

        private static void Add(string text)
        {
            Click("UrgentAdd");
            Find<TextBox>("UrgentEntry").Text = text;
            board.FinishEditing();
            root.UpdateLayout();
        }

        private static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Picture(Visual visual, int width, int height, string folder, string filename)
        {
            RenderTargetBitmap image = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
            image.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream fs = File.Create(Path.Combine(folder, filename))) encoder.Save(fs);
        }

        private static FrameworkElement SimulateDrag(int index)
        {
            FrameworkElement card = (FrameworkElement)Find<StackPanel>("UrgentItems").Children[index];
            Set("draggedCard", card);
            Invoke("Refresh");
            root.UpdateLayout();
            card.Visibility = Visibility.Collapsed;

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext dc = drawing.Open())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(104, 82, 68)), null, new Rect(0, 0, 290, 35), 8, 8);
                dc.DrawRectangle(Brushes.BurlyWood, null, new Rect(11, 11, 95, 3));
                dc.DrawRectangle(Brushes.BurlyWood, null, new Rect(11, 20, 120, 3));
            }
            Type ghostType = typeof(TaskBoard).GetNestedType("CardGhost", BindingFlags.NonPublic);
            var ghost = (Adorner)Activator.CreateInstance(ghostType, new object[] { panel, new DrawingImage(drawing), new Size(290, 35) });
            root.AdornerLayer.Add(ghost);
            Set("ghost", ghost);
            Set("ghostLayer", root.AdornerLayer);
            Invoke("UpdateGhost", new Point(187, 557), true);
            ((DiscardMotion)Get("discardMotion")).Lift();
            return card;
        }

        private static void Release(FrameworkElement card)
        {
            Set("draggedCard", null);
            card.Visibility = Visibility.Visible;
            Invoke("BeginDiscard", card);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                storage = Path.Combine(args[1], "discard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(storage);
                using (FileStream source = File.OpenRead(args[0])) panel = (FrameworkElement)XamlReader.Load(source);
                root = new AdornerDecorator { Child = panel };
                board = new TaskBoard(panel, storage);
                root.Measure(new Size(372, 610));
                root.Arrange(new Rect(0, 0, 372, 610));
                root.UpdateLayout();
                Add("immediate-save-card");
                Add("preserved-card");
                Grid art = Find<Grid>("BinArt");
                Check(art.Children.Count == 1, "BinArt hosts the drawn bin visual");

                FrameworkElement card = SimulateDrag(0);
                Pump(190);
                Picture(art, 42, 40, storage, "bin-closed.png");
                Invoke("SetTrashHot", true);
                Pump(150);
                Picture(art, 42, 40, storage, "bin-open.png");
                Rect mouth;
                var child = (FrameworkElement)art.Children[0];
                Console.WriteLine("Art " + child.ActualWidth + "x" + child.ActualHeight + " visible " + child.IsVisible + " " + Find<FrameworkElement>("TrashTarget").Visibility + " " + art.TranslatePoint(new Point(), panel));
                Check(((DiscardMotion)Get("discardMotion")).TryGetMouth(panel, out mouth) && mouth.Width > 20 && mouth.Top > 500,
                    "Landing coordinate maps into the rendered bin opening");

                Release(card);
                string xml = Path.Combine(storage, "tasks.xml");
                Check(board.UrgentTotal == 1 && !File.ReadAllText(xml).Contains("immediate-save-card"),
                    "Release commits memory and disk before video frames");
                if (SystemParameters.ClientAreaAnimation)
                {
                    Check(board.IsInteracting && board.IsDraggingOrSettling && Find<FrameworkElement>("UndoBar").Visibility == Visibility.Collapsed,
                        "Undo stays hidden during the card flight");
                    Pump(210);
                    Picture(root, 372, 610, storage, "bin-flight.png");
                    Check(board.IsDraggingOrSettling, "Mid-flight retains the interaction guard");
                    Pump(290);
                }
                Check(!board.IsInteracting && Get("ghost") == null && Find<FrameworkElement>("UndoBar").Visibility == Visibility.Visible,
                    "Completed motion clears the ghost and offers undo");
                IList removals = (IList)Get("removals");
                TimeSpan deadline = (TimeSpan)removals[0].GetType().GetField("Expires", Instance).GetValue(removals[0]);
                TimeSpan remaining = deadline - ((Stopwatch)Get("uptime")).Elapsed;
                Check(remaining.TotalSeconds > 4.8 && remaining.TotalSeconds <= 5.01,
                    "Undo window starts after presentation completes");
                Click("UndoButton");
                Check(board.UrgentTotal == 2 && File.ReadAllText(xml).Contains("immediate-save-card"),
                    "Immediate undo restores the same card and persists");

                card = SimulateDrag(0);
                Invoke("SetTrashHot", true);
                Release(card);
                Invoke("OnPanelUnloaded", panel, new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Check(!board.IsInteracting && Get("ghost") == null && Find<FrameworkElement>("UndoBar").Visibility == Visibility.Visible &&
                    removals.Count == 1 && board.UrgentTotal == 1,
                    "Unloading cancels presentation and offers one undo without duplicating the committed removal");
                Click("UndoButton");
                Pump(455);
                Check(board.UrgentTotal == 2 && removals.Count == 0,
                    "A stale presentation clock cannot delete an undone card");
                Console.WriteLine("Frames " + storage);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetBaseException());
                return 1;
            }
        }
    }
}
