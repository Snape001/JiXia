using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SidePage
{
    internal sealed class DockHost
    {
        private const double HandleWidth = 28;
        private const double HandleHeight = 72;
        private const double DrawerWidth = 392;
        private const double DrawerHeight = 650;
        private const double MinimumDrawerHeight = 410;
        private const int OpenDelay = 160;
        private const int CloseDelay = 500;
        private const int ExtendedStyle = -20;
        private const int ToolWindowStyle = 0x80;
        private const int GetWorkArea = 0x0030;
        private const int SettingsChanged = 0x001A;
        private const int DisplayChanged = 0x007E;
        private const int DpiChanged = 0x02E0;
        private const int MouseActivate = 0x0021;
        private const double ClosedOpacity = 0.83;
        private static readonly uint ProcessId = GetCurrentProcessId();

        private readonly TaskBoard board;
        private readonly ThoughtJournal journal;
        private readonly string storageDirectory;
        private readonly Window handle;
        private readonly Window drawer;
        private readonly Button handleButton;
        private readonly Button pinButton;
        private readonly TextBox thoughtEntry;
        private readonly Grid framedPanel;
        private readonly FrameworkElement drawerContent;
        private readonly Viewbox drawerSurface;
        private readonly TranslateTransform slide = new TranslateTransform();
        private readonly TextBlock urgentLabel;
        private readonly Ellipse urgentDot;
        private readonly Forms.NotifyIcon tray;
        private readonly TrayMenu trayMenu;
        private readonly Drawing.Icon trayIcon;
        private readonly DispatcherTimer pointerTimer;
        private readonly DispatcherTimer thoughtCollapseTimer;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private bool started;
        private bool disposed;
        private bool quitting;
        private bool expanded;
        private bool pinned;
        private bool keyboardMode;
        private bool hoverRequiresLeave;
        private bool layoutQueued;
        private long handleEntered = -1;
        private long drawerLeft = -1;
        private long keepOpenUntil;
        private int slideVersion;
        private Point keyboardPointer;
        private IntPtr previousForeground;
        private uint previousForegroundProcess;

        public DockHost(FrameworkElement panel, TaskBoard board, Drawing.Icon icon)
            : this(panel, board, icon,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidePage"))
        {
        }

        internal DockHost(FrameworkElement panel, TaskBoard board, Drawing.Icon icon, string storageDirectory)
        {
            this.board = board;
            this.storageDirectory = storageDirectory;
            journal = new ThoughtJournal(panel, storageDirectory,
                delegate { return board.IsDraggingOrSettling; }, board.FinishEditing);
            journal.Captured += OnThoughtCaptured;

            handle = CreateWindow("记下把手");
            handle.Closing += WindowClosing;
            handle.SourceInitialized += ObserveWindowSource;

            urgentLabel = new TextBlock
            {
                Foreground = ColorBrush("#D5C9BD"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 5)
            };
            urgentDot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center };
            handleButton = BuildHandle();
            handleButton.PreviewMouseDown += delegate { thoughtCollapseTimer.Stop(); };
            handleButton.Click += delegate { Open(true, !handleButton.IsMouseOver); };
            handle.Content = handleButton;

            drawer = CreateWindow("记下");
            drawer.Closing += WindowClosing;
            drawer.SourceInitialized += ObserveWindowSource;
            drawerContent = new Border
            {
                Margin = new Thickness(20, 20, 0, 20),
                Child = new AdornerDecorator { Child = panel },
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 19,
                    Direction = 230,
                    ShadowDepth = 5,
                    Opacity = 0.48
                }
            };
            framedPanel = new Grid { Width = DrawerWidth, Height = DrawerHeight };
            framedPanel.Children.Add(drawerContent);
            drawerSurface = new Viewbox
            {
                Child = framedPanel,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                RenderTransform = slide,
                Opacity = ClosedOpacity
            };
            Grid viewport = new Grid { ClipToBounds = true };
            viewport.Children.Add(drawerSurface);
            drawer.Content = viewport;
            drawer.PreviewKeyDown += DrawerKeyDown;
            drawer.PreviewMouseMove += DrawerMouseMove;
            drawer.PreviewMouseDown += delegate
            {
                keyboardMode = false;
                thoughtCollapseTimer.Stop();
            };
            drawer.PreviewMouseWheel += delegate { thoughtCollapseTimer.Stop(); };
            drawer.Deactivated += delegate
            {
                keyboardMode = false;
                if (thoughtCollapseTimer.IsEnabled)
                    previousForeground = IntPtr.Zero;
            };

            pinButton = panel.FindName("PinButton") as Button;
            if (pinButton != null)
                pinButton.Click += delegate { TogglePin(); };

            Button closeButton = panel.FindName("CloseButton") as Button;
            if (closeButton != null)
            {
                AutomationProperties.SetName(closeButton, "收起记下");
                closeButton.ToolTip = "收起记下";
                closeButton.Click += delegate { Collapse(true); };
            }

            trayIcon = (Drawing.Icon)(icon ?? Drawing.SystemIcons.Application).Clone();
            trayMenu = new TrayMenu(delegate
            {
                if (expanded)
                    Collapse(true);
                else
                    Reveal();
            }, TogglePin, Quit);
            trayMenu.Opening += delegate
            {
                thoughtCollapseTimer.Stop();
                trayMenu.SetState(expanded, pinned);
            };
            tray = new Forms.NotifyIcon { Icon = trayIcon, ContextMenuStrip = trayMenu };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs args)
            {
                if (args.Button == Forms.MouseButtons.Left)
                    Reveal();
            };

            pointerTimer = new DispatcherTimer(DispatcherPriority.Background, handle.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            pointerTimer.Tick += ObservePointer;

            thoughtCollapseTimer = new DispatcherTimer(DispatcherPriority.Background, drawer.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(290)
            };
            thoughtCollapseTimer.Tick += CollapseAfterThought;
            thoughtEntry = (TextBox)panel.FindName("ThoughtEntry");
            thoughtEntry.TextChanged += delegate
            {
                if (thoughtEntry.Text.Length != 0)
                    thoughtCollapseTimer.Stop();
            };
            FrameworkElement review = (FrameworkElement)panel.FindName("ThoughtReview");
            review.IsVisibleChanged += delegate
            {
                if (review.IsVisible)
                    thoughtCollapseTimer.Stop();
            };
        }

        public void Start()
        {
            if (started || disposed)
                return;

            started = true;
            Application.Current.Exit += ApplicationExit;
            Application.Current.SessionEnding += SessionEnding;
            board.Changed += UpdateUrgency;
            UpdatePin();
            UpdateUrgency(this, EventArgs.Empty);

            PositionWindows();
            handle.Show();
            drawer.Owner = handle;
            PositionWindows();
            tray.Visible = true;
            pointerTimer.Start();

            if (RecordFirstLaunch())
            {
                Open(false, false);
                keepOpenUntil = clock.ElapsedMilliseconds + 8000;
            }
        }

        public void Reveal()
        {
            if (!handle.Dispatcher.CheckAccess())
            {
                handle.Dispatcher.BeginInvoke(new Action(Reveal));
                return;
            }

            Open(true, true);
        }

        private static Window CreateWindow(string title)
        {
            Window window = new Window
            {
                Title = title,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                SetWindowLong(hwnd, ExtendedStyle, GetWindowLong(hwnd, ExtendedStyle) | ToolWindowStyle);
            };
            return window;
        }

        private Button BuildHandle()
        {
            StackPanel symbols = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            symbols.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,1 L 2,6 L 7,11"),
                Stroke = ColorBrush("#DBA457"),
                StrokeThickness = 1.45,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 9,
                Height = 12
            });
            symbols.Children.Add(urgentLabel);
            symbols.Children.Add(urgentDot);

            FrameworkElementFactory shape = new FrameworkElementFactory(typeof(Border));
            shape.Name = "HandleShape";
            shape.SetValue(Border.CornerRadiusProperty, new CornerRadius(8, 0, 0, 8));
            shape.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 0, 1));
            shape.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            shape.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            shape.AppendChild(content);

            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = shape };
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, ColorBrush("#3B322A"), "HandleShape"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#9E7446"), "HandleShape"));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#DBA457"), "HandleShape"));
            template.Triggers.Add(focus);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, ColorBrush("#4F3E2D"), "HandleShape"));
            template.Triggers.Add(pressed);

            return new Button
            {
                Content = symbols,
                Template = template,
                Background = new LinearGradientBrush(Color.FromRgb(51, 45, 39), Color.FromRgb(35, 32, 29), 75),
                BorderBrush = ColorBrush("#57483B"),
                Cursor = Cursors.Hand
            };
        }

        private static Brush ColorBrush(string value)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }

        private void UpdatePin()
        {
            drawerLeft = -1;
            if (trayMenu.Visible)
                trayMenu.SetState(expanded, pinned);
            if (pinButton == null)
                return;

            string description = pinned ? "取消固定记下" : "固定记下";
            pinButton.Tag = pinned;
            pinButton.ToolTip = description;
            AutomationProperties.SetName(pinButton, description);
        }

        private void TogglePin()
        {
            thoughtCollapseTimer.Stop();
            pinned = !pinned;
            if (pinned && !expanded)
                Open(false, false);
            UpdatePin();
        }

        private void UpdateUrgency(object sender, EventArgs args)
        {
            int total = board.UrgentTotal;
            string count = total.ToString(CultureInfo.CurrentCulture);
            urgentLabel.Text = total > 99 ? "99+" : count;
            urgentLabel.Foreground = ColorBrush(total > 0 ? "#F3D3A1" : "#B1A49A");
            urgentDot.Fill = ColorBrush(total > 0 ? "#DBA457" : "#6B563F");
            string description = "紧急 " + count + " 项";
            AutomationProperties.SetName(handleButton, "展开记下，" + description);
            tray.Text = "记下 · " + description;
        }

        private void Open(bool activate, bool useKeyboard)
        {
            if (!started || disposed || quitting)
                return;

            thoughtCollapseTimer.Stop();
            RememberForeground();
            drawerLeft = -1;
            if (!expanded)
            {
                expanded = true;
                if (trayMenu.Visible)
                    trayMenu.SetState(expanded, pinned);
                drawerSurface.IsHitTestVisible = true;
                if (!drawer.IsVisible)
                {
                    slide.X = drawer.Width;
                    drawerSurface.Opacity = ClosedOpacity;
                    drawer.Show();
                }
                AnimateSlide(0, true);
            }

            if (!activate)
                return;

            if (useKeyboard)
            {
                keyboardMode = true;
                ReadPointer(out keyboardPointer);
                keepOpenUntil = clock.ElapsedMilliseconds + 1800;
            }

            drawer.Activate();
            if (useKeyboard && !drawer.IsKeyboardFocusWithin && pinButton != null)
                pinButton.Focus();
        }

        private void Collapse(bool explicitAction, bool requireHandleLeave = false)
        {
            if (!started || disposed || !expanded)
                return;

            thoughtCollapseTimer.Stop();
            previousForeground = IntPtr.Zero;
            previousForegroundProcess = 0;
            expanded = false;
            keyboardMode = false;
            keepOpenUntil = 0;
            handleEntered = -1;
            hoverRequiresLeave = explicitAction || requireHandleLeave;
            if (explicitAction)
            {
                journal.FinishEditing();
                board.FinishEditing();
                pinned = false;
                UpdatePin();
            }

            drawerSurface.IsHitTestVisible = false;
            AnimateSlide(drawer.Width, false);
        }

        private void AnimateSlide(double destination, bool opening)
        {
            double from = slide.X;
            double fromOpacity = drawerSurface.Opacity;
            double toOpacity = opening ? 1 : ClosedOpacity;
            int version = ++slideVersion;
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            drawerSurface.BeginAnimation(UIElement.OpacityProperty, null);
            slide.X = destination;
            drawerSurface.Opacity = toOpacity;
            double distance = Math.Abs(from - destination);
            if (!SystemParameters.ClientAreaAnimation || distance < 0.5)
            {
                if (!opening)
                    drawer.Hide();
                return;
            }

            double fraction = Math.Min(1, distance / Math.Max(1, drawer.Width));
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(40, fraction * (opening ? 205 : 185)));
            CubicEase easing = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };
            DoubleAnimation animation = new DoubleAnimation(from, destination, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate
            {
                if (!disposed && version == slideVersion && !expanded)
                    drawer.Hide();
            };
            slide.BeginAnimation(TranslateTransform.XProperty, animation);
            drawerSurface.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(fromOpacity, toOpacity, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
        }

        private void DrawerKeyDown(object sender, KeyEventArgs args)
        {
            thoughtCollapseTimer.Stop();
            if (args.Key == Key.Escape && journal.HandleEscape())
            {
                args.Handled = true;
                return;
            }
            if (args.Key == Key.Escape && !board.IsInteracting && !journal.IsInteracting)
            {
                args.Handled = true;
                Collapse(true);
                return;
            }

            keyboardMode = true;
            ReadPointer(out keyboardPointer);
        }

        private void DrawerMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
        {
            Point position;
            if (keyboardMode && ReadPointer(out position) && position != keyboardPointer)
                keyboardMode = false;
        }

        private void ObservePointer(object sender, EventArgs args)
        {
            if (trayMenu.Visible)
            {
                handleEntered = -1;
                drawerLeft = -1;
                return;
            }

            long now = clock.ElapsedMilliseconds;
            Point position;
            if (!expanded)
            {
                if (hoverRequiresLeave)
                {
                    if (ReadPointer(out position) && !PointerWithin(handleButton, position))
                        hoverRequiresLeave = false;
                    return;
                }

                if (!handleButton.IsMouseOver)
                {
                    handleEntered = -1;
                }
                else if (handleEntered < 0)
                {
                    handleEntered = now;
                }
                else if (handleEntered >= 0 && now - handleEntered >= OpenDelay)
                {
                    Open(false, false);
                }
                return;
            }

            if (ReadPointer(out position) && (handleButton.IsMouseOver || PointerWithin(drawerContent, position)))
            {
                keepOpenUntil = 0;
                drawerLeft = -1;
                return;
            }

            if (pinned || board.IsInteracting || journal.IsInteracting || thoughtCollapseTimer.IsEnabled ||
                now < keepOpenUntil || (keyboardMode && drawer.IsActive))
                drawerLeft = -1;
            else if (drawerLeft < 0)
                drawerLeft = now;
            else if (now - drawerLeft >= CloseDelay)
                Collapse(false);
        }

        private static bool PointerWithin(FrameworkElement element, Point position)
        {
            if (PresentationSource.FromVisual(element) == null)
                return false;

            Point local = element.PointFromScreen(position);
            return local.X >= 0 && local.X < element.ActualWidth &&
                local.Y >= 0 && local.Y < element.ActualHeight;
        }

        private void PositionWindows()
        {
            Rect area = SystemParameters.WorkArea;
            HwndSource source = PresentationSource.FromVisual(handle) as HwndSource;
            NativeRect bounds;
            if (source != null && source.CompositionTarget != null &&
                SystemParametersInfo(GetWorkArea, 0, out bounds, 0))
            {
                Matrix dip = source.CompositionTarget.TransformFromDevice;
                area = new Rect(dip.Transform(new Point(bounds.Left, bounds.Top)),
                    dip.Transform(new Point(bounds.Right, bounds.Bottom)));
            }

            handle.Width = Math.Min(HandleWidth, area.Width);
            handle.Height = Math.Min(HandleHeight, area.Height);
            handle.Left = area.Right - handle.Width;
            handle.Top = area.Top + (area.Height - handle.Height) / 2;

            double contentHeight = Math.Max(MinimumDrawerHeight, Math.Min(DrawerHeight, area.Height));
            framedPanel.Height = contentHeight;
            double scale = Math.Min(1, Math.Min(area.Width / DrawerWidth, area.Height / contentHeight));
            double width = DrawerWidth * scale;
            double height = contentHeight * scale;
            bool resized = drawer.Width != width || drawer.Height != height;
            drawer.Width = width;
            drawer.Height = height;
            drawer.Left = area.Right - width;
            drawer.Top = area.Top + (area.Height - height) / 2;

            if (!resized)
                return;

            if (drawer.IsVisible)
                AnimateSlide(expanded ? 0 : width, expanded);
            else
                slide.X = width;
        }

        private void ObserveWindowSource(object sender, EventArgs args)
        {
            Window window = (Window)sender;
            HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source.AddHook(ObserveWindowMessage);
        }

        private IntPtr ObserveWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == MouseActivate && !disposed && !quitting)
                RememberForeground();

            if (!layoutQueued && !disposed &&
                (message == SettingsChanged || message == DisplayChanged || message == DpiChanged))
            {
                layoutQueued = true;
                handle.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    layoutQueued = false;
                    if (!disposed)
                        PositionWindows();
                }));
            }
            return IntPtr.Zero;
        }

        private bool RecordFirstLaunch()
        {
            try
            {
                Directory.CreateDirectory(storageDirectory);
                string marker = System.IO.Path.Combine(storageDirectory, "introduced");
                using (File.Open(marker, FileMode.CreateNew, FileAccess.Write)) { }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void OnThoughtCaptured(object sender, EventArgs args)
        {
            if (!CanCollapseAfterThought())
                return;

            drawerLeft = -1;
            thoughtCollapseTimer.Stop();
            thoughtCollapseTimer.Start();
        }

        private bool CanCollapseAfterThought()
        {
            return started && expanded && !disposed && !quitting && !pinned &&
                !journal.IsReviewOpen && !journal.HasUnstoredDraft && !board.IsInteracting;
        }

        private void CollapseAfterThought(object sender, EventArgs args)
        {
            thoughtCollapseTimer.Stop();
            if (!CanCollapseAfterThought())
                return;

            RestorePreviousForeground();
            Collapse(false, true);
        }

        private void RememberForeground()
        {
            IntPtr current = GetForegroundWindow();
            uint process;
            if (current != IntPtr.Zero && GetWindowThreadProcessId(current, out process) != 0 && process != ProcessId)
            {
                previousForeground = current;
                previousForegroundProcess = process;
            }
        }

        private void RestorePreviousForeground()
        {
            IntPtr previous = previousForeground;
            uint process;
            if (previous == IntPtr.Zero || GetWindowThreadProcessId(previous, out process) == 0 ||
                process != previousForegroundProcess || !IsWindowVisible(previous) || IsIconic(previous))
                return;

            IntPtr current = GetForegroundWindow();
            if (current != IntPtr.Zero && (current == new WindowInteropHelper(drawer).Handle ||
                current == new WindowInteropHelper(handle).Handle))
                SetForegroundWindow(previous);
        }

        private void SessionEnding(object sender, SessionEndingCancelEventArgs args)
        {
            journal.FinishEditing();
            board.FinishEditing();
            thoughtCollapseTimer.Stop();
        }

        private void Quit()
        {
            quitting = true;
            thoughtCollapseTimer.Stop();
            journal.FinishEditing();
            if (journal.HasUnstoredDraft)
            {
                quitting = false;
                Open(true, true);
                thoughtEntry.Focus();
                return;
            }
            board.FinishEditing();
            Application.Current.Shutdown();
        }

        private void WindowClosing(object sender, CancelEventArgs args)
        {
            if (quitting || disposed || handle.Dispatcher.HasShutdownStarted)
                return;

            args.Cancel = true;
            thoughtCollapseTimer.Stop();
            if (expanded)
                handle.Dispatcher.BeginInvoke(new Action(delegate { Collapse(true); }));
        }

        private void ApplicationExit(object sender, ExitEventArgs args)
        {
            disposed = true;
            journal.Captured -= OnThoughtCaptured;
            Application.Current.SessionEnding -= SessionEnding;
            board.Changed -= UpdateUrgency;
            pointerTimer.Stop();
            thoughtCollapseTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            trayIcon.Dispose();
        }

        private static bool ReadPointer(out Point position)
        {
            NativePoint native;
            bool available = GetCursorPos(out native);
            position = new Point(native.X, native.Y);
            return available;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(int action, int parameter, out NativeRect value, int flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
