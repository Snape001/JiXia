using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SidePage
{
    internal static class DockCheck
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")]
        private static extern bool CloseDesktop(IntPtr desktop);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, W, H, Cols, Rows, Fill, Flags;
            public short Show, ReservedCount;
            public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string path, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInfo info);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out int exitCode);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 4 && args[3] == "--child")
            {
                try { return CheckWindows(args[0], args[1], args[2]); }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }

            // Place a new STA process on this desktop before WPF creates its first HWND.
            string name = "SidePageCheck-" + Guid.NewGuid().ToString("N");
            IntPtr desktop = CreateDesktop(name,
                IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
            if (desktop == IntPtr.Zero)
            {
                Console.Error.WriteLine("Cannot create isolated GUI desktop, Win32 " + Marshal.GetLastWin32Error());
                return 1;
            }
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                var command = new StringBuilder("\"" + path + "\"");
                foreach (string arg in args) command.Append(" \"").Append(Path.GetFullPath(arg)).Append('"');
                command.Append(" --child");
                var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = name,
                    Flags = 0x100, Input = GetStdHandle(-10), Output = GetStdHandle(-11), Error = GetStdHandle(-12) };
                ProcessInfo child;
                if (!CreateProcess(path, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                    IntPtr.Zero, null, ref startup, out child))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (WaitForSingleObject(child.Process, 30000) != 0)
                    {
                        TerminateProcess(child.Process, 2);
                        throw new TimeoutException("The isolated test did not finish.");
                    }
                    int status;
                    return GetExitCodeProcess(child.Process, out status) ? status : 2;
                }
                finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
            }
            finally { CloseDesktop(desktop); }
        }

        private static int CheckWindows(string xaml, string iconPath, string dataRoot)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string data = Path.Combine(dataRoot, "dock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            FrameworkElement panel;
            using (var file = File.OpenRead(xaml)) panel = (FrameworkElement)XamlReader.Load(file);
            var board = new TaskBoard(panel, data);
            using (var icon = new Icon(iconPath))
            {
                var host = new DockHost(panel, board, icon, data);
                host.Start();
                Pump(300);
                Window window = Window.GetWindow(panel);
                Check(window != null && window.IsVisible && window.Topmost && !window.ShowInTaskbar,
                    "First launch shows topmost native panel");
                Check(panel.ActualWidth > 200 && panel.ActualWidth <= 380 && panel.ActualHeight > 200,
                    "Native panel adopts desktop work area");
                Button pin = (Button)panel.FindName("PinButton");
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, pin));
                Pump(850);
                Check(window.IsVisible && object.Equals(pin.Tag, true), "Pin keeps panel open after pointer leaves");
                Button close = (Button)panel.FindName("CloseButton");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
                Pump(350);
                Check(!window.IsVisible, "Collapse hides the full-size native window");
                Window handle = null;
                foreach (Window candidate in app.Windows)
                    if (candidate.IsVisible && candidate.ActualWidth <= 70 && candidate.Topmost) handle = candidate;
                Check(handle != null && handle.ActualHeight <= 110, "Only a narrow native edge handle remains");
                host.Reveal();
                Pump(250);
                Check(window.IsVisible, "Reveal restores the panel after collapse");
                ((Button)panel.FindName("UrgentAdd")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)panel.FindName("UrgentEntry")).Text = "isolated native integration check";
                Pump(1200);
                Check(window.IsVisible && board.IsInteracting, "An active editor blocks automatic collapse");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
                Pump(600);
                Check(!window.IsVisible && board.UrgentTotal == 1, "Manual collapse commits a pending task");
                byte[] originalTasks = File.ReadAllBytes(Path.Combine(data, "tasks.xml"));
                host.Reveal();
                Pump(260);
                var quickNote = (TextBox)panel.FindName("ThoughtEntry");
                var submitNote = (Button)panel.FindName("ThoughtSubmit");
                quickNote.Focus();
                quickNote.Text = "Check the museum opening hours";
                submitNote.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submitNote));
                Pump(950);
                Check(!window.IsVisible && ((TextBlock)panel.FindName("ThoughtCount")).Text == "1",
                    "Quick capture saves and dismisses the unpinned drawer");
                host.Reveal();
                Pump(250);
                pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, pin));
                quickNote.Text = "Find the album from Saturday";
                submitNote.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submitNote));
                Pump(850);
                Check(window.IsVisible && ((TextBlock)panel.FindName("ThoughtCount")).Text == "2",
                    "Pinned capture stays open");
                var review = (Button)panel.FindName("ThoughtToggle");
                review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, review));
                Pump(330);
                Check(((FrameworkElement)panel.FindName("ThoughtReview")).IsVisible &&
                    !((FrameworkElement)panel.FindName("TaskSections")).IsVisible,
                    "Review uses the content area without compressing both task groups");
                Check(board.UrgentTotal == 1 && Convert.ToBase64String(originalTasks) ==
                    Convert.ToBase64String(File.ReadAllBytes(Path.Combine(data, "tasks.xml"))),
                    "Independent journal leaves existing tasks byte-for-byte intact");
                app.Shutdown();
                Pump(60);
            }
            return 0;
        }

        private static void Check(bool ok, string title)
        {
            if (!ok) throw new InvalidOperationException(title);
            Console.WriteLine("PASS " + title);
        }

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            if (!Dispatcher.CurrentDispatcher.HasShutdownStarted) Dispatcher.PushFrame(frame);
        }
    }
}
