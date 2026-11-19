using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Xml;

namespace SidePage
{
    internal static partial class DragCheck
    {
        private const int LeftButton = 1;
        private const string Urgent = "Urgent";
        private const string Later = "Later";
        private static FrameworkElement panel;
        private static AdornerDecorator visualRoot;
        private static TaskBoard board;
        private static HwndSource presentation;
        private static string storageFile;
        private static bool enteredLoop;
        private static Func<OleTarget, DragDropEffects> runLoop;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string storageDirectory = args[1];
                Directory.CreateDirectory(storageDirectory);
                storageFile = Path.Combine(storageDirectory, "tasks.xml");
                if (args.Length < 3) Seed();
                using (Stream xaml = File.OpenRead(args[0]))
                    panel = (FrameworkElement)XamlReader.Load(xaml);

                // The HWND gives WPF its normal input hit-testing and screen coordinate transforms.
                // WS_VISIBLE is deliberately absent. The harness never moves the cursor or injects keys.
                var parameters = new HwndSourceParameters("SidePage drag QA")
                {
                    Width = 372,
                    Height = 610,
                    WindowStyle = 0x04000000
                };
                using (presentation = new HwndSource(parameters))
                {
                    visualRoot = new AdornerDecorator { Child = panel };
                    presentation.RootVisual = visualRoot;
                    board = new TaskBoard(panel, storageDirectory);
                    Layout();
                    if (args.Length >= 3)
                    {
                        MakePreview(storageDirectory, args[2]);
                        return 0;
                    }
                    CheckOrder("U1 U2 U3", "L1 L2 L3");
                    CheckFooter("RestFooter");
                    DragBetweenSections();
                    ReorderEarlier();
                    ReorderLater();
                    CancelOverTrash();
                    DropOnHeader();
                    DropInTrashAndUndo();
                }
                Console.WriteLine("PASS all drag checks");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void DragBetweenSections()
        {
            Drag(Urgent, "U2", route =>
            {
                Point point = CardPoint(Later, "L2", 0.25);
                route.Over(point, DragDropEffects.Move);
                CheckGap(Later, "L1 [gap] L2 L3");
                return route.Drop(point);
            });
            CheckOrder("U1 U3", "L1 U2 L2 L3");
            Console.WriteLine("PASS cross-section drop at the requested slot, including persistence");
        }

        private static void ReorderEarlier()
        {
            Drag(Later, "L3", route =>
            {
                Point point = CardPoint(Later, "U2", 0.25);
                route.Over(point, DragDropEffects.Move);
                CheckGap(Later, "L1 [gap] U2 L2");
                return route.Drop(point);
            });
            CheckOrder("U1 U3", "L1 L3 U2 L2");
            Console.WriteLine("PASS earlier same-section insertion, including persistence");
        }

        private static void ReorderLater()
        {
            Drag(Later, "L1", route =>
            {
                Point point = CardPoint(Later, "U2", 0.75);
                route.Over(point, DragDropEffects.Move);
                CheckGap(Later, "L3 U2 [gap] L2");
                return route.Drop(point);
            });
            CheckOrder("U1 U3", "L3 U2 L1 L2");
            Console.WriteLine("PASS later same-section insertion, including persistence");
        }

        private static void CancelOverTrash()
        {
            byte[] before = File.ReadAllBytes(storageFile);
            string restingLabel = Find<TextBlock>("TrashLabel").Text;
            Drag(Later, "U2", route =>
            {
                route.Over(TrashPoint(), DragDropEffects.Move);
                Assert(Find<TextBlock>("TrashLabel").Text != restingLabel, "Trash must show drag-over feedback.");
                Assert(!Cards(Later).Any(IsGap), "Trash drag-over must clear the insertion preview.");
                return DragDropEffects.None;
            });
            CheckOrder("U1 U3", "L3 U2 L1 L2");
            Assert(File.ReadAllBytes(storageFile).SequenceEqual(before), "Cancelling drag changed persisted tasks.");
            Assert(Find<TextBlock>("TrashLabel").Text == restingLabel, "Cancelling drag left the trash hot.");
            CheckFooter("RestFooter");
            Console.WriteLine("PASS cancelling a drag over the trash preserves tasks and clears feedback");
        }

        private static void DropOnHeader()
        {
            byte[] before = File.ReadAllBytes(storageFile);
            Drag(Urgent, "U3", route =>
            {
                route.Over(CardPoint(Later, "L3", 0.25), DragDropEffects.Move);
                route.Over(HeaderPoint(), DragDropEffects.None);
                return route.Drop(HeaderPoint(), DragDropEffects.None);
            });
            CheckOrder("U1 U3", "L3 U2 L1 L2");
            Assert(File.ReadAllBytes(storageFile).SequenceEqual(before), "An invalid drop changed persisted tasks.");
            CheckFooter("RestFooter");
            Console.WriteLine("PASS dropping over the panel header is rejected");
        }

        private static void DropInTrashAndUndo()
        {
            string originalId = StoredId("U2");
            FrameworkElement originalCard = Card(Later, "U2");
            Drag(Later, "U2", route =>
            {
                Point point = TrashPoint();
                route.Over(point, DragDropEffects.Move);
                Assert(StoredId("U2") == originalId, "Trash hover removed the task before release.");
                return route.Drop(point);
            });
            CheckOrder("U1 U3", "L3 L1 L2");
            CheckFooter("UndoBar");
            Assert(StoredId("U2") == null, "Released task is still present in storage.");
            Console.WriteLine("PASS trash release removes the task and offers undo");

            Button undo = Find<Button>("UndoButton");
            undo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, undo));
            CheckOrder("U1 U3", "L3 U2 L1 L2");
            CheckFooter("RestFooter");
            Assert(StoredId("U2") == originalId, "Undo did not restore the persisted task identity.");
            Assert(Card(Later, "U2") == originalCard, "Undo did not restore the original task card.");
            Console.WriteLine("PASS undo restores the original identity, section, slot, and footer");
        }

        private static void Drag(string section, string title, Func<OleTarget, DragDropEffects> loop)
        {
            FrameworkElement source = Card(section, title);
            MethodInfo start = typeof(TaskBoard).GetMethod("StartDrag", BindingFlags.Instance | BindingFlags.NonPublic);
            enteredLoop = false;
            runLoop = loop;
            Point pressed = source.TranslatePoint(new Point(source.ActualWidth / 2, source.ActualHeight / 2), panel);
            typeof(TaskBoard).GetField("pressedAt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(board, pressed);
            start.Invoke(board, new object[] { source, new Point(pressed.X + 5, pressed.Y) });
            Assert(enteredLoop, "The controller did not enter the drag loop.");
            if (board.IsDraggingOrSettling)
            {
                Assert(StoredId(title) == null, "The discarded task must already be saved as removed during its animation.");
                if (previewFrames != null) Record(620, null);
                else Pump(750);
            }
            Assert(!board.IsInteracting, "The controller is still in drag mode after finishing.");
            Assert(source.Visibility == Visibility.Visible, "Card remained hidden after finishing.");
            Assert(!Cards(Urgent).Concat(Cards(Later)).Any(IsGap), "An insertion gap remains after finishing.");
            Assert(AdornerLayer.GetAdornerLayer(panel).GetAdorners(panel) == null, "The drag ghost remained after finishing.");
            runLoop = null;
            Layout();
        }

        // The test-only build redirects the one OS drag-loop call here. WPF's OLE drop target
        // processes synthetic screen coordinates and produces the real preview routed events.
        internal static DragDropEffects RunOleLoop(DependencyObject source, DataObject data, DragDropEffects allowed)
        {
            Assert(source == panel && allowed == DragDropEffects.Move, "Unexpected drag source or allowed effects.");
            Assert(board.IsInteracting, "The controller has not started dragging.");
            CheckFooter("TrashTarget");
            enteredLoop = true;
            Layout();
            Adorner[] adorners = AdornerLayer.GetAdornerLayer(panel).GetAdorners(panel);
            Assert(adorners != null && adorners.Length == 1 && !adorners[0].IsHitTestVisible,
                "The drag ghost is missing or intercepts drop input.");
            var route = new OleTarget(data, allowed);
            route.Enter(HeaderPoint());
            return runLoop(route);
        }

        private sealed class OleTarget
        {
            private readonly Type type = typeof(DragDrop).Assembly.GetType("System.Windows.OleDropTarget", true);
            private readonly object target;
            private readonly DataObject data;
            private readonly int allowed;

            public OleTarget(DataObject data, DragDropEffects allowed)
            {
                this.data = data;
                this.allowed = (int)allowed;
                target = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new object[] { presentation.Handle }, null);
            }

            public void Enter(Point point)
            {
                Call("OleDragEnter", new object[] { data, LeftButton, PackPoint(point), allowed }, DragDropEffects.None);
            }

            public void Over(Point point, DragDropEffects expected)
            {
                Call("OleDragOver", new object[] { LeftButton, PackPoint(point), allowed }, expected);
            }

            public DragDropEffects Drop(Point point, DragDropEffects expected = DragDropEffects.Move)
            {
                return Call("OleDrop", new object[] { data, 0, PackPoint(point), allowed }, expected);
            }

            private DragDropEffects Call(string name, object[] args, DragDropEffects expected)
            {
                MethodInfo method = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single(candidate => candidate.Name.EndsWith("." + name, StringComparison.Ordinal));
                int result = (int)method.Invoke(target, args);
                Assert(result == 0, name + " returned HRESULT " + result + ".");
                var effect = (DragDropEffects)(int)args[args.Length - 1];
                Assert(effect == expected, name + " returned " + effect + " instead of " + expected + ".");
                return effect;
            }

            private static long PackPoint(Point point)
            {
                return ((long)(int)Math.Round(point.Y) << 32) | (uint)(int)Math.Round(point.X);
            }
        }

        private static Point CardPoint(string section, string title, double fraction)
        {
            FrameworkElement item = Card(section, title);
            StackPanel items = Find<StackPanel>(section + "Items");
            Rect slot = LayoutInformation.GetLayoutSlot(item);
            return items.PointToScreen(new Point(32, slot.Top + item.Margin.Top + item.ActualHeight * fraction));
        }

        private static Point HeaderPoint()
        {
            return panel.PointToScreen(new Point(150, 35));
        }

        private static Point TrashPoint()
        {
            FrameworkElement trash = Find<FrameworkElement>("TrashTarget");
            return trash.PointToScreen(new Point(trash.ActualWidth / 2, trash.ActualHeight / 2));
        }

        private static void Layout()
        {
            visualRoot.Measure(new Size(372, 610));
            visualRoot.Arrange(new Rect(0, 0, 372, 610));
            visualRoot.UpdateLayout();
        }

        private static void CheckGap(string section, string expected)
        {
            string actual = string.Join(" ", Cards(section).Where(item => item.Visibility == Visibility.Visible)
                .Select(item => IsGap(item) ? "[gap]" : AutomationProperties.GetName(item)));
            Assert(actual == expected, section + " preview order was " + actual + "; expected " + expected + ".");
        }

        private static void CheckOrder(string urgent, string later)
        {
            XmlDocument saved = ReadTasks();
            CheckSection(Urgent, urgent, saved, true);
            CheckSection(Later, later, saved, false);
        }

        private static void CheckSection(string section, string expected, XmlDocument saved, bool urgent)
        {
            FrameworkElement[] cards = Cards(section).ToArray();
            string actual = string.Join(" ", cards.Select(AutomationProperties.GetName));
            Assert(actual == expected, section + " visual order was " + actual + "; expected " + expected + ".");
            string persisted = string.Join(" ", saved.SelectNodes("/Tasks/Task").Cast<XmlElement>()
                .Where(element => XmlConvert.ToBoolean(element.GetAttribute("urgent")) == urgent)
                .Select(element => element.InnerText));
            Assert(persisted == expected, section + " saved order was " + persisted + "; expected " + expected + ".");
            Assert(Find<TextBlock>(section + "Count").Text == cards.Length.ToString(), section + " count is stale.");
        }

        private static void CheckFooter(string current)
        {
            foreach (string name in new[] { "RestFooter", "TrashTarget", "UndoBar" })
            {
                bool shown = name == current || (current == "UndoBar" && name == "RestFooter");
                Visibility expected = shown ? Visibility.Visible : Visibility.Collapsed;
                Assert(Find<FrameworkElement>(name).Visibility == expected, "Incorrect visibility for " + name + ".");
            }
        }

        private static bool IsGap(FrameworkElement item)
        {
            return item.Visibility == Visibility.Visible && !item.IsHitTestVisible &&
                string.IsNullOrEmpty(AutomationProperties.GetName(item));
        }

        private static IEnumerable<FrameworkElement> Cards(string section)
        {
            return Find<StackPanel>(section + "Items").Children.Cast<FrameworkElement>();
        }

        private static FrameworkElement Card(string section, string title)
        {
            return Cards(section).Single(item => AutomationProperties.GetName(item) == title);
        }

        private static T Find<T>(string name) where T : FrameworkElement
        {
            return (T)panel.FindName(name);
        }

        private static XmlDocument ReadTasks()
        {
            var document = new XmlDocument { XmlResolver = null };
            document.Load(storageFile);
            return document;
        }

        private static string StoredId(string title)
        {
            var task = (XmlElement)ReadTasks().SelectSingleNode("/Tasks/Task[text()='" + title + "']");
            return task == null ? null : task.GetAttribute("id");
        }

        private static void Seed()
        {
            using (XmlWriter xml = XmlWriter.Create(storageFile))
            {
                xml.WriteStartElement("Tasks");
                xml.WriteAttributeString("version", "1");
                foreach (string title in new[] { "U1", "U2", "U3", "L1", "L2", "L3" })
                {
                    xml.WriteStartElement("Task");
                    xml.WriteAttributeString("id", Guid.NewGuid().ToString("N"));
                    xml.WriteAttributeString("urgent", XmlConvert.ToString(title[0] == 'U'));
                    xml.WriteString(title);
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
