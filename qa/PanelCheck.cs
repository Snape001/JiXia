using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SidePage
{
    internal static class PanelCheck
    {
        private static FrameworkElement panel;
        private static TaskBoard board;
        private static string dataPath;
        private static string xamlPath;
        private static readonly List<string> pass = new List<string>();

        private static void Check(bool ok, string title)
        {
            if (!ok) throw new InvalidOperationException(title);
            pass.Add(title);
            Console.WriteLine("PASS " + title);
        }

        private static T Find<T>(string name) where T : FrameworkElement
        {
            T result = panel.FindName(name) as T;
            if (result == null) throw new InvalidOperationException("Missing " + name);
            return result;
        }

        private static void Click(string name)
        {
            ButtonBase button = Find<ButtonBase>(name);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private static void Add(string section, string value)
        {
            Click(section + "Add");
            TextBox editor = Find<TextBox>(section + "Entry");
            if (editor.Visibility != Visibility.Visible || !board.IsInteracting)
                throw new InvalidOperationException("The add button did not open the " + section + " editor");
            editor.Text = value;
            board.FinishEditing();
        }

        private static void LoadBoard()
        {
            using (Stream s = File.OpenRead(xamlPath)) panel = (FrameworkElement)XamlReader.Load(s);
            board = new TaskBoard(panel, dataPath);
        }

        private static void Layout(double width, double height)
        {
            panel.Measure(new Size(width, height));
            panel.Arrange(new Rect(0, 0, width, height));
            panel.UpdateLayout();
        }

        internal static void Render(FrameworkElement visual, string filePath)
        {
            const double scale = 1.5;
            var format = PixelFormats.Pbgra32;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale),
                (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, format);
            bitmap.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var f = File.Create(filePath)) png.Save(f);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                xamlPath = args[0];
                string results = args[1];
                dataPath = Path.Combine(results, "data-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataPath);
                LoadBoard();
                Layout(372, 610);
                Check(board.UrgentTotal == 0 && Find<FrameworkElement>("LaterEmpty").Visibility == Visibility.Visible,
                    "Fresh installation starts empty");
                Render(panel, Path.Combine(results, "empty.png"));
                int changes = 0;
                board.Changed += delegate { changes++; };
                Add("Urgent", "   ");
                Check(board.UrgentTotal == 0 && !board.IsInteracting, "Blank entries leave no task");
                Add("Urgent", "确认首页的交互稿");
                Add("Urgent", "把周五的评审材料发给小组");
                Add("Later", "整理这周收集的字体样本");
                Add("Later", "读完书架上的那本摄影集");
                Add("Later", "周末去花市挑一盆绿植");
                Check(changes > 0 && board.UrgentTotal == 2 && Find<TextBlock>("LaterCount").Text.Contains("3"),
                    "Normal add flow updates both groups and urgency badge");
                Check(Find<FrameworkElement>("UrgentEmpty").Visibility == Visibility.Collapsed,
                    "Populated groups hide their empty states");
                LoadBoard();
                Check(board.UrgentTotal == 2 && Find<TextBlock>("LaterCount").Text.Contains("3"),
                    "Independent board reload restores saved tasks");
                Layout(372, 610);
                Render(panel, Path.Combine(results, "preview.png"));
                Layout(320, 430);
                Check(Find<FrameworkElement>("Footer").TranslatePoint(new Point(0,0), panel).Y +
                    Find<FrameworkElement>("Footer").ActualHeight <= 431, "Footer fits compact workspace");
                Render(panel, Path.Combine(results, "compact.png"));
                Console.WriteLine("Passed " + pass.Count + " interaction checks");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
