using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml;

namespace SidePage
{
    internal sealed class ThoughtJournal
    {
        private static readonly Brush RecordedFill = BrushAt(0x30, 0x2F, 0x2B);
        private static readonly Brush RecordedEdge = BrushAt(0x43, 0x44, 0x3D);
        private static readonly Brush ProcessedFill = BrushAt(0x2A, 0x26, 0x22);
        private static readonly Brush ProcessedEdge = BrushAt(0x3D, 0x35, 0x2E);
        private static readonly Brush MainText = BrushAt(0xF2, 0xE9, 0xDB);
        private static readonly Brush MutedText = BrushAt(0xB6, 0xA9, 0x9A);
        private static readonly Brush ActionInk = BrushAt(0xAD, 0xB9, 0xC0);
        private static readonly Geometry DoneMark = ReadIcon("M 2,7 L 5.3,10.2 L 11.8,3.7");
        private static readonly Geometry RestoreMark = ReadIcon("M 5.2,3 L 2,6.3 L 5.2,9.5 M 2,6.3 L 8.3,6.3 C 11.5,6.3 12.3,8.1 12.3,10.5");

        private readonly string storagePath;
        private readonly Func<bool> taskBusy;
        private readonly Action finishTaskEditing;
        private readonly TextBox entry;
        private readonly Button submit;
        private readonly object submitHint;
        private readonly Button toggle;
        private readonly TextBlock count;
        private readonly TextBlock status;
        private readonly FrameworkElement taskSections;
        private readonly FrameworkElement review;
        private readonly Button back;
        private readonly TextBlock pendingCount;
        private readonly StackPanel pendingItems;
        private readonly FrameworkElement empty;
        private readonly TextBlock reviewStatus;
        private readonly Button historyToggle;
        private readonly TextBlock historyLabel;
        private readonly StackPanel historyItems;
        private readonly Style actionStyle;
        private readonly DispatcherTimer receiptTimer;
        private readonly TranslateTransform tasksMotion = new TranslateTransform();
        private readonly TranslateTransform reviewMotion = new TranslateTransform();

        private List<Thought> records;
        private TextComposition activeComposition;
        private int completedCount;
        private bool reviewOpen;
        private bool historyOpen;
        private bool primaryValid;
        private bool capturing;

        public ThoughtJournal(FrameworkElement panel, string storageDirectory, Func<bool> taskBusy, Action finishTaskEditing)
        {
            storagePath = Path.Combine(storageDirectory, "thoughts.xml");
            this.taskBusy = taskBusy;
            this.finishTaskEditing = finishTaskEditing;
            entry = Named<TextBox>(panel, "ThoughtEntry");
            submit = Named<Button>(panel, "ThoughtSubmit");
            submitHint = submit.ToolTip;
            toggle = Named<Button>(panel, "ThoughtToggle");
            count = Named<TextBlock>(panel, "ThoughtCount");
            Named<TextBlock>(panel, "ThoughtToggleLabel").Text = "回顾";
            status = panel.FindName("ThoughtStatus") as TextBlock;
            taskSections = Named<FrameworkElement>(panel, "TaskSections");
            review = Named<FrameworkElement>(panel, "ThoughtReview");
            back = Named<Button>(panel, "ThoughtBack");
            pendingCount = Named<TextBlock>(panel, "ThoughtPendingCount");
            pendingItems = Named<StackPanel>(panel, "ThoughtItems");
            empty = Named<FrameworkElement>(panel, "ThoughtEmpty");
            reviewStatus = panel.FindName("ThoughtReviewStatus") as TextBlock;
            historyToggle = Named<Button>(panel, "ThoughtHistoryToggle");
            historyLabel = Named<TextBlock>(panel, "ThoughtHistoryLabel");
            historyItems = Named<StackPanel>(panel, "ThoughtHistoryItems");
            actionStyle = panel.TryFindResource("ActionButton") as Style;

            taskSections.RenderTransform = tasksMotion;
            review.RenderTransform = reviewMotion;
            review.Visibility = Visibility.Collapsed;
            historyItems.Visibility = Visibility.Collapsed;
            SetLiveStatus(status);
            SetLiveStatus(reviewStatus);

            receiptTimer = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1100)
            };
            receiptTimer.Tick += delegate
            {
                receiptTimer.Stop();
                SetStatus(status, "");
            };

            submit.Click += delegate { Capture(); };
            toggle.Click += delegate { SetReview(!reviewOpen); };
            back.Click += delegate { SetReview(false); };
            historyToggle.Click += delegate { SetHistory(!historyOpen); };
            entry.AcceptsReturn = false;
            entry.PreviewKeyDown += EntryKeyDown;
            entry.LostKeyboardFocus += delegate { activeComposition = null; };
            entry.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
                new TextCompositionEventHandler(CompositionChanged), true);
            entry.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
                new TextCompositionEventHandler(CompositionChanged), true);
            entry.AddHandler(TextCompositionManager.PreviewTextInputEvent,
                new TextCompositionEventHandler(CompositionEnded), true);

            Load();
            Refresh();
        }

        public event EventHandler Captured;

        public bool IsInteracting { get { return entry.IsKeyboardFocusWithin || receiptTimer.IsEnabled; } }

        public bool IsReviewOpen { get { return reviewOpen; } }

        public bool HasUnstoredDraft { get { return !string.IsNullOrWhiteSpace(entry.Text); } }

        public void FinishEditing()
        {
            Capture();
            ReleaseEditorFocus();
        }

        public bool HandleEscape()
        {
            if (entry.IsKeyboardFocusWithin)
            {
                if (activeComposition != null)
                    return false;

                ReleaseEditorFocus();
                return true;
            }

            if (!reviewOpen)
                return false;

            SetReview(false);
            return true;
        }

        private void EntryKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Enter || activeComposition != null || Keyboard.Modifiers != ModifierKeys.None)
                return;

            args.Handled = true;
            if (!args.IsRepeat)
                Capture();
        }

        private void CompositionChanged(object sender, TextCompositionEventArgs args)
        {
            TextComposition composition = args.TextComposition;
            if (composition.AutoComplete == TextCompositionAutoComplete.Off || composition.CompositionText.Length > 0)
                activeComposition = composition;
        }

        private void CompositionEnded(object sender, TextCompositionEventArgs args)
        {
            if (activeComposition == args.TextComposition)
                activeComposition = null;
        }

        private void Capture()
        {
            if (capturing)
                return;

            capturing = true;
            try
            {
                TextComposition composition = activeComposition;
                string composingDraft = null;
                if (composition != null)
                {
                    composingDraft = entry.Text;
                    composition.Complete();
                    ReleaseEditorFocus();
                }

                string value = Normalize(entry.Text);
                if (value.Length == 0 && composingDraft != null)
                {
                    value = Normalize(composingDraft);
                    if (value.Length == 0)
                        value = Normalize(composition.CompositionText);
                    entry.Text = value;
                }
                if (value.Length == 0)
                    return;

                List<Thought> next = new List<Thought>(records);
                next.Add(new Thought(Guid.NewGuid().ToString("N"), value, false));
                if (!TrySave(next))
                {
                    ShowWriteFailure();
                    return;
                }

                records = next;
                entry.Clear();
                ReleaseEditorFocus();
                Refresh();
                ClearWriteFailure();
                SetStatus(status, "已记下");
                receiptTimer.Stop();
                receiptTimer.Start();
                EventHandler handler = Captured;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
            finally { capturing = false; }
        }

        private void ReleaseEditorFocus()
        {
            DependencyObject focusScope = FocusManager.GetFocusScope(entry);
            if (focusScope != null && FocusManager.GetFocusedElement(focusScope) == entry)
                FocusManager.SetFocusedElement(focusScope, null);
            if (entry.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
        }

        private void SetReview(bool open)
        {
            if (reviewOpen == open)
                return;

            if (open && taskBusy())
                return;

            bool moveFocus = toggle.IsKeyboardFocusWithin || taskSections.IsKeyboardFocusWithin ||
                review.IsKeyboardFocusWithin || entry.IsKeyboardFocusWithin;
            if (open)
                finishTaskEditing();

            ReleaseEditorFocus();
            reviewOpen = open;
            if (open)
                Refresh();
            else
            {
                pendingItems.Children.Clear();
                SetHistory(false);
            }

            FrameworkElement shown = open ? review : taskSections;
            FrameworkElement hidden = open ? taskSections : review;
            TranslateTransform shownMotion = open ? reviewMotion : tasksMotion;
            TranslateTransform hiddenMotion = open ? tasksMotion : reviewMotion;
            hidden.BeginAnimation(UIElement.OpacityProperty, null);
            hiddenMotion.BeginAnimation(TranslateTransform.XProperty, null);
            hidden.Visibility = Visibility.Collapsed;
            shown.Visibility = Visibility.Visible;
            toggle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

            if (SystemParameters.ClientAreaAnimation)
            {
                shown.BeginAnimation(UIElement.OpacityProperty, Ease(0.65, 1, 125));
                shownMotion.BeginAnimation(TranslateTransform.XProperty, Ease(open ? 7 : -7, 0, 165));
            }

            if (moveFocus)
                (open ? back : toggle).Focus();
        }

        private void UpdateToggleName(string pending)
        {
            string description = "回顾速记，" + pending + " 条待处理";
            AutomationProperties.SetName(toggle, description);
            toggle.ToolTip = description;
        }

        private void Refresh()
        {
            completedCount = 0;
            foreach (Thought thought in records)
                if (thought.Completed)
                    completedCount++;

            int remaining = records.Count - completedCount;
            string pending = remaining.ToString(CultureInfo.CurrentCulture);
            count.Text = pending;
            pendingCount.Text = pending;
            empty.Visibility = remaining == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateToggleName(pending);
            if (reviewOpen)
                BuildRows(pendingItems, false);
            SetHistory(reviewOpen && historyOpen);
        }

        private void SetHistory(bool open)
        {
            historyOpen = open && completedCount > 0;
            historyToggle.Visibility = completedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            historyItems.Visibility = historyOpen ? Visibility.Visible : Visibility.Collapsed;
            historyToggle.IsEnabled = completedCount > 0;
            string total = completedCount.ToString(CultureInfo.CurrentCulture);
            historyLabel.Text = "已处理 " + total + (historyOpen ? " ▾" : " ▸");
            AutomationProperties.SetName(historyToggle, (historyOpen ? "收起" : "展开") + "已处理速记，" + total + " 条");
            if (historyOpen)
                BuildRows(historyItems, true);
            else
                historyItems.Children.Clear();
        }

        private void BuildRows(StackPanel destination, bool completed)
        {
            destination.Children.Clear();
            foreach (Thought thought in records)
                if (thought.Completed == completed)
                    destination.Children.Add(BuildRow(thought));
        }

        private Border BuildRow(Thought thought)
        {
            TextBlock body = new TextBlock
            {
                Text = thought.Text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = thought.Completed ? MutedText : MainText,
                Margin = new Thickness(0, 2, 8, 2)
            };

            string actionName = thought.Completed ? "恢复到待处理" : "移入已处理";
            Button action = new Button
            {
                Style = actionStyle,
                Foreground = thought.Completed ? MutedText : ActionInk,
                Width = 28,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = actionName
            };
            AutomationProperties.SetName(action, actionName + "：" + (thought.Text.Length > 42 ? ShortName(thought.Text) : thought.Text));
            System.Windows.Shapes.Path icon = new System.Windows.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = thought.Completed ? RestoreMark : DoneMark,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = action });
            action.Content = icon;
            action.Click += delegate { SetCompleted(thought, action.IsKeyboardFocused); };

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(action, 1);
            row.Children.Add(body);
            row.Children.Add(action);
            return new Border
            {
                Background = thought.Completed ? ProcessedFill : RecordedFill,
                BorderBrush = thought.Completed ? ProcessedEdge : RecordedEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 5, 5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            };
        }

        private void SetCompleted(Thought thought, bool restoreFocus)
        {
            int index = records.IndexOf(thought);
            if (index < 0)
                return;

            List<Thought> next = new List<Thought>(records);
            next[index] = new Thought(thought.Id, thought.Text, !thought.Completed);
            if (!TrySave(next))
            {
                ShowWriteFailure();
                return;
            }

            records = next;
            ClearWriteFailure();
            Refresh();
            if (!restoreFocus)
                return;

            StackPanel source = thought.Completed ? historyItems : pendingItems;
            int previous = 0;
            for (int i = 0; i < index; i++)
                if (records[i].Completed == thought.Completed)
                    previous++;

            if (source.Children.Count > 0)
            {
                Border row = (Border)source.Children[Math.Min(previous, source.Children.Count - 1)];
                ((Button)((Grid)row.Child).Children[1]).Focus();
            }
            else
            {
                (historyToggle.IsEnabled ? historyToggle : back).Focus();
            }
        }

        private void Load()
        {
            records = Read(storagePath);
            primaryValid = records != null;
            if (primaryValid)
                return;

            records = Read(storagePath + ".bak");
            if (records == null)
            {
                records = new List<Thought>();
                return;
            }

            if (!TrySave(records))
                ShowWriteFailure();
        }

        private static List<Thought> Read(string path)
        {
            try
            {
                XmlDocument document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader reader = XmlReader.Create(stream, settings))
                    document.Load(reader);

                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "Thoughts" || root.GetAttribute("version") != "1")
                    return null;

                var result = new List<Thought>();
                var ids = new HashSet<Guid>();
                foreach (XmlNode child in root.ChildNodes)
                {
                    XmlElement item = child as XmlElement;
                    if (item == null)
                        continue;

                    Guid id;
                    bool completed;
                    string value = item.InnerText;
                    if (item.Name != "Thought" || !Guid.TryParseExact(item.GetAttribute("id"), "N", out id) ||
                        !bool.TryParse(item.GetAttribute("completed"), out completed) || !ids.Add(id) || string.IsNullOrWhiteSpace(value))
                        return null;

                    result.Add(new Thought(id.ToString("N"), value, completed));
                }
                return result;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (XmlException) { return null; }
        }

        private bool TrySave(List<Thought> next)
        {
            string temporary = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storagePath));
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    XmlWriterSettings settings = new XmlWriterSettings { Indent = true, NewLineHandling = NewLineHandling.Entitize };
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        writer.WriteStartElement("Thoughts");
                        writer.WriteAttributeString("version", "1");
                        foreach (Thought thought in next)
                        {
                            writer.WriteStartElement("Thought");
                            writer.WriteAttributeString("id", thought.Id);
                            writer.WriteAttributeString("completed", XmlConvert.ToString(thought.Completed));
                            writer.WriteString(thought.Text);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    stream.Flush(true);
                }

                if (File.Exists(storagePath))
                    File.Replace(temporary, storagePath, primaryValid ? storagePath + ".bak" : null, true);
                else
                    File.Move(temporary, storagePath);

                primaryValid = true;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void ShowWriteFailure()
        {
            receiptTimer.Stop();
            SetStatus(status, "保存失败");
            SetStatus(reviewStatus, "保存失败，请重试。");
            submit.ToolTip = "保存失败，请重试。";
        }

        private void ClearWriteFailure()
        {
            if (!receiptTimer.IsEnabled)
                SetStatus(status, "");
            SetStatus(reviewStatus, "");
            submit.ToolTip = submitHint;
        }

        private static void SetLiveStatus(TextBlock target)
        {
            if (target != null)
                AutomationProperties.SetLiveSetting(target, AutomationLiveSetting.Polite);
        }

        private static void SetStatus(TextBlock target, string message)
        {
            if (target == null || target.Text == message)
                return;

            target.Text = message;
            if (message.Length > 0 && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                AutomationPeer peer = UIElementAutomationPeer.FromElement(target) ?? UIElementAutomationPeer.CreatePeerForElement(target);
                if (peer != null)
                    peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }

        private static string Normalize(string text)
        {
            string trimmed = text.Trim();
            try { return XmlConvert.VerifyXmlChars(trimmed); }
            catch (XmlException)
            {
                var clean = new StringBuilder(trimmed.Length);
                for (int i = 0; i < trimmed.Length; i++)
                {
                    char current = trimmed[i];
                    if (i + 1 < trimmed.Length && char.IsSurrogatePair(current, trimmed[i + 1]))
                    {
                        clean.Append(current);
                        clean.Append(trimmed[++i]);
                    }
                    else
                    {
                        clean.Append(XmlConvert.IsXmlChar(current) ? current : '\uFFFD');
                    }
                }
                return clean.ToString();
            }
        }

        private static string ShortName(string value)
        {
            int end = char.IsHighSurrogate(value[41]) ? 41 : 42;
            return value.Substring(0, end) + "…";
        }

        private static DoubleAnimation Ease(double from, double to, int milliseconds)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }

        private static T Named<T>(FrameworkElement panel, string name) where T : FrameworkElement
        {
            T control = panel.FindName(name) as T;
            if (control == null)
                throw new InvalidOperationException("Thought journal requires " + name + ".");
            return control;
        }

        private static Brush BrushAt(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Geometry ReadIcon(string path)
        {
            Geometry icon = Geometry.Parse(path);
            icon.Freeze();
            return icon;
        }

        private sealed class Thought
        {
            public readonly string Id;
            public readonly string Text;
            public readonly bool Completed;

            public Thought(string id, string text, bool completed)
            {
                Id = id;
                Text = text;
                Completed = completed;
            }
        }
    }
}
