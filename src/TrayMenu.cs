using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SidePage
{
    internal sealed class TrayMenu : ContextMenuStrip
    {
        private const int MenuWidth = 166;
        private const int DefaultDpi = 96;
        private const int DpiChanged = 0x02E0;
        private static readonly Color Surface = Color.FromArgb(39, 35, 31);
        private static readonly Color Outline = Color.FromArgb(89, 77, 65);
        private static readonly Color TextInk = Color.FromArgb(239, 230, 219);
        private static readonly Color IconInk = Color.FromArgb(180, 162, 144);
        private static readonly Color Amber = Color.FromArgb(222, 170, 103);
        private static readonly Color DisabledInk = Color.FromArgb(131, 119, 106);
        private static readonly Color SelectedFill = Color.FromArgb(60, 50, 41);
        private static readonly Color SelectedEdge = Color.FromArgb(96, 77, 57);
        private static readonly Color ExitFill = Color.FromArgb(65, 48, 41);
        private static readonly Color ExitEdge = Color.FromArgb(102, 73, 60);
        private static readonly Color DividerInk = Color.FromArgb(74, 63, 53);

        private readonly MenuItem panelItem;
        private readonly MenuItem pinItem;
        private readonly MenuItem exitItem;
        private readonly TrayRenderer trayRenderer;
        private int layoutDpi = DefaultDpi;
        private Font menuFont;

        public TrayMenu(Action togglePanel, Action togglePin, Action quit)
        {
            trayRenderer = new TrayRenderer(this);
            panelItem = new MenuItem("打开记下", Symbol.Open, togglePanel);
            pinItem = new MenuItem("固定面板", Symbol.Pin, togglePin);
            exitItem = new MenuItem("退出记下", Symbol.Exit, quit);

            SuspendLayout();
            Items.Add(panelItem);
            Items.Add(pinItem);
            Items.Add(new Divider());
            Items.Add(exitItem);
            ApplyMetrics(DeviceDpi);
            ApplyAppearance();
            ResumeLayout();
        }

        public void SetState(bool expanded, bool pinned)
        {
            panelItem.Text = expanded ? "收起记下" : "打开记下";
            panelItem.Icon = expanded ? Symbol.Close : Symbol.Open;
            pinItem.Checked = pinned;
            Invalidate();
        }

        protected override void OnOpening(CancelEventArgs e)
        {
            ApplyAppearance();
            base.OnOpening(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi = DeviceDpi;
            try { dpi = (int)GetDpiForWindow(Handle); }
            catch (EntryPointNotFoundException) { }
            ApplyMetrics(dpi);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == DpiChanged)
                ApplyMetrics((int)(message.WParam.ToInt64() & 0xffff));
            base.WndProc(ref message);
        }

        private void ApplyMetrics(int dpi)
        {
            if (dpi <= 0 || (dpi == layoutDpi && menuFont != null))
                return;

            SuspendLayout();
            layoutDpi = dpi;
            MinimumSize = new Size(Scale(MenuWidth), 0);
            panelItem.Margin = new Padding(0, Scale(4), 0, 0);
            exitItem.Margin = new Padding(0, 0, 0, Scale(4));
            Font previousFont = menuFont;
            menuFont = new Font("Microsoft YaHei UI", (40f / 3) * dpi / DefaultDpi, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = SystemInformation.HighContrast ? SystemFonts.MenuFont : menuFont;
            if (previousFont != null)
                previousFont.Dispose();
            ResumeLayout();
        }

        private void ApplyAppearance()
        {
            bool system = SystemInformation.HighContrast;
            ShowImageMargin = !system;
            ShowCheckMargin = system;
            BackColor = system ? SystemColors.Menu : Surface;
            ForeColor = system ? SystemColors.MenuText : TextInk;
            Font = system ? SystemFonts.MenuFont : menuFont;
            if (system)
                RenderMode = ToolStripRenderMode.System;
            else if (Renderer != trayRenderer)
                Renderer = trayRenderer;
        }

        private int Scale(int value)
        {
            return (int)Math.Round(value * (double)layoutDpi / DefaultDpi);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && menuFont != null)
                menuFont.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private enum Symbol { Open, Close, Pin, Exit }

        private sealed class MenuItem : ToolStripMenuItem
        {
            public Symbol Icon;

            public MenuItem(string caption, Symbol icon, Action action) : base(caption)
            {
                Icon = icon;
                Click += delegate { action(); };
            }

            public override Size GetPreferredSize(Size constrainingSize)
            {
                Size size = base.GetPreferredSize(constrainingSize);
                TrayMenu owner = Owner as TrayMenu;
                if (owner == null)
                    return size;
                return new Size(Math.Max(size.Width, owner.Scale(MenuWidth)), Math.Max(size.Height, owner.Scale(38)));
            }
        }

        private sealed class Divider : ToolStripSeparator
        {
            public override Size GetPreferredSize(Size constrainingSize)
            {
                Size size = base.GetPreferredSize(constrainingSize);
                TrayMenu owner = Owner as TrayMenu;
                if (owner != null)
                    size.Height = owner.Scale(11);
                return size;
            }
        }

        private sealed class TrayRenderer : ToolStripRenderer
        {
            private readonly TrayMenu owner;

            public TrayRenderer(TrayMenu owner)
            {
                this.owner = owner;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(Surface))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Rectangle bounds = e.ToolStrip.ClientRectangle;
                using (var pen = new Pen(Outline))
                    e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                MenuItem item = (MenuItem)e.Item;
                bool selected = item.Enabled && (item.Selected || item.Pressed);
                GraphicsState saved = e.Graphics.Save();
                try
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (selected)
                    {
                        int x = owner.Scale(6);
                        int y = owner.Scale(2);
                        var bounds = new Rectangle(x, y, item.Width - x * 2 - 1, item.Height - y * 2 - 1);
                        using (GraphicsPath shape = RoundedRect(bounds, owner.Scale(7)))
                        using (var brush = new SolidBrush(item.Icon == Symbol.Exit ? ExitFill : SelectedFill))
                        using (var pen = new Pen(item.Icon == Symbol.Exit ? ExitEdge : SelectedEdge))
                        {
                            e.Graphics.FillPath(brush, shape);
                            e.Graphics.DrawPath(pen, shape);
                        }
                    }

                    Color ink = item.Enabled ? (item.Checked || selected ? Amber : IconInk) : DisabledInk;
                    float scale = (float)owner.layoutDpi / DefaultDpi;
                    if (item.Checked)
                    {
                        float x = item.Width - owner.Scale(18);
                        float y = item.Height / 2f;
                        using (var pen = new Pen(ink, 1.3f * scale)
                        {
                            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
                        })
                            e.Graphics.DrawLines(pen, new[] { new PointF(x - 8 * scale, y),
                                new PointF(x - 5 * scale, y + 2.6f * scale), new PointF(x, y - 3 * scale) });
                    }
                    e.Graphics.TranslateTransform(owner.Scale(15), (item.Height - 16 * scale) / 2f);
                    e.Graphics.ScaleTransform(scale, scale);
                    PaintSymbol(e.Graphics, item.Icon, item.Checked, ink);
                }
                finally { e.Graphics.Restore(saved); }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                int left = owner.Scale(43);
                e.TextRectangle = new Rectangle(left, 0, Math.Max(0, e.Item.Width - left - owner.Scale(12)), e.Item.Height);
                e.TextColor = e.Item.Enabled ? TextInk : DisabledInk;
                e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // The pin glyph already reflects the menu item's native checked state.
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int inset = owner.Scale(11);
                int y = e.Item.Height / 2;
                using (var pen = new Pen(DividerInk))
                    e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
            }

            private static GraphicsPath RoundedRect(Rectangle rect, int radius)
            {
                int diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
                GraphicsPath path = new GraphicsPath();
                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }

            private static void PaintSymbol(Graphics canvas, Symbol icon, bool isChecked, Color color)
            {
                using (var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    switch (icon)
                    {
                        case Symbol.Open:
                            canvas.DrawLine(pen, 13, 2, 13, 14);
                            canvas.DrawLine(pen, 10, 8, 3, 8);
                            canvas.DrawLines(pen, new[] { new PointF(6, 5), new PointF(3, 8), new PointF(6, 11) });
                            break;
                        case Symbol.Close:
                            canvas.DrawLine(pen, 13, 2, 13, 14);
                            canvas.DrawLine(pen, 3, 8, 10, 8);
                            canvas.DrawLines(pen, new[] { new PointF(7, 5), new PointF(10, 8), new PointF(7, 11) });
                            break;
                        case Symbol.Pin:
                            PointF[] pin = { new PointF(8, 2), new PointF(13, 7), new PointF(10.4f, 8),
                                new PointF(9.4f, 12), new PointF(4.4f, 7), new PointF(8.2f, 6) };
                            if (isChecked)
                            {
                                using (var fill = new SolidBrush(Color.FromArgb(95, color)))
                                    canvas.FillPolygon(fill, pin);
                            }
                            canvas.DrawPolygon(pen, pin);
                            canvas.DrawLine(pen, 6.9f, 9.6f, 3, 13.5f);
                            break;
                        case Symbol.Exit:
                            canvas.DrawLines(pen, new[] { new PointF(8, 2), new PointF(3, 2), new PointF(3, 14), new PointF(8, 14) });
                            canvas.DrawLine(pen, 7, 8, 13, 8);
                            canvas.DrawLines(pen, new[] { new PointF(10.5f, 5.5f), new PointF(13, 8), new PointF(10.5f, 10.5f) });
                            break;
                    }
                }
            }
        }
    }
}
