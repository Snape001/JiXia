using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

namespace SidePage
{
    internal sealed class TaskBoard
    {
        private const string DragFormat = "SidePage.TaskCard";
        private const double CardSpacing = 7;
        private static readonly Brush CardFill = MakeBrush(0x34, 0x2D, 0x27);
        private static readonly Brush ActiveFill = MakeBrush(0x3C, 0x33, 0x2B);
        private static readonly Brush CardEdge = MakeBrush(0x49, 0x3B, 0x30);
        private static readonly Brush MainText = MakeBrush(0xF2, 0xE9, 0xDB);
        private static readonly Brush MutedText = MakeBrush(0xB6, 0xA9, 0x9A);
        private static readonly Brush Amber = MakeBrush(0xDD, 0xA4, 0x5D);
        private static readonly Brush DropFill = MakeBrush(0x3D, 0x32, 0x26);
        private static readonly Brush HotTrashFill = MakeBrush(0x4B, 0x38, 0x27);

        private readonly FrameworkElement panel;
        private readonly Section urgent;
        private readonly Section later;
        private readonly FrameworkElement restFooter;
        private readonly FrameworkElement trashTarget;
        private readonly TextBlock trashLabel;
        private readonly FrameworkElement undoBar;
        private readonly TextBlock undoLabel;
        private readonly Button undoButton;
        private readonly DiscardMotion discardMotion;
        private readonly string storagePath;
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer scrollTimer;
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private readonly List<Removal> removals = new List<Removal>();
        private readonly Border gap;
        private readonly string restingTrashLabel;
        private readonly Brush restingTrashForeground;
        private readonly Brush restingTrashBackground;
        private readonly Brush restingTrashEdge;
        private readonly object restingFooterToolTip;

        private Section editorSection;
        private Card editorCard;
        private TextBox editor;
        private Card pressedCard;
        private Point pressedAt;
        private Card draggedCard;
        private Removal settlingRemoval;
        private CardGhost ghost;
        private AdornerLayer ghostLayer;
        private Point ghostAnchor;
        private Section scrollingSection;
        private Section gapSection;
        private int gapIndex = -1;
        private Section droppedSection;
        private int droppedIndex;
        private bool trashHot;
        private bool droppedInTrash;
        private bool primaryValid;
        private bool pendingSave;
        private TimeSpan retryAt;

        public TaskBoard(FrameworkElement panel)
            : this(panel, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidePage"))
        {
        }

        internal TaskBoard(FrameworkElement panel, string storageDirectory)
        {
            this.panel = panel;
            storagePath = Path.Combine(storageDirectory, "tasks.xml");
            urgent = new Section(panel, "Urgent");
            later = new Section(panel, "Later");
            restFooter = Named<FrameworkElement>(panel, "RestFooter");
            trashTarget = Named<FrameworkElement>(panel, "TrashTarget");
            trashLabel = Named<TextBlock>(panel, "TrashLabel");
            undoBar = Named<FrameworkElement>(panel, "UndoBar");
            undoLabel = Named<TextBlock>(panel, "UndoLabel");
            undoButton = Named<Button>(panel, "UndoButton");
            discardMotion = new DiscardMotion(panel.FindName("BinArt") as Grid);
            restingTrashLabel = trashLabel.Text;
            restingTrashForeground = trashLabel.Foreground;
            restingFooterToolTip = restFooter.ToolTip;
            Border trashBorder = trashTarget as Border;
            if (trashBorder != null)
            {
                restingTrashBackground = trashBorder.Background;
                restingTrashEdge = trashBorder.BorderBrush;
            }

            gap = new Border
            {
                Background = DropFill,
                BorderBrush = Amber,
                BorderThickness = new Thickness(1, 2, 1, 0),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 0, CardSpacing),
                IsHitTestVisible = false
            };

            timer = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher);
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += OnTimer;
            scrollTimer = new DispatcherTimer(DispatcherPriority.Input, panel.Dispatcher);
            scrollTimer.Interval = TimeSpan.FromMilliseconds(38);
            scrollTimer.Tick += OnScrollTimer;

            HookSection(urgent);
            HookSection(later);
            undoButton.Click += delegate { Undo(); };
            panel.PreviewMouseDown += OnPanelMouseDown;
            panel.PreviewMouseMove += OnPanelMouseMove;
            panel.PreviewMouseLeftButtonUp += delegate { pressedCard = null; };
            panel.PreviewKeyDown += OnBoardKeyDown;
            panel.Unloaded += OnPanelUnloaded;
            panel.AllowDrop = true;
            panel.PreviewDragEnter += OnDragOver;
            panel.PreviewDragOver += OnDragOver;
            panel.PreviewDragLeave += OnDragLeave;
            panel.PreviewDrop += OnDrop;
            panel.PreviewQueryContinueDrag += OnQueryContinueDrag;

            Load();
            Refresh();
        }

        public bool IsInteracting { get { return editor != null || IsDraggingOrSettling; } }
        public bool IsDraggingOrSettling { get { return draggedCard != null || settlingRemoval != null; } }
        public int UrgentTotal { get { return urgent.Cards.Count; } }
        public event EventHandler Changed;

        public void FinishEditing()
        {
            EndEdit(true, false);
        }

        private void HookSection(Section section)
        {
            section.Add.Click += delegate { StartNew(section); };
            section.Zone.AllowDrop = true;
            ConfigureEditor(section.Entry);
        }

        private void StartNew(Section section)
        {
            if (editor == section.Entry)
            {
                editor.Focus();
                return;
            }

            FinishEditing();
            editorSection = section;
            editor = section.Entry;
            editor.Clear();
            editor.Visibility = Visibility.Visible;
            UpdateEmpty();
            if (section.Scroller != null)
                section.Scroller.ScrollToTop();
            FocusEditor(false);
            RaiseChanged();
        }

        private void StartEdit(Card card)
        {
            if (editorCard == card)
                return;

            FinishEditing();
            editorSection = card.Section;
            editorCard = card;
            editor = card.Editor;
            editor.Text = card.Text.Text;
            card.Text.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            FocusEditor(true);
            RaiseChanged();
        }

        private void ConfigureEditor(TextBox box)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.PreviewKeyDown += OnEditorKeyDown;
            box.LostKeyboardFocus += delegate(object sender, KeyboardFocusChangedEventArgs e)
            {
                if (editor == box && !IsDescendant(e.NewFocus as DependencyObject, box))
                    FinishEditing();
            };
        }

        private void FocusEditor(bool selectAll)
        {
            TextBox target = editor;
            target.BringIntoView();
            if (target.Focus())
            {
                if (selectAll)
                    target.SelectAll();
                return;
            }

            panel.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (editor != target)
                    return;
                target.BringIntoView();
                target.Focus();
                if (selectAll)
                    target.SelectAll();
            }));
        }

        private void EndEdit(bool commit, bool restoreKeyboardFocus)
        {
            if (editor == null)
                return;

            TextBox closedEditor = editor;
            Section section = editorSection;
            Card card = editorCard;
            string value = NormalizeText(closedEditor.Text);
            editor = null;
            editorSection = null;
            editorCard = null;
            closedEditor.Visibility = Visibility.Collapsed;
            closedEditor.Clear();

            if (card != null)
                card.Text.Visibility = Visibility.Visible;

            if (commit && value.Length > 0)
            {
                if (card == null)
                {
                    card = CreateCard(Guid.NewGuid().ToString("N"), value, section);
                    section.Cards.Add(card);
                    section.Items.Children.Add(card);
                    AnimateArrival(card);
                    Save();
                }
                else if (card.Text.Text != value)
                {
                    SetText(card, value);
                    Save();
                }
            }

            Refresh();
            if (restoreKeyboardFocus)
            {
                if (card != null)
                {
                    card.Focus();
                    card.BringIntoView();
                }
                else
                    section.Add.Focus();
            }
            RaiseChanged();
        }

        private void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndEdit(false, true);
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                EndEdit(true, true);
            }
        }

        private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (editor != null && !IsDescendant(e.OriginalSource as DependencyObject, editor))
                FinishEditing();
        }

        private void OnBoardKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && settlingRemoval != null)
            {
                FinishDiscard();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control &&
                !(Keyboard.FocusedElement is TextBox) && editor == null && draggedCard == null &&
                (settlingRemoval != null || removals.Count > 0))
            {
                Undo();
                e.Handled = true;
            }
        }

        private void OnPanelUnloaded(object sender, RoutedEventArgs e)
        {
            pressedCard = null;
            SetScrollSection(null);
            if (settlingRemoval != null)
                FinishDiscard();
            else
                ResetDragVisuals();
        }

        private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (draggedCard != null && !panel.IsLoaded)
            {
                e.Action = DragAction.Cancel;
                e.Handled = true;
            }
        }

        private Card CreateCard(string id, string value, Section section)
        {
            Card card = new Card(id, section)
            {
                Focusable = true,
                Margin = new Thickness(0, 0, 0, CardSpacing),
                Padding = new Thickness(10, 8, 10, 8),
                MinHeight = 38,
                CornerRadius = new CornerRadius(9),
                Background = CardFill,
                BorderThickness = new Thickness(1),
                BorderBrush = CardEdge,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                FocusVisualStyle = null,
                RenderTransform = new TranslateTransform()
            };

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });

            card.Accent = new Border
            {
                Width = 2,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0),
                CornerRadius = new CornerRadius(1),
                Background = Amber,
                IsHitTestVisible = false
            };
            card.Text = new TextBlock
            {
                Foreground = MainText,
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            card.Editor = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = MainText,
                CaretBrush = MainText,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxLines = 5,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(card.Editor, "编辑便签");
            ConfigureEditor(card.Editor);

            FrameworkElement grip = CreateGrip();
            Grid.SetColumn(card.Text, 1);
            Grid.SetColumn(card.Editor, 1);
            Grid.SetColumn(grip, 2);
            content.Children.Add(card.Accent);
            content.Children.Add(card.Text);
            content.Children.Add(card.Editor);
            content.Children.Add(grip);
            card.Child = content;

            SetText(card, value);
            UpdateCardSection(card, section);
            AutomationProperties.SetHelpText(card, "上下移动，左右换区，F2 编辑，Delete 移除");
            card.MouseEnter += delegate { UpdateCardLook(card); };
            card.MouseLeave += delegate { UpdateCardLook(card); };
            card.IsKeyboardFocusedChanged += delegate { UpdateCardLook(card); };
            card.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { OnCardMouseDown(card, e); };
            card.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { OnCardKeyDown(card, e); };
            return card;
        }

        private static FrameworkElement CreateGrip()
        {
            Grid grip = new Grid
            {
                Width = 8,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Opacity = 0.56,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动便签"
            };

            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    grip.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 2,
                        Height = 2,
                        Margin = new Thickness(column * 4, row * 5, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Fill = MutedText,
                        IsHitTestVisible = false
                    });
                }
            }

            return grip;
        }

        private static void SetText(Card card, string value)
        {
            card.Text.Text = value;
            AutomationProperties.SetName(card, value);
        }

        private void UpdateCardSection(Card card, Section section)
        {
            card.Section = section;
            card.Accent.Visibility = section == urgent ? Visibility.Visible : Visibility.Hidden;
        }

        private static void UpdateCardLook(Card card)
        {
            card.Background = card.IsMouseOver || card.IsKeyboardFocused ? ActiveFill : CardFill;
            card.BorderBrush = card.IsKeyboardFocused ? Amber : CardEdge;
        }

        private void OnCardMouseDown(Card card, MouseButtonEventArgs e)
        {
            if (editorCard == card)
                return;

            card.Focus();
            if (e.ClickCount == 2)
            {
                pressedCard = null;
                StartEdit(card);
                e.Handled = true;
                return;
            }

            pressedCard = card;
            pressedAt = e.GetPosition(panel);
        }

        private void OnCardKeyDown(Card card, KeyEventArgs e)
        {
            if (!card.IsKeyboardFocused)
                return;

            Section section = card.Section;
            int index = section.Cards.IndexOf(card);
            switch (e.Key)
            {
                case Key.Enter:
                case Key.F2:
                    StartEdit(card);
                    break;
                case Key.Delete:
                    Remove(card, true);
                    break;
                case Key.Up:
                    Move(card, section, Math.Max(0, index - 1), true);
                    break;
                case Key.Down:
                    Move(card, section, Math.Min(section.Cards.Count - 1, index + 1), true);
                    break;
                case Key.Left:
                case Key.Right:
                    Section target = section == urgent ? later : urgent;
                    Move(card, target, Math.Min(index, target.Cards.Count), true);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                pressedCard = null;
                return;
            }
            if (pressedCard == null || draggedCard != null)
                return;

            Point current = e.GetPosition(panel);
            if (Math.Abs(current.X - pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            Card card = pressedCard;
            pressedCard = null;
            e.Handled = true;
            StartDrag(card, current);
        }

        private void StartDrag(Card card, Point current)
        {
            FinishDiscard();
            FinishEditing();
            draggedCard = card;
            droppedSection = null;
            droppedInTrash = false;

            DragDropEffects effect = DragDropEffects.None;
            try
            {
                AddGhost(card, current);
                gap.Height = Math.Max(38, card.ActualHeight);
                card.Visibility = Visibility.Collapsed;
                SetGap(card.Section, card.Section.Cards.IndexOf(card), false);
                Refresh();
                discardMotion.Lift();
                RaiseChanged();
                panel.UpdateLayout();
                effect = DragDrop.DoDragDrop(panel, new DataObject(DragFormat, card.Id), DragDropEffects.Move);
            }
            finally
            {
                SetScrollSection(null);
                SetGap(null, -1, false);
                draggedCard = null;
                card.Visibility = Visibility.Visible;
                if ((effect & DragDropEffects.Move) != 0 && droppedInTrash)
                    BeginDiscard(card);
                else
                {
                    ResetDragVisuals();
                    Refresh();
                    if ((effect & DragDropEffects.Move) != 0 && droppedSection != null)
                    {
                        if (!Move(card, droppedSection, droppedIndex, true))
                        {
                            AnimateArrival(card);
                            RaiseChanged();
                        }
                    }
                    else
                    {
                        card.Focus();
                        RaiseChanged();
                    }
                }
            }
        }

        private void BeginDiscard(Card card)
        {
            // Commit the removal before starting any presentation clock.
            settlingRemoval = DetachCard(card);
            trashLabel.Text = "已完成";
            Refresh();

            Rect mouth;
            if (ghost == null || !SystemParameters.ClientAreaAnimation || !discardMotion.TryGetMouth(panel, out mouth))
            {
                FinishDiscard();
                return;
            }

            ghost.PrepareFall(mouth);
            if (discardMotion.Play(ghost.FallFrame, FinishDiscard))
                RaiseChanged();
            else
                FinishDiscard();
        }

        private void FinishDiscard()
        {
            Removal removal = settlingRemoval;
            if (removal == null)
                return;

            settlingRemoval = null;
            ResetDragVisuals();
            OfferUndo(removal);
            Refresh();
            RaiseChanged();
        }

        private void ResetDragVisuals()
        {
            RemoveGhost();
            SetTrashHot(false);
            trashLabel.Text = restingTrashLabel;
            discardMotion.Reset();
        }

        private void AddGhost(Card card, Point current)
        {
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(panel);
            PresentationSource source = PresentationSource.FromVisual(panel);
            if (layer == null || source == null || source.CompositionTarget == null ||
                card.ActualWidth < 1 || card.ActualHeight < 1)
                return;

            Size size = new Size(card.ActualWidth, Math.Min(106, card.ActualHeight));
            double scale = Math.Max(1, source.CompositionTarget.TransformToDevice.M11);
            RenderTargetBitmap image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
                (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            VisualBrush brush = new VisualBrush(card)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(size)
            };
            DrawingVisual preview = new DrawingVisual();
            using (DrawingContext drawing = preview.RenderOpen())
                drawing.DrawRectangle(brush, null, new Rect(size));
            image.Render(preview);
            image.Freeze();

            const double previewScale = 0.93;
            Point cardTop = card.TranslatePoint(new Point(), panel);
            ghostAnchor = new Point(
                Math.Max(0, Math.Min(size.Width, pressedAt.X - cardTop.X)) * previewScale,
                Math.Max(0, Math.Min(size.Height - 4, pressedAt.Y - cardTop.Y)) * previewScale);
            ghost = new CardGhost(panel, image, new Size(size.Width * previewScale, size.Height * previewScale));
            ghostLayer = layer;
            ghostLayer.Add(ghost);
            UpdateGhost(current, false);
        }

        private void RemoveGhost()
        {
            if (ghost == null)
                return;
            ghostLayer.Remove(ghost);
            ghost = null;
            ghostLayer = null;
        }

        private void UpdateGhost(Point position, bool overTrash)
        {
            if (ghost == null)
                return;

            const double inset = 5;
            double x = position.X - ghostAnchor.X;
            double y = position.Y - ghostAnchor.Y;
            if (overTrash)
                y = trashTarget.TranslatePoint(new Point(), panel).Y - ghost.CardSize.Height - 13;
            x = Math.Max(inset, Math.Min(x, panel.ActualWidth - ghost.CardSize.Width - inset));
            y = Math.Max(inset, Math.Min(y, panel.ActualHeight - ghost.CardSize.Height - inset));
            ghost.MoveTo(new Point(x, y));
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!IsOwnDrag(e))
                return;

            bool overTrash = IsOver(trashTarget, e.GetPosition(trashTarget));
            Section section = overTrash ? null : FindSection(e);
            SetTrashHot(overTrash);
            UpdateGhost(e.GetPosition(panel), overTrash);
            if (section != null)
            {
                SetGap(section, FindSlot(section, e.GetPosition(section.Items)), true);
                SetScrollSection(ScrollDelta(section, e.GetPosition(section.Zone)) == 0 ? null : section);
            }
            else
            {
                SetGap(null, -1, true);
                SetScrollSection(null);
            }
            e.Effects = overTrash || section != null ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            if (!IsOwnDrag(e))
                return;

            NativePoint cursor;
            if (GetCursorPos(out cursor) && PresentationSource.FromVisual(panel) != null &&
                !IsOver(panel, panel.PointFromScreen(new Point(cursor.X, cursor.Y))))
            {
                if (ghost != null)
                    ghost.Visibility = Visibility.Collapsed;
                SetScrollSection(null);
                SetGap(null, -1, true);
                SetTrashHot(false);
            }
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!IsOwnDrag(e))
                return;

            droppedInTrash = IsOver(trashTarget, e.GetPosition(trashTarget));
            droppedSection = droppedInTrash ? null : FindSection(e);
            if (droppedInTrash)
            {
                SetTrashHot(true);
                UpdateGhost(e.GetPosition(panel), true);
            }
            if (droppedSection != null)
                droppedIndex = FindSlot(droppedSection, e.GetPosition(droppedSection.Items));
            e.Effects = droppedInTrash || droppedSection != null ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private bool IsOwnDrag(DragEventArgs e)
        {
            return draggedCard != null && e.Data.GetDataPresent(DragFormat, false) &&
                string.Equals(e.Data.GetData(DragFormat, false) as string, draggedCard.Id, StringComparison.Ordinal);
        }

        private Section FindSection(DragEventArgs e)
        {
            if (IsOver(urgent.Zone, e.GetPosition(urgent.Zone)))
                return urgent;
            if (IsOver(later.Zone, e.GetPosition(later.Zone)))
                return later;
            return null;
        }

        private static bool IsOver(FrameworkElement element, Point relative)
        {
            return element.IsVisible && relative.X >= 0 && relative.Y >= 0 &&
                relative.X <= element.ActualWidth && relative.Y <= element.ActualHeight;
        }

        private void SetScrollSection(Section section)
        {
            if (scrollingSection == section)
                return;
            scrollingSection = section;
            if (section == null)
                scrollTimer.Stop();
            else
                scrollTimer.Start();
        }

        private static double ScrollDelta(Section section, Point inZone)
        {
            ScrollViewer scroller = section.Scroller;
            if (scroller == null || !IsOver(section.Zone, inZone) || scroller.ScrollableHeight <= 0)
                return 0;

            Point relative = section.Zone.TranslatePoint(inZone, scroller);
            const double band = 23;
            if (relative.Y >= 0 && relative.Y < band && scroller.VerticalOffset > 0)
                return -Math.Min(scroller.VerticalOffset, 5 + (band - relative.Y) * 0.65);

            double fromBottom = scroller.ActualHeight - relative.Y;
            double remaining = scroller.ScrollableHeight - scroller.VerticalOffset;
            if (fromBottom >= 0 && fromBottom < band && remaining > 0)
                return Math.Min(remaining, 5 + (band - fromBottom) * 0.65);
            return 0;
        }

        private void OnScrollTimer(object sender, EventArgs e)
        {
            Section section = scrollingSection;
            NativePoint cursor;
            if (draggedCard == null || section == null || !GetCursorPos(out cursor) ||
                PresentationSource.FromVisual(panel) == null)
            {
                SetScrollSection(null);
                return;
            }

            Point screen = new Point(cursor.X, cursor.Y);
            double delta = ScrollDelta(section, section.Zone.PointFromScreen(screen));
            if (delta == 0)
            {
                SetScrollSection(null);
                return;
            }

            section.Scroller.ScrollToVerticalOffset(section.Scroller.VerticalOffset + delta);
            panel.UpdateLayout();
            SetGap(section, FindSlot(section, section.Items.PointFromScreen(screen)), true);
        }

        private int FindSlot(Section section, Point relative)
        {
            if (section == gapSection)
            {
                double gapTop = LayoutTop(gap);
                if (relative.Y >= gapTop && relative.Y <= gapTop + gap.ActualHeight + CardSpacing)
                    return gapIndex;
            }

            int index = 0;
            foreach (Card card in section.Cards)
            {
                if (card == draggedCard)
                    continue;
                if (relative.Y < LayoutTop(card) + card.ActualHeight / 2)
                    return index;
                index++;
            }
            return index;
        }

        private void SetGap(Section section, int index, bool animate)
        {
            if (section == gapSection && index == gapIndex)
                return;

            bool reflow = animate;
            animate = animate && SystemParameters.ClientAreaAnimation;
            Dictionary<Card, double> oldPositions = null;
            if (animate)
            {
                oldPositions = new Dictionary<Card, double>();
                RememberPositions(gapSection, oldPositions);
                if (section != gapSection)
                    RememberPositions(section, oldPositions);
            }

            if (gapSection != null)
                gapSection.Items.Children.Remove(gap);
            gapSection = section;
            gapIndex = index;
            if (section != null)
            {
                int physicalIndex = index;
                if (draggedCard.Section == section && section.Cards.IndexOf(draggedCard) < index)
                    physicalIndex++;
                section.Items.Children.Insert(physicalIndex, gap);
                if (animate)
                    gap.BeginAnimation(UIElement.OpacityProperty, Motion(0.2, 1, 125));
            }
            UpdateEmpty();
            if (reflow)
                panel.UpdateLayout();
            if (oldPositions == null)
                return;

            foreach (KeyValuePair<Card, double> previous in oldPositions)
            {
                Card card = previous.Key;
                double shift = previous.Value - LayoutTop(card);
                TranslateTransform transform = (TranslateTransform)card.RenderTransform;
                if (Math.Abs(shift) > 0.5)
                    transform.BeginAnimation(TranslateTransform.YProperty, Motion(shift, 0, 145));
                else
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }

        private void RememberPositions(Section section, Dictionary<Card, double> positions)
        {
            if (section == null)
                return;

            foreach (Card card in section.Cards)
            {
                if (card != draggedCard)
                    positions.Add(card, LayoutTop(card) + ((TranslateTransform)card.RenderTransform).Y);
            }
        }

        private static double LayoutTop(FrameworkElement element)
        {
            return System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element).Top + element.Margin.Top;
        }

        private void SetTrashHot(bool hot)
        {
            if (trashHot == hot)
                return;

            trashHot = hot;
            trashLabel.Text = hot ? "松手完成" : restingTrashLabel;
            trashLabel.Foreground = hot ? Amber : restingTrashForeground;
            discardMotion.SetOpen(hot);
            if (ghost != null)
                ghost.SetHot(hot);
            Border border = trashTarget as Border;
            if (border != null)
            {
                border.Background = hot ? HotTrashFill : restingTrashBackground;
                border.BorderBrush = hot ? Amber : restingTrashEdge;
            }
        }

        private bool Move(Card card, Section target, int targetIndex, bool focus)
        {
            Section source = card.Section;
            int sourceIndex = source.Cards.IndexOf(card);
            bool changed = source != target || sourceIndex != targetIndex;
            if (changed)
            {
                source.Items.Children.Remove(card);
                source.Cards.RemoveAt(sourceIndex);
                target.Cards.Insert(targetIndex, card);
                target.Items.Children.Insert(targetIndex, card);
                UpdateCardSection(card, target);
                Save();
                Refresh();
                AnimateArrival(card);
                RaiseChanged();
            }
            if (focus)
            {
                card.Focus();
                card.BringIntoView();
            }
            return changed;
        }

        private void Remove(Card card, bool focus)
        {
            FinishDiscard();
            Removal removal = DetachCard(card);
            OfferUndo(removal);
            Refresh();
            if (focus)
            {
                if (removal.Section.Cards.Count > 0)
                    removal.Section.Cards[Math.Min(removal.Index, removal.Section.Cards.Count - 1)].Focus();
                else
                    removal.Section.Add.Focus();
            }
            RaiseChanged();
        }

        private Removal DetachCard(Card card)
        {
            Section section = card.Section;
            int index = section.Cards.IndexOf(card);
            Removal removal = new Removal
            {
                Card = card,
                Section = section,
                Index = index,
                Previous = index > 0 ? section.Cards[index - 1] : null,
                Next = index + 1 < section.Cards.Count ? section.Cards[index + 1] : null
            };
            section.Items.Children.Remove(card);
            section.Cards.RemoveAt(index);
            Save();
            return removal;
        }

        private void OfferUndo(Removal removal)
        {
            removal.Expires = uptime.Elapsed + TimeSpan.FromSeconds(5);
            removals.Add(removal);
            timer.Start();
        }

        private void Undo()
        {
            FinishDiscard();
            PruneRemovals();
            if (removals.Count == 0)
            {
                Refresh();
                return;
            }

            FinishEditing();
            Removal removal = removals[removals.Count - 1];
            removals.RemoveAt(removals.Count - 1);
            Section section = removal.Section;
            int index = section.Cards.IndexOf(removal.Next);
            if (index < 0)
            {
                int previous = section.Cards.IndexOf(removal.Previous);
                index = previous < 0 ? Math.Min(removal.Index, section.Cards.Count) : previous + 1;
            }

            section.Cards.Insert(index, removal.Card);
            section.Items.Children.Insert(index, removal.Card);
            AnimateArrival(removal.Card);
            Save();
            Refresh();
            removal.Card.Focus();
            removal.Card.BringIntoView();
            RaiseChanged();
        }

        private void Refresh()
        {
            urgent.Count.Text = urgent.Cards.Count.ToString(CultureInfo.InvariantCulture);
            later.Count.Text = later.Cards.Count.ToString(CultureInfo.InvariantCulture);
            UpdateEmpty();
            UpdateFooter();
        }

        private void UpdateEmpty()
        {
            UpdateEmpty(urgent);
            UpdateEmpty(later);
        }

        private void UpdateEmpty(Section section)
        {
            int visible = section.Cards.Count;
            if (draggedCard != null && draggedCard.Section == section)
                visible--;
            bool show = visible == 0 && section != gapSection && section != editorSection;
            section.Empty.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFooter()
        {
            bool dragging = IsDraggingOrSettling;
            bool undoing = !dragging && removals.Count != 0;
            restFooter.Visibility = dragging ? Visibility.Collapsed : Visibility.Visible;
            trashTarget.Visibility = dragging ? Visibility.Visible : Visibility.Collapsed;
            undoBar.Visibility = undoing ? Visibility.Visible : Visibility.Collapsed;
            if (undoing)
                undoLabel.Text = "已完成";
        }

        private void PruneRemovals()
        {
            TimeSpan now = uptime.Elapsed;
            while (removals.Count > 0 && removals[0].Expires <= now)
                removals.RemoveAt(0);
        }

        private void OnTimer(object sender, EventArgs e)
        {
            int before = removals.Count;
            PruneRemovals();
            if (before != removals.Count)
            {
                UpdateFooter();
                RaiseChanged();
            }
            if (pendingSave && uptime.Elapsed >= retryAt)
                Save();
            if (removals.Count == 0 && !pendingSave)
                timer.Stop();
        }

        private void Load()
        {
            List<StoredCard> saved = Read(storagePath);
            primaryValid = saved != null;
            if (saved == null)
                saved = Read(storagePath + ".bak");
            if (saved == null)
                return;

            foreach (StoredCard task in saved)
            {
                Section section = task.Urgent ? urgent : later;
                Card card = CreateCard(task.Id, task.Text, section);
                section.Cards.Add(card);
                section.Items.Children.Add(card);
            }
            if (!primaryValid)
                Save();
        }

        private static List<StoredCard> Read(string path)
        {
            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader reader = XmlReader.Create(input, settings))
                    doc.Load(reader);

                XmlElement root = doc.DocumentElement;
                if (root == null || root.Name != "Tasks" || root.GetAttribute("version") != "1")
                    return null;

                List<StoredCard> cards = new List<StoredCard>();
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (XmlNode node in root.ChildNodes)
                {
                    XmlElement task = node as XmlElement;
                    if (task == null)
                        continue;

                    Guid id;
                    bool isUrgent;
                    string value = task.InnerText.Trim();
                    if (task.Name != "Task" || !Guid.TryParseExact(task.GetAttribute("id"), "N", out id) ||
                        !bool.TryParse(task.GetAttribute("urgent"), out isUrgent) || value.Length == 0 ||
                        !ids.Add(id.ToString("N")))
                        return null;
                    cards.Add(new StoredCard { Id = id.ToString("N"), Text = value, Urgent = isUrgent });
                }
                return cards;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (XmlException) { return null; }
        }

        private void Save()
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
                        writer.WriteStartElement("Tasks");
                        writer.WriteAttributeString("version", "1");
                        WriteCards(writer, urgent, true);
                        WriteCards(writer, later, false);
                        writer.WriteEndElement();
                    }
                    stream.Flush(true);
                }

                if (File.Exists(storagePath))
                    File.Replace(temporary, storagePath, primaryValid ? storagePath + ".bak" : null, true);
                else
                    File.Move(temporary, storagePath);
                primaryValid = true;
                pendingSave = false;
                restFooter.ToolTip = restingFooterToolTip;
            }
            catch (IOException) { QueueSave(); }
            catch (UnauthorizedAccessException) { QueueSave(); }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void WriteCards(XmlWriter writer, Section section, bool isUrgent)
        {
            foreach (Card card in section.Cards)
            {
                writer.WriteStartElement("Task");
                writer.WriteAttributeString("id", card.Id);
                writer.WriteAttributeString("urgent", XmlConvert.ToString(isUrgent));
                writer.WriteString(card.Text.Text);
                writer.WriteEndElement();
            }
        }

        private void QueueSave()
        {
            pendingSave = true;
            retryAt = uptime.Elapsed + TimeSpan.FromSeconds(5);
            restFooter.ToolTip = "任务已更新，本地写入失败，稍后会自动重试。";
            timer.Start();
        }

        private static void AnimateArrival(Card card)
        {
            if (!SystemParameters.ClientAreaAnimation)
                return;
            TranslateTransform transform = (TranslateTransform)card.RenderTransform;
            transform.BeginAnimation(TranslateTransform.YProperty, Motion(5, 0, 170));
            card.BeginAnimation(UIElement.OpacityProperty, Motion(0.58, 1, 130));
        }

        private static DoubleAnimation Motion(double from, double to, int milliseconds)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private static bool IsDescendant(DependencyObject element, DependencyObject target)
        {
            while (element != null)
            {
                if (element == target)
                    return true;
                element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private static string NormalizeText(string text)
        {
            string trimmed = text.Trim();
            try { return XmlConvert.VerifyXmlChars(trimmed); }
            catch (XmlException)
            {
                StringBuilder cleaned = new StringBuilder(trimmed.Length);
                for (int index = 0; index < trimmed.Length; index++)
                {
                    char current = trimmed[index];
                    if (index + 1 < trimmed.Length && char.IsSurrogatePair(current, trimmed[index + 1]))
                    {
                        cleaned.Append(current);
                        cleaned.Append(trimmed[++index]);
                    }
                    else if (XmlConvert.IsXmlChar(current))
                        cleaned.Append(current);
                    else
                        cleaned.Append('\uFFFD');
                }
                return cleaned.ToString();
            }
        }

        private static T Named<T>(FrameworkElement owner, string name) where T : FrameworkElement
        {
            T element = owner.FindName(name) as T;
            if (element == null)
                throw new InvalidOperationException("Task board requires " + name + ".");
            return element;
        }

        private static Brush MakeBrush(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        private sealed class Section
        {
            public readonly Button Add;
            public readonly TextBlock Count;
            public readonly FrameworkElement Zone;
            public readonly StackPanel Items;
            public readonly TextBox Entry;
            public readonly FrameworkElement Empty;
            public readonly ScrollViewer Scroller;
            public readonly List<Card> Cards = new List<Card>();

            public Section(FrameworkElement owner, string prefix)
            {
                Add = Named<Button>(owner, prefix + "Add");
                Count = Named<TextBlock>(owner, prefix + "Count");
                Zone = Named<FrameworkElement>(owner, prefix + "Zone");
                Items = Named<StackPanel>(owner, prefix + "Items");
                Entry = Named<TextBox>(owner, prefix + "Entry");
                Empty = Named<FrameworkElement>(owner, prefix + "Empty");
                for (DependencyObject ancestor = LogicalTreeHelper.GetParent(Items); ancestor != null;
                    ancestor = LogicalTreeHelper.GetParent(ancestor))
                {
                    Scroller = ancestor as ScrollViewer;
                    if (Scroller != null)
                        break;
                }
            }
        }

        private sealed class CardGhost : Adorner
        {
            private static readonly DependencyProperty TiltProperty = DependencyProperty.Register("Tilt", typeof(double), typeof(CardGhost),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
            private readonly ImageSource image;
            private Point offset;
            private Geometry mouthClip;
            private Point releaseOrigin;
            private Point releaseEnd;
            private Point fallPosition;
            private double releaseScale;
            private double finalScale;
            private double releaseAngle;
            private double fallScale;
            private double fallAngle;
            private double fallOpacity;
            public readonly Size CardSize;

            public CardGhost(FrameworkElement owner, ImageSource image, Size size) : base(owner)
            {
                this.image = image;
                CardSize = size;
                IsHitTestVisible = false;
                Focusable = false;
                ClipToBounds = true;
            }

            public void MoveTo(Point position)
            {
                Visibility = Visibility.Visible;
                if (offset == position)
                    return;
                offset = position;
                InvalidateVisual();
            }

            public void SetHot(bool hot)
            {
                double from = (double)GetValue(TiltProperty);
                double to = hot ? 1 : 0;
                SetValue(TiltProperty, to);
                BeginAnimation(TiltProperty, SystemParameters.ClientAreaAnimation ? Motion(from, to, 115) : null,
                    HandoffBehavior.SnapshotAndReplace);
            }

            public void PrepareFall(Rect mouth)
            {
                double t = (double)GetValue(TiltProperty);
                releaseOrigin = offset + new Vector(CardSize.Width / 2, CardSize.Height / 2);
                releaseEnd = new Point(mouth.Left + mouth.Width / 2, mouth.Bottom + 2.4);
                releaseScale = 1 - 0.16 * t;
                releaseAngle = -7 * t;
                finalScale = Math.Min(0.13, mouth.Width * 0.76 / CardSize.Width);
                Rect above = new Rect(0, 0, AdornedElement.RenderSize.Width, Math.Max(0, mouth.Top + mouth.Height * 0.48));
                mouthClip = new CombinedGeometry(GeometryCombineMode.Union,
                    new RectangleGeometry(above), new EllipseGeometry(mouth));
                mouthClip.Freeze();
            }

            public void FallFrame(double progress)
            {
                double travel = DiscardMotion.Ease(progress, 0, 0.46);
                double descend = DiscardMotion.Ease(progress, 0.26, 0.73);
                double arc = Math.Sin(Math.PI * Math.Min(1, progress / 0.73));
                fallPosition = new Point(releaseOrigin.X + (releaseEnd.X - releaseOrigin.X) * travel,
                    releaseOrigin.Y + (releaseEnd.Y - releaseOrigin.Y) * descend - arc * 9.5);
                double shrink = DiscardMotion.Ease(progress, 0, 0.67);
                fallScale = releaseScale + (finalScale - releaseScale) * shrink;
                fallAngle = releaseAngle * (1 - travel) + 19 * arc;
                fallOpacity = 0.89 * (1 - DiscardMotion.Ease(progress, 0.60, 0.74));
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                bool falling = mouthClip != null;
                double tilt = (double)GetValue(TiltProperty);
                double scale = falling ? fallScale : 1 - 0.16 * tilt;
                double angle = falling ? fallAngle : -7 * tilt;
                double opacity = falling ? fallOpacity : 0.89;
                if (opacity <= 0 || scale <= 0)
                    return;

                Point center = falling ? fallPosition : offset + new Vector(CardSize.Width / 2, CardSize.Height / 2);
                Size size = new Size(CardSize.Width * scale, CardSize.Height * scale);
                Point corner = center - new Vector(size.Width / 2, size.Height / 2);
                if (falling)
                    drawing.PushClip(mouthClip);
                drawing.PushTransform(new RotateTransform(angle, center.X, center.Y));
                if (!falling)
                {
                    Rect shadow = new Rect(corner + new Vector(1, 3), size);
                    drawing.PushOpacity(0.2);
                    drawing.DrawRoundedRectangle(Brushes.Black, null, shadow, 9, 9);
                    drawing.Pop();
                }
                drawing.PushOpacity(opacity);
                drawing.DrawImage(image, new Rect(corner, size));
                drawing.Pop();
                drawing.Pop();
                if (falling)
                    drawing.Pop();
            }
        }

        private sealed class Card : Border
        {
            public readonly string Id;
            public Section Section;
            public TextBlock Text;
            public TextBox Editor;
            public Border Accent;

            public Card(string id, Section section)
            {
                Id = id;
                Section = section;
            }

            protected override AutomationPeer OnCreateAutomationPeer()
            {
                return new CardPeer(this);
            }
        }

        private sealed class CardPeer : FrameworkElementAutomationPeer
        {
            public CardPeer(Card card) : base(card) { }

            protected override string GetClassNameCore() { return "TaskCard"; }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.ListItem; }
            protected override bool IsControlElementCore() { return true; }
        }

        private sealed class Removal
        {
            public Card Card;
            public Section Section;
            public int Index;
            public Card Previous;
            public Card Next;
            public TimeSpan Expires;
        }

        private sealed class StoredCard
        {
            public string Id;
            public string Text;
            public bool Urgent;
        }
    }
}
