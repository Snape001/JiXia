using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

[assembly: AssemblyTitle("记下")]
[assembly: AssemblyDescription("屏幕右侧的任务清单")]
[assembly: AssemblyProduct("记下")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace SidePage
{
    internal static class Program
    {
        private const string InstanceName = @"Local\SidePage.43cf9a03.Instance";
        private const string WakeName = @"Local\SidePage.43cf9a03.Reveal";

        [STAThread]
        private static void Main()
        {
            try
            {
                using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName))
                using (var instance = new Mutex(false, InstanceName))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        wake.Set();
                        return;
                    }
                    try { Run(wake); }
                    finally { instance.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("记下未能正常运行。\n\n" + ex.GetBaseException().Message,
                    "记下", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void Run(EventWaitHandle wake)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Assembly assembly = Assembly.GetExecutingAssembly();
            FrameworkElement panel;
            using (Stream source = assembly.GetManifestResourceStream("SidePage.Panel.xaml"))
                panel = (FrameworkElement)XamlReader.Load(source);

            var board = new TaskBoard(panel);
            using (Stream source = assembly.GetManifestResourceStream("SidePage.ico"))
            using (var icon = new Icon(source))
            {
                var dock = new DockHost(panel, board, icon);
                dock.Start();
                var signal = ThreadPool.RegisterWaitForSingleObject(wake, delegate(object state, bool expired)
                {
                    if (!app.Dispatcher.HasShutdownStarted)
                        app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(dock.Reveal));
                }, null, Timeout.Infinite, false);
                try { app.Run(); }
                finally { signal.Unregister(null); }
            }
        }
    }
}
