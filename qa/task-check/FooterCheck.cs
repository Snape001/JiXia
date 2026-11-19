using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SidePage
{
    internal static class FooterCheck
    {
        static FrameworkElement panel;
        static TaskBoard task;
        static ThoughtJournal journal;
        static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        static T Find<T>(string name) where T : FrameworkElement
        {
            return (T)panel.FindName(name);
        }

        static void Layout(int width)
        {
            int height = width == 320 ? 430 : 610;
            panel.Measure(new Size(width, height));
            panel.Arrange(new Rect(0, 0, width, height));
            panel.UpdateLayout();
        }

        static void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }

        static bool Clickable(Button button)
        {
            Point point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), panel);
            HitTestResult hit = VisualTreeHelper.HitTest(panel, point);
            DependencyObject hitVisual = hit == null ? null : hit.VisualHit;
            while (hitVisual != null && hitVisual != button)
                hitVisual = VisualTreeHelper.GetParent(hitVisual);
            return hitVisual == button;
        }

        static Button FindPendingAction(DependencyObject item)
        {
            Button button = item as Button;
            if (button != null && AutomationProperties.GetName(button).StartsWith("移入已处理", StringComparison.Ordinal))
                return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            {
                Button found = FindPendingAction(VisualTreeHelper.GetChild(item, i));
                if (found != null) return found;
            }
            return null;
        }

        static void Snapshot(string path)
        {
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)(panel.ActualWidth * 2),
                (int)(panel.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) png.Save(stream);
        }

        static void Run(int width, string source, string folder)
        {
            using (FileStream stream = File.OpenRead(source)) panel = (FrameworkElement)XamlReader.Load(stream);
            string storage = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            task = new TaskBoard(panel, storage);
            journal = new ThoughtJournal(panel, storage, delegate { return task.IsDraggingOrSettling; }, task.FinishEditing);
            Layout(width);
            Grid footer = Find<Grid>("Footer");
            FrameworkElement capture = Find<FrameworkElement>("RestFooter");
            FrameworkElement undo = Find<FrameworkElement>("UndoBar");
            FrameworkElement trash = Find<FrameworkElement>("TrashTarget");
            TextBox entry = Find<TextBox>("ThoughtEntry");
            Button captureButton = Find<Button>("ThoughtSubmit");
            Button reviewButton = Find<Button>("ThoughtToggle");
            Button undoButton = Find<Button>("UndoButton");
            Check(footer.ColumnDefinitions[1].ActualWidth == 0, width + " non-undo footer uses its full width");

            Click(Find<Button>("UrgentAdd"));
            Find<TextBox>("UrgentEntry").Text = "待完成的任务";
            task.FinishEditing();
            Click(Find<Button>("UrgentAdd"));
            Find<TextBox>("UrgentEntry").Text = "重排任务";
            task.FinishEditing();
            var urgent = Find<StackPanel>("UrgentItems");
            FrameworkElement card = (FrameworkElement)urgent.Children[0];
            typeof(TaskBoard).GetMethod("Remove", Hidden).Invoke(task, new object[] { card, false });

            entry.Text = "先记一条分心念头";
            Layout(width);
            Click(captureButton);
            entry.Text = "还有另一个要记";
            Layout(width);
            Check(entry.ActualWidth >= 60 && entry.ActualHeight >= 18,
                width + " draft stays editable beside status and undo (" + entry.ActualWidth + "x" + entry.ActualHeight + ")");
            Check(capture.Visibility == Visibility.Visible && undo.Visibility == Visibility.Visible &&
                trash.Visibility == Visibility.Collapsed && Math.Abs(undo.ActualWidth - 80) <= 1 &&
                Math.Abs(undo.TranslatePoint(new Point(), footer).X - capture.ActualWidth - 4) <= 1,
                width + " capture and compact undo are visible side by side");
            Check(Clickable(captureButton) && Clickable(reviewButton) && Clickable(undoButton) &&
                Find<TextBlock>("UndoLabel").ActualWidth >= 30, width + " capture, review, and undo have separate hit targets");
            Snapshot(Path.Combine(folder, "footer-" + width + ".png"));

            Click(reviewButton);
            Layout(width);
            Button pendingAction = FindPendingAction(Find<StackPanel>("ThoughtItems"));
            Check(journal.IsReviewOpen && pendingAction != null && Clickable(pendingAction) && Clickable(undoButton),
                width + " pending action and undo remain clickable in review");
            Click(pendingAction);
            Check(Find<TextBlock>("ThoughtPendingCount").Text == "0", width + " pending action works");
            Click(Find<Button>("ThoughtBack"));
            Layout(width);
            Check(!journal.IsReviewOpen && entry.Text == "还有另一个要记", width + " return to capture retains the draft");

            var dragged = (FrameworkElement)urgent.Children[0];
            typeof(TaskBoard).GetField("draggedCard", Hidden).SetValue(task, dragged);
            typeof(TaskBoard).GetMethod("Refresh", Hidden).Invoke(task, null);
            Layout(width);
            Check(capture.Visibility == Visibility.Collapsed && undo.Visibility == Visibility.Collapsed &&
                trash.Visibility == Visibility.Visible && Math.Abs(footer.ActualWidth - trash.ActualWidth) <= 1,
                width + " bin replaces both columns through the drag");

            typeof(TaskBoard).GetField("draggedCard", Hidden).SetValue(task, null);
            typeof(TaskBoard).GetMethod("Refresh", Hidden).Invoke(task, null);
            Layout(width);
            Click(undoButton);
            Layout(width);
            Check(task.UrgentTotal == 2 && footer.ColumnDefinitions[1].ActualWidth == 0 &&
                capture.ActualWidth == footer.ActualWidth, width + " undo restores the card and full-width footer");
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Run(320, args[0], args[1]);
                Run(372, args[0], args[1]);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetBaseException()); return 1; }
        }
    }
}