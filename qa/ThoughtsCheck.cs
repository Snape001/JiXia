using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SidePage
{
    internal static class ThoughtsCheck
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[3] == "--child")
                    return Run(args[0], args[2]);

                MethodInfo launcher = typeof(DockCheck).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
                return (int)launcher.Invoke(null, new object[] { args });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetBaseException());
                return 1;
            }
        }

        private static int Run(string xaml, string data)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Directory.CreateDirectory(data);
            try
            {
                CheckCaptureReviewAndPersistence(xaml, data);
                CheckFailedSave(xaml, Path.Combine(data, "failed"));
            }
            finally
            {
                app.Shutdown();
                Pump(50);
            }
            return 0;
        }

        private static void CheckCaptureReviewAndPersistence(string xaml, string data)
        {
            string primary = Path.Combine(data, "thoughts.xml");
            string tasksPath = Path.Combine(data, "tasks.xml");
            const string tasks = "<Tasks version=\"1\"><Task id=\"2f8ded789a1f4094bc85b54dc8f530fa\" urgent=\"true\">独立任务</Task></Tasks>";
            File.WriteAllText(tasksPath, tasks);
            const string first = "  第一条速记  ";
            const string second = "多行记录\r\n保留第二行 & <标记>";
            bool dragging = false;
            int finishedTasks = 0;
            FrameworkElement panel = LoadPanel(xaml);
            var journal = new ThoughtJournal(panel, data, () => dragging, () => finishedTasks++);
            int captures = 0;
            journal.Captured += delegate { captures++; };
            var window = new Window { Content = panel, Width = 392, Height = 650, ShowInTaskbar = false };
            window.Show();
            try
            {
                Pump(120);
                var entry = Find<TextBox>(panel, "ThoughtEntry");
                var submit = Find<Button>(panel, "ThoughtSubmit");
                var reviewToggle = Find<Button>(panel, "ThoughtToggle");
                var historyToggle = Find<Button>(panel, "ThoughtHistoryToggle");
                var pending = Find<StackPanel>(panel, "ThoughtItems");
                var history = Find<StackPanel>(panel, "ThoughtHistoryItems");
                var status = Find<TextBlock>(panel, "ThoughtStatus");

                Require(Count(panel) == "0" && !File.Exists(primary), "Starts empty with existing task storage only");
                entry.Text = " \t　";
                Click(submit);
                Require(captures == 0 && !File.Exists(primary), "Whitespace input creates no record");

                Focus(entry);
                entry.Text = first;
                Require(journal.IsInteracting, "A focused quick entry protects the drawer");
                Require(Press(entry, Key.Enter).Handled && entry.Text == "" && !entry.IsKeyboardFocusWithin &&
                    status.Text == "已记下" && captures == 1 && Count(panel) == "1",
                    "Enter records once, releases editor focus, updates count and shows feedback");

                entry.Text = second;
                Click(submit);
                Require(pending.Children.Count == 0 && history.Children.Count == 0 && Count(panel) == "2",
                    "Quick capture leaves closed list visuals unmaterialized");

                Focus(entry);
                entry.Text = "";
                var composition = new Candidate(entry);
                TextCompositionManager.StartComposition(composition);
                TextCompositionManager.UpdateComposition(composition);
                Require(!Press(entry, Key.Enter).Handled && !journal.HandleEscape() && captures == 2,
                    "Return and Escape are left to a pending IME composition");
                composition.Choose("中文候选");
                Require(entry.Text == "中文候选" && captures == 2, "IME completion inserts text without recording it");
                Press(entry, Key.Enter);
                Press(entry, Key.Enter);
                Require(captures == 3 && Count(panel) == "3", "A separate Return records the Chinese candidate once");
                Pump(1300);
                Require(!journal.IsInteracting && status.Text == "", "Receipt ends and releases automatic collapse protection");

                dragging = true;
                Click(reviewToggle);
                Require(!journal.IsReviewOpen && finishedTasks == 0, "Task dragging blocks a page change");
                dragging = false;
                Click(reviewToggle);
                Require(journal.IsReviewOpen && finishedTasks == 1 && !journal.IsInteracting &&
                    reviewToggle.Visibility == Visibility.Collapsed &&
                    Find<FrameworkElement>(panel, "TaskSections").Visibility == Visibility.Collapsed &&
                    Find<FrameworkElement>(panel, "ThoughtReview").Visibility == Visibility.Visible &&
                    Find<TextBlock>(panel, "ThoughtPendingCount").Text == "3" &&
                    historyToggle.Visibility == Visibility.Collapsed,
                    "Review finishes task editing, swaps pages and hides empty history without blocking collapse");
                Require(Text(pending, 0) == first.Trim() && Text(pending, 1) == second && Text(pending, 2) == "中文候选",
                    "Review preserves capture order, multiline text and XML-sensitive characters");

                Button completion = Action(pending, 0);
                Require(completion.Focusable && AutomationProperties.GetName(completion).Contains(first.Trim()),
                    "Completion action is keyboard focusable and named for the record");
                Click(completion);
                Require(Count(panel) == "2" && Text(pending, 0) == second && history.Children.Count == 0 &&
                    history.Visibility == Visibility.Collapsed && historyToggle.Visibility == Visibility.Visible,
                    "Completion moves a record into lazy, collapsed history");
                Click(historyToggle);
                Require(Text(history, 0) == first.Trim(), "Expanded history shows the completed record");
                Click(Action(history, 0));
                Require(Count(panel) == "3" && Text(pending, 0) == first.Trim() &&
                    history.Visibility == Visibility.Collapsed && historyToggle.Visibility == Visibility.Collapsed,
                    "Restore returns the record to its original position and hides empty history");
                Click(Action(pending, 1));
                Click(Find<Button>(panel, "ThoughtBack"));
                Click(reviewToggle);
                Require(journal.HandleEscape() && !journal.IsReviewOpen && reviewToggle.Visibility == Visibility.Visible &&
                    Find<TextBlock>(panel, "ThoughtToggleLabel").Text == "回顾",
                    "Rapid Back, re-open and Escape return to a stable task page");

                Focus(entry);
                entry.Text = "  关闭时保留草稿 📎  ";
                Require(journal.HandleEscape() && !entry.IsKeyboardFocusWithin && journal.HasUnstoredDraft &&
                    entry.Text == "  关闭时保留草稿 📎  " && Count(panel) == "2",
                    "Escape exits editing while preserving an editable draft");
                window.Hide();
                window.Show();
                Focus(entry);
                Require(entry.Text == "  关闭时保留草稿 📎  ", "Draft remains editable across collapse and reopen");
                journal.FinishEditing();
                Require(!journal.HasUnstoredDraft && captures == 4 && Count(panel) == "3" && File.ReadAllText(tasksPath) == tasks,
                    "Manual finish persists the draft without touching task storage");
            }
            finally { window.Close(); }

            FrameworkElement reloaded = LoadPanel(xaml);
            var reloadedJournal = new ThoughtJournal(reloaded, data, () => false, delegate { });
            Click(Find<Button>(reloaded, "ThoughtToggle"));
            Click(Find<Button>(reloaded, "ThoughtHistoryToggle"));
            Require(reloadedJournal.IsReviewOpen && Count(reloaded) == "3" &&
                Text(Find<StackPanel>(reloaded, "ThoughtItems"), 2) == "关闭时保留草稿 📎" &&
                Text(Find<StackPanel>(reloaded, "ThoughtHistoryItems"), 0) == second,
                "Recreating the journal restores Unicode text and completion state from disk");

            string backup = primary + ".bak";
            string expectedBackup = File.ReadAllText(backup);
            File.WriteAllText(primary, "<Thoughts malformed");
            FrameworkElement recovered = LoadPanel(xaml);
            new ThoughtJournal(recovered, data, () => false, delegate { });
            Require(Count(recovered) == "2" && File.ReadAllText(primary) == expectedBackup &&
                File.ReadAllText(backup) == expectedBackup, "Corrupt primary is repaired from the last good backup");
        }

        private static void CheckFailedSave(string xaml, string data)
        {
            string blocked = Path.Combine(data, "thoughts.xml");
            Directory.CreateDirectory(blocked);
            FrameworkElement panel = LoadPanel(xaml);
            var journal = new ThoughtJournal(panel, data, () => false, delegate { });
            int captures = 0;
            journal.Captured += delegate { captures++; };
            var entry = Find<TextBox>(panel, "ThoughtEntry");
            var status = Find<TextBlock>(panel, "ThoughtStatus");
            entry.Text = "未写入时不能丢";
            journal.FinishEditing();
            Require(captures == 0 && Count(panel) == "0" && journal.HasUnstoredDraft &&
                entry.Text == "未写入时不能丢" && status.Text == "保存失败",
                "A write failure reports the error and keeps the uncommitted draft");
            Directory.Delete(blocked);
            Click(Find<Button>(panel, "ThoughtSubmit"));
            Require(captures == 1 && Count(panel) == "1" && !journal.HasUnstoredDraft && File.Exists(blocked),
                "Retry commits after the storage obstruction is removed");
        }

        private static FrameworkElement LoadPanel(string path)
        {
            using (var file = File.OpenRead(path)) return (FrameworkElement)XamlReader.Load(file);
        }

        private static T Find<T>(FrameworkElement panel, string name) where T : FrameworkElement
        {
            return (T)panel.FindName(name);
        }

        private static string Count(FrameworkElement panel) { return Find<TextBlock>(panel, "ThoughtCount").Text; }

        private static Grid Row(StackPanel list, int index) { return (Grid)((Border)list.Children[index]).Child; }

        private static string Text(StackPanel list, int index) { return ((TextBlock)Row(list, index).Children[0]).Text; }

        private static Button Action(StackPanel list, int index) { return (Button)Row(list, index).Children[1]; }

        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }

        private static void Focus(TextBox entry)
        {
            entry.Focus();
            if (!entry.IsKeyboardFocusWithin)
                throw new InvalidOperationException("Isolated WPF window did not accept editor focus.");
        }

        private static KeyEventArgs Press(TextBox editor, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            editor.RaiseEvent(args);
            return args;
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

        private sealed class Candidate : TextComposition
        {
            public Candidate(IInputElement source)
                : base(InputManager.Current, source, "", TextCompositionAutoComplete.Off)
            {
                CompositionText = "zhongwen";
            }

            public void Choose(string result)
            {
                CompositionText = "";
                Text = result;
                Complete();
            }
        }
    }
}
