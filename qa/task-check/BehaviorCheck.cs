using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SidePage
{
    internal static class BehaviorCheck
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static FrameworkElement panel;
        private static TaskBoard board;

        private static void Load(string xaml, string data)
        {
            using (FileStream stream = File.OpenRead(xaml))
                panel = (FrameworkElement)XamlReader.Load(stream);
            board = new TaskBoard(panel, data);
        }

        private static void Layout()
        {
            panel.Measure(new Size(372, 610));
            panel.Arrange(new Rect(0, 0, 372, 610));
            panel.UpdateLayout();
        }

        private static FrameworkElement Item(string area, int index)
        {
            return (FrameworkElement)((StackPanel)panel.FindName(area + "Items")).Children[index];
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, Instance).GetValue(target);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().GetField(name, Instance).SetValue(target, value);
        }

        private static object Call(string name, params object[] args)
        {
            return board.GetType().GetMethod(name, Instance).Invoke(board, args);
        }

        private static void Click(string name)
        {
            Button button = (Button)panel.FindName(name);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private static void Add(string area, string text)
        {
            Click(area + "Add");
            ((TextBox)panel.FindName(area + "Entry")).Text = text;
            board.FinishEditing();
        }

        private static void Check(bool ok, string description)
        {
            if (!ok) throw new InvalidOperationException(description);
            Console.WriteLine("PASS " + description);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string data = Path.Combine(args[1], "behavior-" + Guid.NewGuid().ToString("N"));
                Load(args[0], data);
                Add("Urgent", "A");
                Add("Urgent", "B");
                Add("Urgent", "C");
                Add("Later", "任务 🌿\r\n第二行 & <保留>\u001B");
                Layout();

                FrameworkElement a = Item("Urgent", 0);
                FrameworkElement b = Item("Urgent", 1);
                FrameworkElement c = Item("Urgent", 2);
                FrameworkElement laterCard = Item("Later", 0);
                object urgent = Field(board, "urgent");
                object later = Field(board, "later");
                Check(Field(urgent, "Scroller") is ScrollViewer && Field(later, "Scroller") is ScrollViewer,
                    "Both sections locate their scroll viewers");
                Check(AutomationProperties.GetName(laterCard) == "任务 🌿\r\n第二行 & <保留>\uFFFD",
                    "Multiline Unicode remains intact with invalid XML controls normalized");

                Set(board, "draggedCard", b);
                b.Visibility = Visibility.Collapsed;
                Border gap = (Border)Field(board, "gap");
                gap.Height = 38;
                Call("SetGap", urgent, 1, false);
                Layout();
                double gapY = LayoutInformation.GetLayoutSlot(gap).Top;
                double cY = LayoutInformation.GetLayoutSlot(c).Top;
                Check((int)Call("FindSlot", urgent, new Point(0, gapY + 1)) == 1 &&
                    (int)Call("FindSlot", urgent, new Point(0, cY + c.ActualHeight - 1)) == 2,
                    "Drop slots match the marker and the next card's lower half");
                Call("SetGap", urgent, 2, true);
                Check(Item("Urgent", 3) == gap, "Gap reorders correctly past the hidden source card");
                Call("SetGap", null, -1, false);
                Set(board, "draggedCard", null);
                b.Visibility = Visibility.Visible;

                Call("Move", b, urgent, 2, false);
                Check(Item("Urgent", 0) == a && Item("Urgent", 1) == c && Item("Urgent", 2) == b,
                    "Moving down retains the intended insertion index");
                Call("Move", c, later, 0, false);
                Check(Item("Later", 0) == c && Item("Later", 1) == laterCard && board.UrgentTotal == 2,
                    "Moving across groups updates card order and counts");

                Call("Remove", b, false);
                Call("Remove", a, false);
                Check(((FrameworkElement)panel.FindName("UndoBar")).Visibility == Visibility.Visible && board.UrgentTotal == 0,
                    "Removal presents undo while both tasks are recoverable");
                Click("UndoButton");
                Click("UndoButton");
                Check(Item("Urgent", 0) == a && Item("Urgent", 1) == b && board.UrgentTotal == 2,
                    "Successive undo actions restore original positions");
                Call("Remove", b, false);
                DispatcherFrame frame = new DispatcherFrame();
                DispatcherTimer expiry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5350) };
                expiry.Tick += delegate { expiry.Stop(); frame.Continue = false; };
                expiry.Start();
                Dispatcher.PushFrame(frame);
                Click("UndoButton");
                Check(board.UrgentTotal == 1 && ((FrameworkElement)panel.FindName("UndoBar")).Visibility == Visibility.Collapsed,
                    "Undo expires after five seconds");

                string saved = Path.Combine(data, "tasks.xml");
                string backup = File.ReadAllText(saved + ".bak");
                File.WriteAllText(saved, "<Tasks");
                Load(args[0], data);
                Check(board.UrgentTotal == 2 && File.ReadAllText(saved) == backup &&
                    AutomationProperties.GetName(Item("Later", 1)) == "任务 🌿\r\n第二行 & <保留>\uFFFD",
                    "Malformed primary file is recovered from its valid backup");
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
