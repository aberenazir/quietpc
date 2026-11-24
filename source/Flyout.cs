// The quick panel that pops up from the moon icon by the clock, styled like Windows Quick Settings.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuietPC
{
    class Tile : Control
    {
        public string Glyph = "", Caption = "", Sub = "";
        public bool On, Busy;
        bool hover;

        public Tile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = On ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.CardHover : Theme.Card);
            if (Busy) back = Theme.Line;
            Color fore = On && !Busy ? Color.Black : Theme.Text;
            Color dim = On && !Busy ? Color.FromArgb(30, 60, 55) : Theme.Dim;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(8)))
            using (SolidBrush b = new SolidBrush(back))
                g.FillPath(b, p);
            int pad = Theme.S(12);
            TextRenderer.DrawText(g, Glyph, Theme.BigIcons, new Rectangle(pad, pad, Theme.S(30), Theme.S(30)), fore, back,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Caption, Theme.RowTitle, new Rectangle(pad, Height - pad - Theme.S(40), Width - pad * 2, Theme.S(22)), fore, back,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Busy ? "Working..." : Sub, Theme.Small, new Rectangle(pad, Height - pad - Theme.S(18), Width - pad * 2, Theme.S(18)), dim, back,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    class Flyout : Form
    {
        readonly QuietApp app;
        readonly Tile quiet = new Tile { Glyph = "", Caption = "Quiet mode" };
        readonly Tile updates = new Tile { Glyph = "", Caption = "Pause updates" };
        readonly Tile calm = new Tile { Glyph = "", Caption = "Calm my PC" };
        readonly Tile clean = new Tile { Glyph = "", Caption = "Clean junk files" };
        readonly Label busy = new Label();
        readonly Label open = new Label();
        readonly ProcessSampler sampler = new ProcessSampler();
        readonly Timer timer = new Timer();
        bool sampling;
        public DateTime HiddenAt = DateTime.MinValue;

        public Flyout(QuietApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            Font = Theme.Body;
            int pad = Theme.S(14), tileW = Theme.S(160), tileH = Theme.S(104), gap = Theme.S(10);
            ClientSize = new Size(pad * 2 + tileW * 2 + gap, Theme.S(380));

            Label title = new Label();
            title.Text = "QuietPC";
            title.Font = Theme.CardTitle;
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.Location = new Point(pad, pad);
            Controls.Add(title);

            int y = pad + Theme.S(36);
            Tile[] tiles = { quiet, updates, calm, clean };
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].Bounds = new Rectangle(pad + (i % 2) * (tileW + gap), y + (i / 2) * (tileH + gap), tileW, tileH);
                Controls.Add(tiles[i]);
            }
            y += tileH * 2 + gap + Theme.S(16);

            busy.Location = new Point(pad, y);
            busy.Size = new Size(ClientSize.Width - pad * 2, Theme.S(86));
            busy.ForeColor = Theme.Dim;
            busy.Font = Theme.Small;
            busy.Text = "Busy right now: measuring...";
            Controls.Add(busy);
            y += Theme.S(92);

            open.Text = "Open QuietPC  ›";
            open.ForeColor = Theme.Accent;
            open.Font = Theme.RowTitle;
            open.AutoSize = true;
            open.Cursor = Cursors.Hand;
            open.Location = new Point(pad, y);
            open.Click += delegate { Hide(); app.OpenMain(null); };
            Controls.Add(open);
            ClientSize = new Size(ClientSize.Width, y + Theme.S(36));

            quiet.Click += async delegate { await ToggleQuiet(); };
            updates.Click += async delegate { await TogglePause(); };
            calm.Click += async delegate { await CalmNow(); };
            clean.Click += async delegate { await CleanFiles(); };

            timer.Interval = 2000;
            timer.Tick += async delegate { await SampleBusy(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no taskbar button
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // Opens just above the clock (or wherever the taskbar is).
        public void ShowNearTray()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle wa = screen.WorkingArea, b = screen.Bounds;
            int margin = Theme.S(12);
            int x = wa.Right - Width - margin, y = wa.Bottom - Height - margin;
            if (wa.Top > b.Top) y = wa.Top + margin;                     // taskbar at the top
            if (wa.Left > b.Left) x = wa.Left + margin;                  // taskbar on the left
            Location = new Point(x, y);
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            RefreshTiles();
            timer.Start();
            _ = SampleBusy();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible && !anyBusy) { HiddenAt = DateTime.UtcNow; Hide(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) timer.Stop();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        bool anyBusy;

        public async void RefreshTiles()
        {
            bool q = false, qPlug = false, calmOn = false;
            DateTime? until = null;
            try
            {
                await Task.Run(delegate
                {
                    q = Catalog.QuietBattery.IsOn();
                    Tweak plug = Catalog.Find("quiet-plugged");
                    qPlug = plug != null && plug.IsOn();
                    until = PauseUpdatesTweak.PausedUntil();
                    calmOn = Engine.IsCalm;
                });
            }
            catch (Exception ex) { Log.Write("flyout refresh: " + ex.Message); }
            if (IsDisposed) return;
            quiet.On = q;
            quiet.Sub = q ? (qPlug ? "On (also when plugged in)" : "On when on battery") : "Off";
            updates.On = until != null;
            updates.Sub = until != null ? "Until " + until.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Off: Windows decides";
            calm.On = calmOn;
            calm.Sub = calmOn ? "Done ✔" : "Everything at once";
            if (clean.Sub.Length == 0) clean.Sub = "Temp and leftover files";
            foreach (Tile t in new[] { quiet, updates, calm, clean }) t.Invalidate();
        }

        async Task Run(Tile tile, Action work)
        {
            if (tile.Busy) return;
            tile.Busy = true;
            anyBusy = true;
            tile.Invalidate();
            try { await Task.Run(work); }
            catch (Exception ex)
            {
                Log.Write("tile " + tile.Caption + ": " + ex);
                MessageBox.Show(this, "That didn't work:\n\n" + ex.Message, "QuietPC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                anyBusy = false;
                if (!IsDisposed) { tile.Busy = false; tile.Invalidate(); }
            }
            if (!IsDisposed) RefreshTiles();
            app.RefreshMainSoon();
        }

        Task ToggleQuiet()
        {
            bool turnOn = !quiet.On;
            return Run(quiet, delegate
            {
                if (turnOn) Engine.Apply(Catalog.QuietBattery);
                else Engine.Revert(Catalog.QuietBattery);
            });
        }

        Task TogglePause()
        {
            bool turnOn = !updates.On;
            return Run(updates, delegate
            {
                if (turnOn) Engine.Apply(Catalog.PauseUpdates);
                else Engine.Revert(Catalog.PauseUpdates);
            });
        }

        async Task CalmNow()
        {
            List<string> problems = null;
            await Run(calm, delegate { problems = Engine.CalmNow(null); });
            if (problems != null && problems.Count > 0 && !IsDisposed)
                MessageBox.Show(this, "Done. A few things couldn't be changed on this PC:\n\n• " + string.Join("\n• ", problems.ToArray()),
                    "QuietPC", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async Task CleanFiles()
        {
            anyBusy = true; // keep the panel open while the question shows
            DialogResult ok = MessageBox.Show(this, "Delete temporary and leftover files now?\n\nYour own files, photos and downloads are never touched.",
                "QuietPC", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            anyBusy = false;
            if (ok != DialogResult.Yes) return;
            long freed = 0;
            await Run(clean, delegate
            {
                foreach (CleanArea a in Cleaner.Areas()) freed += Cleaner.Clean(a);
            });
            if (!IsDisposed) { clean.Sub = "Freed " + Cleaner.Size(freed); clean.Invalidate(); }
        }

        async Task SampleBusy()
        {
            if (sampling || !Visible) return;
            sampling = true;
            try
            {
                List<BusyItem> items = await Task.Run(delegate { return sampler.Sample(); });
                if (IsDisposed) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("Processor: " + Math.Round(sampler.TotalCpu) + "% busy");
                int shown = 0;
                foreach (BusyItem it in items)
                {
                    if (shown == 3 || it.Cpu < 0.5) break;
                    sb.Append("\n• " + it.Title + "  " + it.Cpu.ToString(it.Cpu < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + "%");
                    shown++;
                }
                if (shown == 0 && sampler.TotalCpu > 0) sb.Append("\nNothing busy. Nice and calm.");
                busy.Text = sb.ToString();
            }
            catch (Exception ex) { Log.Write("flyout sample: " + ex.Message); }
            finally { sampling = false; }
        }
    }
}
