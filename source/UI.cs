// Look and building blocks: colours, switches, rows, cards and buttons.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuietPC
{
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static readonly Color Back = Color.FromArgb(32, 32, 32);
        public static readonly Color Side = Color.FromArgb(26, 26, 26);
        public static readonly Color Card = Color.FromArgb(43, 43, 43);
        public static readonly Color CardHover = Color.FromArgb(52, 52, 52);
        public static readonly Color Line = Color.FromArgb(60, 60, 60);
        public static readonly Color Text = Color.FromArgb(242, 242, 242);
        public static readonly Color Dim = Color.FromArgb(165, 165, 165);
        public static readonly Color Accent = Color.FromArgb(20, 184, 166);
        public static readonly Color AccentHover = Color.FromArgb(45, 205, 186);
        public static readonly Color Warn = Color.FromArgb(245, 180, 80);
        public static readonly Color Good = Color.FromArgb(110, 220, 150);

        public static readonly Font Body = new Font("Segoe UI", 10f);
        public static readonly Font Small = new Font("Segoe UI", 9f);
        public static readonly Font RowTitle = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font CardTitle = new Font("Segoe UI Semibold", 12f);
        public static readonly Font PageTitle = new Font("Segoe UI Semibold", 20f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 16f);
        public static readonly Font Icons = new Font(IconFamily(), 12f);
        public static readonly Font BigIcons = new Font(IconFamily(), 18f);

        static string IconFamily()
        {
            foreach (FontFamily f in FontFamily.Families)
                if (f.Name == "Segoe Fluent Icons") return f.Name;
            return "Segoe MDL2 Assets";
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Max(1, radius * 2);
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static int TextHeight(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c, Color back)
        {
            TextRenderer.DrawText(g, text, font, r, c, back, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top);
        }

        // The QuietPC icon: a white crescent moon on a teal circle.
        public static Icon MakeIcon(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Accent)) g.FillEllipse(b, 0, 0, size - 1, size - 1);
                    using (GraphicsPath moon = new GraphicsPath())
                    using (GraphicsPath cut = new GraphicsPath())
                    {
                        float r = size * 0.30f;
                        moon.AddEllipse(size * 0.5f - r, size * 0.5f - r, r * 2, r * 2);
                        cut.AddEllipse(size * 0.5f - r + r * 0.55f, size * 0.5f - r - r * 0.35f, r * 2, r * 2);
                        using (Region reg = new Region(moon))
                        {
                            reg.Exclude(cut);
                            g.FillRegion(Brushes.White, reg);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }
    }

    // On/off switch like the ones in Windows Settings.
    class ToggleSwitch : Control
    {
        bool on, busy, hover;
        public event Action<bool> Toggled; // asks to switch to this state

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.S(44), Theme.S(22));
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool On { get { return on; } set { on = value; Invalidate(); } }
        public bool Busy { get { return busy; } set { busy = value; Cursor = value ? Cursors.WaitCursor : Cursors.Hand; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color fill = busy ? Theme.Line : (on ? (hover ? Theme.AccentHover : Theme.Accent) : Theme.Card);
            using (GraphicsPath p = Theme.Rounded(r, r.Height / 2))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (!on || busy) using (Pen pen = new Pen(Theme.Dim, 1.2f)) g.DrawPath(pen, p);
            }
            float d = Height - Theme.S(10);
            float x = on ? Width - d - Theme.S(6) : Theme.S(5);
            using (SolidBrush knob = new SolidBrush(on && !busy ? Color.Black : Theme.Dim))
                g.FillEllipse(knob, x, (Height - d) / 2f - 0.5f, d, d);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!busy && Enabled && Toggled != null) Toggled(!on);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && !busy && Toggled != null) Toggled(!on);
            base.OnKeyDown(e);
        }
    }

    class FlatBtn : Button
    {
        public FlatBtn(string text, bool primary)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = primary ? 0 : 1;
            FlatAppearance.BorderColor = Theme.Line;
            BackColor = primary ? Theme.Accent : Theme.Card;
            ForeColor = primary ? Color.Black : Theme.Text;
            FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.CardHover;
            Font = primary ? Theme.RowTitle : Theme.Body;
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowOnly;
            Padding = new Padding(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(4));
            MinimumSize = new Size(0, Theme.S(34));
            UseVisualStyleBackColor = false;
        }
    }

    // A rounded card with a title, a description, an optional warning and a control on the right.
    class Row : Control
    {
        public string Title = "", Description = "", Warning = "", Status = "";
        public Color StatusColor = Theme.Good;
        Control right;

        public Row()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Margin = new Padding(0, 0, 0, Theme.S(6));
        }

        public Control Trailing
        {
            get { return right; }
            set
            {
                if (right != null) Controls.Remove(right);
                right = value;
                if (right != null) { right.BackColor = Theme.Card; Controls.Add(right); }
                Fit();
            }
        }

        int Pad { get { return Theme.S(16); } }

        int TextWidth
        {
            get { return Math.Max(Theme.S(100), Width - Pad * 3 - (right != null ? right.Width : 0)); }
        }

        public void Fit()
        {
            int w = TextWidth;
            int h = Pad + Theme.TextHeight(Title, Theme.RowTitle, w);
            if (Description.Length > 0) h += Theme.S(3) + Theme.TextHeight(Description, Theme.Small, w);
            if (Warning.Length > 0) h += Theme.S(4) + Theme.TextHeight("⚠ " + Warning, Theme.Small, w);
            if (Status.Length > 0) h += Theme.S(4) + Theme.TextHeight(Status, Theme.Small, w);
            h += Pad;
            if (right != null) h = Math.Max(h, right.Height + Pad * 2);
            if (Height != h) Height = h;
            if (right != null) right.Location = new Point(Width - Pad - right.Width, (Height - right.Height) / 2);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Fit();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(8)))
            using (SolidBrush b = new SolidBrush(Theme.Card))
                g.FillPath(b, p);

            int w = TextWidth, x = Pad, y = Pad;
            int h = Theme.TextHeight(Title, Theme.RowTitle, w);
            Theme.DrawText(g, Title, Theme.RowTitle, new Rectangle(x, y, w, h), Theme.Text, Theme.Card);
            y += h;
            if (Description.Length > 0)
            {
                y += Theme.S(3);
                h = Theme.TextHeight(Description, Theme.Small, w);
                Theme.DrawText(g, Description, Theme.Small, new Rectangle(x, y, w, h), Theme.Dim, Theme.Card);
                y += h;
            }
            if (Warning.Length > 0)
            {
                y += Theme.S(4);
                h = Theme.TextHeight("⚠ " + Warning, Theme.Small, w);
                Theme.DrawText(g, "⚠ " + Warning, Theme.Small, new Rectangle(x, y, w, h), Theme.Warn, Theme.Card);
                y += h;
            }
            if (Status.Length > 0)
            {
                y += Theme.S(4);
                h = Theme.TextHeight(Status, Theme.Small, w);
                Theme.DrawText(g, Status, Theme.Small, new Rectangle(x, y, w, h), StatusColor, Theme.Card);
            }
        }
    }

    // A vertical list that keeps every child as wide as itself.
    class Stack : FlowLayoutPanel
    {
        public Stack()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoScroll = true;
            BackColor = Theme.Back;
            Padding = new Padding(Theme.S(28), Theme.S(20), Theme.S(28), Theme.S(28));
            DoubleBuffered = true;
        }

        int ChildWidth
        {
            get
            {
                int scroll = VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
                return Math.Max(Theme.S(200), ClientSize.Width - Padding.Horizontal - scroll - 2);
            }
        }

        void FitChild(Control c)
        {
            int w = ChildWidth;
            if (c is ButtonBar || c is Label) c.MaximumSize = new Size(w, 0);
            else if (!(c is FlatBtn)) c.Width = w;
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            FitChild(e.Control);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            SuspendLayout();
            foreach (Control c in Controls) FitChild(c);
            ResumeLayout();
        }

        public Label Heading(string text, Font font = null, Color? color = null, int topGap = 0)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font ?? Theme.CardTitle;
            l.ForeColor = color ?? Theme.Text;
            l.AutoSize = true;
            l.MaximumSize = new Size(ChildWidth, 0);
            l.Margin = new Padding(0, topGap, 0, Theme.S(8));
            Controls.Add(l);
            return l;
        }

        public Label Note(string text, int bottom = 14)
        {
            Label l = Heading(text, Theme.Body, Theme.Dim);
            l.Margin = new Padding(0, 0, 0, Theme.S(bottom));
            return l;
        }
    }

    // Buttons in a row.
    class ButtonBar : FlowLayoutPanel
    {
        public ButtonBar()
        {
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.Back;
            Margin = new Padding(0, Theme.S(4), 0, Theme.S(14));
        }

        public FlatBtn Add(string text, bool primary, EventHandler click)
        {
            FlatBtn b = new FlatBtn(text, primary);
            b.Margin = new Padding(0, 0, Theme.S(8), Theme.S(4));
            b.Click += click;
            Controls.Add(b);
            return b;
        }
    }

    // Left-side menu button.
    class NavButton : Control
    {
        public string Glyph;
        public bool Selected;
        bool hover;

        public NavButton(string glyph, string text)
        {
            Glyph = glyph;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = Theme.S(40);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 0, Theme.S(2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = Selected ? Theme.Card : (hover ? Color.FromArgb(36, 36, 36) : Theme.Side);
            using (GraphicsPath p = Theme.Rounded(new RectangleF(Theme.S(6), 1, Width - Theme.S(12), Height - 2), Theme.S(6)))
            using (SolidBrush b = new SolidBrush(back))
                g.FillPath(b, p);
            if (Selected)
                using (GraphicsPath bar = Theme.Rounded(new RectangleF(Theme.S(6), Height * 0.3f, Theme.S(3), Height * 0.4f), Theme.S(1.5f)))
                using (SolidBrush a = new SolidBrush(Theme.Accent))
                    g.FillPath(a, bar);
            TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(Theme.S(18), 0, Theme.S(24), Height), Theme.Text, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(Theme.S(50), 0, Width - Theme.S(54), Height), Theme.Text, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    static class Dialogs
    {
        public static DialogResult Ask(IWin32Window owner, string text, string title = "QuietPC")
        {
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        }

        public static void Info(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "QuietPC", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Problem(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "QuietPC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static void ShowText(IWin32Window owner, string title, string text)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.BackColor = Theme.Back;
                f.ForeColor = Theme.Text;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(Theme.S(640), Theme.S(440));
                f.ShowInTaskbar = false;
                f.MinimizeBox = false;
                TextBox t = new TextBox();
                t.Multiline = true;
                t.ReadOnly = true;
                t.ScrollBars = ScrollBars.Vertical;
                t.Dock = DockStyle.Fill;
                t.BackColor = Theme.Card;
                t.ForeColor = Theme.Text;
                t.BorderStyle = BorderStyle.None;
                t.Font = new Font("Consolas", 10f);
                t.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                f.Controls.Add(t);
                f.HandleCreated += delegate { Native.DarkTitleBar(f.Handle); };
                f.ShowDialog(owner);
            }
        }
    }
}
