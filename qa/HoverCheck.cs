using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace SidePage
{
    internal static class HoverCheck
    {
        private static readonly Point Outside = new Point(-100000, -100000);
        private static Point cursor;
        private static MethodInfo observe;
        internal static long Now { get; private set; }
        internal static bool IsHandleHovered { get; private set; }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[3] == "--child")
                    return Run(args[0], args[1], args[2]);

                // Reuse the existing launcher, which starts this executable on a hidden desktop.
                MethodInfo launcher = typeof(DockCheck).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
                return (int)launcher.Invoke(null, new object[] { args });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetBaseException());
                return 1;
            }
        }

        internal static bool ReadPointer(out int x, out int y)
        {
            x = (int)Math.Round(cursor.X);
            y = (int)Math.Round(cursor.Y);
            return true;
        }

        private static int Run(string xaml, string iconPath, string data)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "introduced"), "");
            FrameworkElement panel;
            using (var file = File.OpenRead(xaml)) panel = (FrameworkElement)XamlReader.Load(file);
            var board = new TaskBoard(panel, data);
            using (var icon = new Icon(iconPath))
            {
                var host = new DockHost(panel, board, icon, data);
                host.Start();
                try { Exercise(host, panel, board); }
                finally
                {
                    app.Shutdown();
                    Pump(60);
                }
            }
            return 0;
        }

        private static void Exercise(DockHost host, FrameworkElement panel, TaskBoard board)
        {
            observe = typeof(DockHost).GetMethod("ObservePointer", BindingFlags.Instance | BindingFlags.NonPublic);
            var timer = Field<DispatcherTimer>(host, "pointerTimer");
            var handle = Field<Button>(host, "handleButton");
            var drawer = Field<Window>(host, "drawer");
            var surface = Field<Viewbox>(host, "drawerSurface");
            var pin = (Button)panel.FindName("PinButton");
            var close = (Button)panel.FindName("CloseButton");
            timer.Stop();
            Pump(90);
            Require(handle.IsVisible && !drawer.IsVisible, "Subsequent launch begins collapsed");

            Point edge = handle.PointToScreen(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));
            Sample(host, 100, edge, true);
            Sample(host, 259, edge, true);
            Require(!drawer.IsVisible, "No opening at 159 ms");
            Sample(host, 260, Outside, false);
            Sample(host, 600, edge, true);
            Sample(host, 759, edge, true);
            Require(!drawer.IsVisible, "Brief departure resets the hover dwell");

            SetPointer(760, edge, true);
            timer.Start();
            Pump(180);
            timer.Stop();
            Require(drawer.IsVisible && surface.IsHitTestVisible, "Dispatcher opens the native panel at 160 ms");
            Pump(200);

            Point inside = panel.PointToScreen(new Point(panel.ActualWidth / 2, panel.ActualHeight / 2));
            Sample(host, 900, Outside, false);
            Sample(host, 1350, inside, false);
            Sample(host, 1600, Outside, false);
            Sample(host, 2099, Outside, false);
            Require(drawer.IsVisible && surface.IsHitTestVisible, "Re-entry resets the leave timer, and 499 ms stays open");
            Sample(host, 2100, Outside, false);
            Require(!surface.IsHitTestVisible, "Collapse starts on the 500 ms deadline");
            Pump(400);
            Require(!drawer.IsVisible, "Automatic collapse hides the native drawer window");

            Hover(host, 3000, edge);
            Click(pin);
            Sample(host, 3300, Outside, false);
            Sample(host, 4500, Outside, false);
            Require(drawer.IsVisible && surface.IsHitTestVisible && object.Equals(pin.Tag, true),
                "Pin prevents automatic collapse");

            SetPointer(4550, edge, false);
            Click(close);
            Sample(host, 4560, edge, false);
            Pump(400);
            Sample(host, 5000, edge, true);
            Sample(host, 5300, edge, true);
            Require(!drawer.IsVisible && object.Equals(pin.Tag, false),
                "Manual close resets pin and stays closed until the pointer leaves the handle");
            Sample(host, 5350, Outside, false);
            Hover(host, 5400, edge);
            Require(drawer.IsVisible, "Fresh hover after leaving can reopen the panel");

            Click((Button)panel.FindName("UrgentAdd"));
            ((TextBox)panel.FindName("UrgentEntry")).Text = "hover check";
            Sample(host, 6000, Outside, false);
            Sample(host, 7000, Outside, false);
            Require(drawer.IsVisible && surface.IsHitTestVisible && board.IsInteracting,
                "Active editing prevents automatic collapse");

            board.FinishEditing();
            Sample(host, 7150, Outside, false);
            Sample(host, 7649, Outside, false);
            Require(drawer.IsVisible && surface.IsHitTestVisible, "Finishing an edit starts a fresh leave timeout");
            Sample(host, 7650, Outside, false);
            Pump(400);
            Require(!drawer.IsVisible && board.UrgentTotal == 1, "Edit is retained and automatic collapse resumes");
        }

        private static void Hover(DockHost host, long start, Point edge)
        {
            Sample(host, start, edge, true);
            Sample(host, start + 160, edge, true);
            Pump(350);
        }

        private static void Sample(DockHost host, long time, Point point, bool hovered)
        {
            SetPointer(time, point, hovered);
            observe.Invoke(host, new object[] { null, EventArgs.Empty });
        }

        private static void SetPointer(long time, Point point, bool hovered)
        {
            Now = time;
            cursor = point;
            IsHandleHovered = hovered;
        }

        private static T Field<T>(DockHost host, string name)
        {
            FieldInfo field = typeof(DockHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return (T)field.GetValue(host);
        }

        private static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
            Console.WriteLine("PASS " + detail);
        }

        private static void Pump(int ms)
        {
            if (Dispatcher.CurrentDispatcher.HasShutdownStarted) return;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
