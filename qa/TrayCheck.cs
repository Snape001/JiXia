using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace SidePage
{
    internal static class TrayCheck
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int WmDpiChanged = 0x02e0;
        private static readonly FieldInfo LayoutDpi = typeof(TrayMenu).GetField("layoutDpi", PrivateInstance);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref NativeRect lParam);

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length == 4 && args[3] == "--child")
                    return Run(args[0], args[1], args[2]);

                MethodInfo launcher = typeof(DockCheck).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
                return (int)launcher.Invoke(null, new object[] { args });
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetBaseException());
                return 1;
            }
        }

        private static int Run(string xamlPath, string iconPath, string directory)
        {
            Directory.CreateDirectory(directory);
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                CheckCommands();
                CheckScreens();
                CheckHost(xamlPath, iconPath, directory);
            }
            finally
            {
                if (!app.Dispatcher.HasShutdownStarted)
                    app.Shutdown();
            }

            return 0;
        }

        private static void CheckCommands()
        {
            int togglePanel = 0;
            int togglePin = 0;
            int quit = 0;
            using (TrayMenu menu = new TrayMenu(delegate { togglePanel++; }, delegate { togglePin++; }, delegate { quit++; }))
            {
                menu.SetState(false, false);
                Forms.ToolStripMenuItem panel = (Forms.ToolStripMenuItem)menu.Items[0];
                Forms.ToolStripMenuItem pin = (Forms.ToolStripMenuItem)menu.Items[1];
                Forms.ToolStripMenuItem exit = (Forms.ToolStripMenuItem)menu.Items[3];
                Check(menu.Items.Count == 4 && menu.Items[2] is Forms.ToolStripSeparator &&
                    panel.Text == "打开记下" && pin.Text.Contains("固定") && exit.Text == "退出记下" &&
                    panel.Enabled && pin.Enabled && exit.Enabled && !pin.Checked,
                    "Collapsed menu exposes open, pin and exit in order");
                panel.PerformClick();
                menu.SetState(true, false);
                Check(togglePanel == 1 && panel.Text == "收起记下", "Open callback and expanded caption are connected");
                panel.PerformClick();
                pin.PerformClick();
                menu.SetState(true, true);
                Check(togglePanel == 2 && togglePin == 1 && pin.Checked, "Collapse and pin callbacks are connected");
                pin.PerformClick();
                menu.SetState(true, false);
                Check(togglePin == 2 && !pin.Checked, "The same pin command unpins and clears its native check state");
                exit.PerformClick();
                Check(quit == 1 && togglePanel == 2 && togglePin == 2 && !Application.Current.Dispatcher.HasShutdownStarted,
                    "Exit calls the test callback once without shutting down the checker");
            }
        }

        private static void CheckScreens()
        {
            Forms.Screen[] screens = Forms.Screen.AllScreens;
            Check(screens.Length > 0, "At least one native screen is available on the isolated desktop");
            bool nativeHighDpi = false;
            bool nativeStandardDpi = false;
            using (TrayMenu menu = new TrayMenu(delegate { }, delegate { }, delegate { }))
            {
                menu.SetState(true, true);
                foreach (Forms.Screen screen in screens)
                {
                    Rectangle area = screen.WorkingArea;
                    Show(menu, Center(area));
                    int dpi = NativeDpi(menu);
                    string label = dpi + " DPI native " + screen.DeviceName;
                    nativeStandardDpi |= dpi == 96;
                    nativeHighDpi |= dpi > 96;
                    Check((int)LayoutDpi.GetValue(menu) == dpi, label + " aligns its artwork DPI with the popup HWND");
                    CheckLayout(menu, dpi, label);
                    Size popupSize = menu.Size;
                    menu.Close();
                    CheckCorners(menu, area, popupSize, label);
                }

                if (!nativeStandardDpi)
                    CheckSyntheticDpi(menu, 96, Forms.Screen.PrimaryScreen.WorkingArea);
                if (!nativeHighDpi)
                    CheckSyntheticDpi(menu, 144, Forms.Screen.PrimaryScreen.WorkingArea);

                Forms.Screen primary = Forms.Screen.PrimaryScreen;
                ShowFromTaskbar(menu, TaskbarAnchor(primary));
                Check(primary.Bounds.Contains(menu.Bounds), "NotifyIcon's native taskbar popup fits on its display");
                menu.Close();
            }
        }

        private static void CheckSyntheticDpi(TrayMenu menu, int target, Rectangle area)
        {
            Show(menu, Center(area));
            int native = NativeDpi(menu);
            Size projectedSize;
            try
            {
                string label = target + " DPI synthetic metrics on " + native + " DPI display";
                SimulateDpiChange(menu, target);
                menu.PerformLayout();
                menu.Size = menu.GetPreferredSize(Size.Empty);
                menu.PerformLayout();
                Check((int)LayoutDpi.GetValue(menu) == target && NativeDpi(menu) == native,
                    label + " exercise the production WM_DPICHANGED path without changing monitor DPI");
                CheckLayout(menu, target, label);
                projectedSize = menu.Size;
            }
            finally
            {
                SimulateDpiChange(menu, native);
                menu.Close();
            }

            // WinForms restores the screen's actual DPI whenever a popup opens. Keep the measured
            // projected footprint fixed while exercising the real WinForms placement on that screen.
            bool autoSize = menu.AutoSize;
            Size minimum = menu.MinimumSize;
            menu.AutoSize = false;
            menu.MinimumSize = Size.Empty;
            menu.Size = projectedSize;
            try { CheckCorners(menu, area, projectedSize, target + " DPI projected size with native popup positioning"); }
            finally
            {
                menu.Close();
                menu.MinimumSize = minimum;
                menu.AutoSize = autoSize;
            }
        }

        private static void SimulateDpiChange(TrayMenu menu, int dpi)
        {
            Rectangle bounds = menu.Bounds;
            var suggested = new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            SendMessage(menu.Handle, WmDpiChanged, new IntPtr(dpi | (dpi << 16)), ref suggested);
        }

        private static int NativeDpi(TrayMenu menu)
        {
            try { return checked((int)GetDpiForWindow(menu.Handle)); }
            catch (EntryPointNotFoundException) { return menu.DeviceDpi; }
        }

        private static void CheckLayout(TrayMenu menu, int dpi, string label)
        {
            int minimumRow = Pixels(33, dpi);
            Rectangle first = menu.Items[0].Bounds;
            Rectangle second = menu.Items[1].Bounds;
            Rectangle divider = menu.Items[2].Bounds;
            Rectangle last = menu.Items[3].Bounds;
            Check(first.Height >= minimumRow && second.Height >= minimumRow && last.Height >= minimumRow &&
                Math.Abs(first.Height - last.Height) <= 1 && divider.Height >= Pixels(5, dpi),
                label + " row and divider heights " + first.Height + "/" + second.Height + "/" + divider.Height + "/" + last.Height);
            Check(first.Bottom <= second.Top && second.Bottom <= divider.Top && divider.Bottom <= last.Top &&
                first.Top >= Pixels(5, dpi) && menu.ClientSize.Height - last.Bottom >= Pixels(5, dpi) &&
                menu.ClientRectangle.Contains(first) && menu.ClientRectangle.Contains(last),
                label + " rows, divider, and outer spacing fit the popup " + menu.ClientRectangle +
                " rows " + first + " " + second + " " + divider + " " + last);

            Dictionary<Forms.ToolStripItem, Rectangle> textBounds = new Dictionary<Forms.ToolStripItem, Rectangle>();
            Dictionary<Forms.ToolStripItem, Size> textSize = new Dictionary<Forms.ToolStripItem, Size>();
            Forms.ToolStripItemTextRenderEventHandler inspect = delegate(object sender, Forms.ToolStripItemTextRenderEventArgs args)
            {
                textBounds[args.Item] = args.TextRectangle;
                textSize[args.Item] = Forms.TextRenderer.MeasureText(args.Graphics, args.Text, args.TextFont,
                    new Size(1600, 400), Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix);
            };
            Forms.ToolStripRenderer renderer = menu.Renderer;
            renderer.RenderItemText += inspect;
            try { menu.Refresh(); }
            finally { renderer.RenderItemText -= inspect; }

            foreach (int i in new[] { 0, 1, 3 })
            {
                Forms.ToolStripItem item = menu.Items[i];
                Rectangle text;
                Size measured;
                if (!textBounds.TryGetValue(item, out text) || !textSize.TryGetValue(item, out measured))
                    throw new InvalidOperationException("No text drawing was observed for " + item.Text + ".");
                Check(text.Left >= 0 && text.Top >= 0 && text.Right <= item.Width && text.Bottom <= item.Height &&
                    text.Width >= measured.Width && text.Height >= measured.Height,
                    label + " full text fits with its render margins for " + item.Text);
            }
        }

        private static void CheckCorners(TrayMenu menu, Rectangle area, Size size, string label)
        {
            Point[] anchors =
            {
                new Point(area.Left + 1, area.Top + 1), new Point(area.Right - 1, area.Top + 1),
                new Point(area.Left + 1, area.Bottom - 1), new Point(area.Right - 1, area.Bottom - 1)
            };
            for (int i = 0; i < anchors.Length; i++)
            {
                Show(menu, anchors[i]);
                Rectangle popup = menu.Bounds;
                Check(area.Contains(popup) && Math.Abs(popup.Width - size.Width) <= 1 &&
                    Math.Abs(popup.Height - size.Height) <= 1 && Rectangle.Inflate(popup, 4, 4).Contains(anchors[i]),
                    label + " popup fits at corner " + (i + 1) + " " + popup);
                menu.Close();
            }
        }

        private static void CheckHost(string xamlPath, string iconPath, string directory)
        {
            FrameworkElement panel;
            using (FileStream input = File.OpenRead(xamlPath))
                panel = (FrameworkElement)XamlReader.Load(input);
            TaskBoard board = new TaskBoard(panel, directory);
            using (Icon icon = new Icon(iconPath))
            {
                DockHost host = new DockHost(panel, board, icon, directory);
                TrayMenu menu = (TrayMenu)typeof(DockHost).GetField("trayMenu", PrivateInstance).GetValue(host);
                Forms.NotifyIcon tray = (Forms.NotifyIcon)typeof(DockHost).GetField("tray", PrivateInstance).GetValue(host);
                Check(ReferenceEquals(tray.ContextMenuStrip, menu), "Production NotifyIcon uses the tested context menu");
                Button pinButton = (Button)panel.FindName("PinButton");
                Button closeButton = (Button)panel.FindName("CloseButton");
                Point anchor = TaskbarAnchor(Forms.Screen.PrimaryScreen);
                host.Start();
                host.Reveal();
                Pump(270);
                Window drawer = Window.GetWindow(panel);
                Check(drawer != null && drawer.IsVisible, "The isolated WPF drawer is running for host integration");
                closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, closeButton));
                Pump(240);

                ShowFromTaskbar(menu, anchor);
                Check(!drawer.IsVisible && menu.Items[0].Text == "打开记下", "Real collapsed drawer exposes Open");
                menu.Items[0].PerformClick();
                menu.Close();
                Pump(250);
                ShowFromTaskbar(menu, anchor);
                Check(drawer.IsVisible && menu.Items[0].Text == "收起记下", "Tray Open reveals the WPF drawer");
                menu.Items[1].PerformClick();
                menu.Close();
                ShowFromTaskbar(menu, anchor);
                Check(object.Equals(pinButton.Tag, true) && ((Forms.ToolStripMenuItem)menu.Items[1]).Checked,
                    "Tray Pin synchronizes the WPF pin button and WinForms check state");

                Capture(menu, Path.Combine(directory, "tray-preview.png"));
                menu.Items[1].PerformClick();
                menu.Close();
                ShowFromTaskbar(menu, anchor);
                Check(object.Equals(pinButton.Tag, false) && !((Forms.ToolStripMenuItem)menu.Items[1]).Checked,
                    "Tray Unpin synchronizes both controls");
                menu.Items[0].PerformClick();
                menu.Close();
                Pump(240);
                Check(!drawer.IsVisible, "Tray Collapse conceals the WPF drawer");
            }
        }

        private static void Capture(TrayMenu menu, string path)
        {
            using (Bitmap bitmap = new Bitmap(menu.Width, menu.Height, PixelFormat.Format32bppArgb))
            {
                int dpi = NativeDpi(menu);
                bitmap.SetResolution(dpi, dpi);
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                HashSet<int> colors = new HashSet<int>();
                for (int y = 0; y < bitmap.Height; y += 2)
                    for (int x = 0; x < bitmap.Width; x += 2)
                        colors.Add(bitmap.GetPixel(x, y).ToArgb());
                Check(colors.Count >= 12, "PNG records the visible WinForms menu instead of an empty surface");
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static void Show(TrayMenu menu, Point point)
        {
            menu.Show(point);
            VerifyOpened(menu);
        }

        private static void ShowFromTaskbar(TrayMenu menu, Point point)
        {
            MethodInfo show = typeof(Forms.ContextMenuStrip).GetMethod("ShowInTaskbar", PrivateInstance);
            if (show == null)
                throw new NotSupportedException("The WinForms build does not expose the NotifyIcon taskbar popup path.");
            show.Invoke(menu, new object[] { point.X, point.Y });
            VerifyOpened(menu);
        }

        private static void VerifyOpened(TrayMenu menu)
        {
            Pump(36);
            if (!menu.Visible)
                throw new InvalidOperationException("The menu did not open on the isolated desktop.");
        }

        private static Point TaskbarAnchor(Forms.Screen screen)
        {
            Rectangle monitor = screen.Bounds;
            Rectangle work = screen.WorkingArea;
            int right = monitor.Right - 3;
            int bottom = monitor.Bottom - 3;
            if (work.Left > monitor.Left)
                return new Point(monitor.Left + (work.Left - monitor.Left) / 2, bottom);
            if (work.Right < monitor.Right)
                return new Point(work.Right + (monitor.Right - work.Right) / 2, bottom);
            if (work.Top > monitor.Top)
                return new Point(right, monitor.Top + (work.Top - monitor.Top) / 2);
            return new Point(right, work.Bottom < monitor.Bottom ? work.Bottom + (monitor.Bottom - work.Bottom) / 2 : bottom);
        }

        private static Point Center(Rectangle rectangle)
        {
            return new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
        }

        private static int Pixels(int logical, int dpi)
        {
            return (int)Math.Round(logical * dpi / 96.0);
        }

        private static void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }

        private static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds)
            };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
