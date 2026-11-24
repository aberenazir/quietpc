// The main QuietPC window: a menu on the left and one page at a time on the right.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuietPC
{
    class MainForm : Form
    {
        readonly QuietApp app;
        readonly Panel side = new Panel();
        readonly Panel content = new Panel();
        readonly List<NavButton> navButtons = new List<NavButton>();
        Stack page;
        string current = "";

        readonly ProcessSampler sampler = new ProcessSampler();
        readonly Timer busyTimer = new Timer();
        BusyList busyList;
        Label busyTotal;
        bool sampling;

        static readonly string[,] Pages =
        {
            { "home", "", "Home" },
            { "updates", "", "Updates" },
            { "fan", "", "Fan & battery" },
            { "startup", "", "Startup apps" },
            { "cleanup", "", "Clean up Windows" },
            { "junk", "", "Remove junk apps" },
            { "space", "", "Free up space" },
            { "settings", "", "Settings" },
        };

        public MainForm(QuietApp app, Icon icon)
        {
            this.app = app;
            Text = "QuietPC";
            Icon = icon;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            StartPosition = FormStartPosition.CenterScreen;
            // Fit the screen (a 1080p laptop at 150% has less room than you'd think).
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.S(760), wa.Width - 40), Math.Min(Theme.S(520), wa.Height - 40));
            Size = new Size(Math.Min(Theme.S(1000), wa.Width - 40), Math.Min(Theme.S(720), wa.Height - 40));

            side.Dock = DockStyle.Left;
            side.Width = Theme.S(230);
            side.BackColor = Theme.Side;
            side.Padding = new Padding(Theme.S(6), Theme.S(14), Theme.S(6), Theme.S(10));

            FlowLayoutPanel menu = new FlowLayoutPanel();
            menu.Dock = DockStyle.Fill;
            menu.FlowDirection = FlowDirection.TopDown;
            menu.WrapContents = false;
            menu.BackColor = Theme.Side;

            Label brand = new Label();
            brand.Text = "QuietPC";
            brand.Font = Theme.Big;
            brand.ForeColor = Theme.Text;
            brand.AutoSize = true;
            brand.Margin = new Padding(Theme.S(14), 0, 0, Theme.S(16));
            menu.Controls.Add(brand);

            for (int i = 0; i < Pages.GetLength(0); i++)
            {
                NavButton b = new NavButton(Pages[i, 1], Pages[i, 2]);
                b.Width = side.Width - side.Padding.Horizontal;
                b.Tag = Pages[i, 0];
                b.Click += delegate(object s, EventArgs e) { ShowPage((string)((Control)s).Tag); };
                navButtons.Add(b);
                menu.Controls.Add(b);
            }
            side.Controls.Add(menu);

            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Back;
            Controls.Add(content);
            Controls.Add(side);

            busyTimer.Interval = 2000;
            busyTimer.Tick += async delegate { await SampleBusy(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.DarkTitleBar(Handle);
        }

        // Closing the window keeps QuietPC running near the clock.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !app.Quitting)
            {
                e.Cancel = true;
                Hide();
                busyTimer.Stop();
                return;
            }
            busyTimer.Stop();
            base.OnFormClosing(e);
        }

        public void Open(string pageId)
        {
            ShowPage(pageId ?? (current.Length > 0 ? current : "home"));
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        public void ShowPage(string id)
        {
            busyTimer.Stop();
            busyList = null;
            current = id;
            foreach (NavButton b in navButtons) { b.Selected = (string)b.Tag == id; b.Invalidate(); }

            Stack old = page;
            page = new Stack();
            page.Dock = DockStyle.Fill;
            page.SuspendLayout();
            switch (id)
            {
                case "updates": BuildUpdates(page); break;
                case "fan": BuildFan(page); break;
                case "startup": BuildStartup(page); break;
                case "cleanup": BuildCleanup(page); break;
                case "junk": BuildJunk(page); break;
                case "space": BuildSpace(page); break;
                case "settings": BuildSettings(page); break;
                default: BuildHome(page); break;
            }
            page.ResumeLayout();
            content.Controls.Add(page);
            if (old != null) { content.Controls.Remove(old); old.Dispose(); }
        }

        // ------------------------------------------------------------------ shared pieces

        Row TweakRow(Stack p, Tweak t)
        {
            Row row = new Row { Title = t.Title, Description = t.Description, Warning = t.Warning };
            ToggleSwitch sw = new ToggleSwitch();
            sw.Busy = true;
            row.Trailing = sw;
            p.Controls.Add(row);
            LoadTweakState(row, sw, t);
            sw.Toggled += async delegate(bool wanted) { await SetTweak(row, sw, t, wanted); };
            return row;
        }

        async void LoadTweakState(Row row, ToggleSwitch sw, Tweak t)
        {
            bool on = false, available = true;
            try
            {
                await Task.Run(delegate
                {
                    on = t.IsOn();
                    if (!on && t is PowerTweak) available = t.IsAvailable();
                });
            }
            catch (Exception ex) { Log.Write("state " + t.Id + ": " + ex.Message); }
            if (row.IsDisposed) return;
            sw.On = on;
            sw.Busy = false;
            if (!available)
            {
                sw.Enabled = false;
                row.Status = "This laptop doesn't offer this setting.";
                row.StatusColor = Theme.Dim;
                row.Fit();
            }
        }

        async Task SetTweak(Row row, ToggleSwitch sw, Tweak t, bool wanted)
        {
            sw.Busy = true;
            string error = null;
            bool on = !wanted;
            try
            {
                await Task.Run(delegate
                {
                    if (wanted)
                    {
                        if (t.Recommended || t.NeedsExplorerRestart) Engine.RestorePointOnce();
                        Engine.Apply(t);
                    }
                    else Engine.Revert(t);
                    on = t.IsOn();
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("toggle " + t.Id + ": " + ex);
                try { on = await Task.Run(delegate { return t.IsOn(); }); } catch { }
            }
            if (row.IsDisposed) return;
            sw.On = on;
            sw.Busy = false;
            app.RefreshTraySoon();
            if (t is PauseUpdatesTweak) ShowPauseStatus(row);
            if (error != null) Dialogs.Problem(this, "That didn't work:\n\n" + error);
            else if (t.NeedsExplorerRestart &&
                     Dialogs.Ask(this, "This change shows after the taskbar and File Explorer restart (a few seconds; open folders close).\n\nRestart them now?") == DialogResult.Yes)
                await Task.Run(delegate { ExplorerTools.Restart(); });
        }

        async Task Busy(FlatBtn button, Func<Task> work)
        {
            bool was = button.Enabled;
            button.Enabled = false;
            UseWaitCursor = true;
            try { await work(); }
            catch (Exception ex)
            {
                Log.Write("action: " + ex);
                Dialogs.Problem(this, "Something went wrong:\n\n" + ex.Message);
            }
            finally
            {
                UseWaitCursor = false;
                if (!button.IsDisposed) button.Enabled = was;
            }
        }

        // ------------------------------------------------------------------ home

        void BuildHome(Stack p)
        {
            p.Heading("Your PC", Theme.PageTitle);
            p.Note("One click calms Windows down: updates wait for you, the fan stays quiet, no ads. Everything can be undone.");

            Row status = new Row { Title = "Checking your PC...", Description = "" };
            p.Controls.Add(status);

            Label progress = null;
            ButtonBar bar = new ButtonBar();
            FlatBtn calm = bar.Add("Calm my PC now", true, null);
            FlatBtn undo = bar.Add("Undo everything", false, null);
            p.Controls.Add(bar);
            progress = p.Note("", 6);

            calm.Click += async delegate
            {
                await Busy(calm, async delegate
                {
                    List<string> problems = null;
                    await Task.Run(delegate
                    {
                        problems = Engine.CalmNow(delegate(string msg) { Report(progress, msg); });
                    });
                    Report(progress, "");
                    app.RefreshTraySoon();
                    if (problems.Count > 0)
                        Dialogs.Info(this, "Done. A few things couldn't be changed on this PC:\n\n• " + string.Join("\n• ", problems.ToArray()));
                    if (!status.IsDisposed) await RefreshHome(status);
                });
            };
            undo.Click += async delegate { await UndoAll(undo, progress); if (!status.IsDisposed) await RefreshHome(status); };

            p.Heading("Quick access", Theme.CardTitle, null, Theme.S(16));
            p.Note("Click the QuietPC moon icon next to the Wi-Fi and sound icons (bottom-right, by the clock) for the quick panel: Quiet mode, Pause updates and Calm now in one click.");

            RefreshHomeAsync(status);
        }

        async void RefreshHomeAsync(Row status) { await RefreshHome(status); }

        async Task RefreshHome(Row status)
        {
            bool calm = false, quiet = false;
            DateTime? paused = null;
            await Task.Run(delegate
            {
                calm = Engine.IsCalm;
                quiet = Catalog.QuietBattery.IsOn();
                paused = PauseUpdatesTweak.PausedUntil();
            });
            if (status.IsDisposed) return;
            status.Title = calm ? "✔  Your PC is calm" : "Your PC isn't calmed yet";
            string text = "Updates: " + (paused != null ? "paused until " + paused.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : "Windows decides when");
            text += "\nQuiet mode on battery: " + (quiet ? "on" : "off");
            if (BatteryInfo.HasBattery)
                text += "\nBattery: " + (int)(SystemInformation.PowerStatus.BatteryLifePercent * 100) + "% " + (BatteryInfo.OnBattery ? "(on battery)" : "(charging / plugged in)");
            status.Description = text;
            status.Status = calm ? "" : "Press \"Calm my PC now\" below.";
            status.StatusColor = Theme.Warn;
            status.Fit();
        }

        void Report(Label progress, string message)
        {
            if (progress.IsDisposed) return;
            try { progress.BeginInvoke(new Action(delegate { if (!progress.IsDisposed) progress.Text = message; })); } catch { }
        }

        async Task UndoAll(FlatBtn button, Label progress)
        {
            if (Dialogs.Ask(this, "Put back every setting QuietPC changed, exactly as it was before?\n\n(Apps you removed can be reinstalled from the Microsoft Store.)") != DialogResult.Yes) return;
            await Busy(button, async delegate
            {
                List<string> failed = await Task.Run(delegate { return Engine.UndoEverything(delegate(string m) { if (progress != null) Report(progress, m); }); });
                if (progress != null) Report(progress, "");
                app.RefreshTraySoon();
                if (failed.Count == 0) Dialogs.Info(this, "Done. Everything QuietPC changed is back the way it was.\n\nSome changes show fully after a restart.");
                else Dialogs.Problem(this, "Most settings are back, but these couldn't be put back right now:\n\n\u2022 " +
                    string.Join("\n\u2022 ", failed.ToArray()) + "\n\nQuietPC kept their original values. Try \"Undo everything\" again later or after a restart.");
            });
        }

        // ------------------------------------------------------------------ updates

        void BuildUpdates(Stack p)
        {
            p.Heading("Updates", Theme.PageTitle);
            p.Note("Windows only updates when you say so. Security fixes matter, so update every month or so: QuietPC reminds you.");

            Row pause = TweakRow(p, Catalog.PauseUpdates);
            Row howLong = new Row { Title = "Pause for", Description = "Used next time you pause (or right away if updates are paused)." };
            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Theme.Card;
            combo.ForeColor = Theme.Text;
            combo.Width = Theme.S(150);
            int[] days = { 7, 35, 90, 365 };
            string[] names = { "1 week", "5 weeks", "3 months", "1 year" };
            combo.Items.AddRange(names);
            int idx = Array.IndexOf(days, PauseUpdatesTweak.Days);
            combo.SelectedIndex = idx >= 0 ? idx : 3;
            howLong.Trailing = combo;
            p.Controls.Add(howLong);
            combo.SelectedIndexChanged += async delegate
            {
                PauseUpdatesTweak.Days = days[combo.SelectedIndex];
                bool paused = await Task.Run(delegate { return Catalog.PauseUpdates.IsOn(); });
                if (paused) await Task.Run(delegate { Engine.Apply(Catalog.PauseUpdates); });
                ShowPauseStatus(pause);
                app.RefreshTraySoon();
            };
            ShowPauseStatus(pause);

            ButtonBar bar = new ButtonBar();
            FlatBtn now = bar.Add("Update now", true, null);
            p.Controls.Add(bar);
            now.Click += async delegate
            {
                if (Dialogs.Ask(this, "This lifts the pause and opens Windows Update, which starts checking right away.\n\nWhen the updates are done, you can pause again here or from the quick panel.\n\nUpdate now?") != DialogResult.Yes) return;
                await Busy(now, async delegate { await Task.Run(delegate { WindowsUpdates.UpdateNow(); }); });
                app.RefreshTraySoon();
                if (!p.IsDisposed && current == "updates") ShowPage("updates");
            };

            p.Heading("More update settings", Theme.CardTitle, null, Theme.S(10));
            foreach (Tweak t in Catalog.InSection(Sections.Updates))
                if (t != Catalog.PauseUpdates) TweakRow(p, t);
        }

        async void ShowPauseStatus(Row pause)
        {
            DateTime? until = await Task.Run(delegate { return PauseUpdatesTweak.PausedUntil(); });
            if (pause.IsDisposed) return;
            pause.Status = until != null ? "Paused until " + until.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : "Not paused: Windows updates by itself.";
            pause.StatusColor = until != null ? Theme.Good : Theme.Dim;
            pause.Fit();
        }

        // ------------------------------------------------------------------ fan & battery

        void BuildFan(Stack p)
        {
            p.Heading("Fan & battery", Theme.PageTitle);
            p.Note("What's busy right now, in plain English. Updated every 2 seconds.");

            busyTotal = p.Heading("Processor: measuring...", Theme.RowTitle);
            busyList = new BusyList();
            busyList.EndClicked += EndProgram;
            p.Controls.Add(busyList);

            p.Heading("Make it quieter", Theme.CardTitle, null, Theme.S(14));
            foreach (Tweak t in Catalog.InSection(Sections.Fan)) TweakRow(p, t);

            p.Heading("Battery", Theme.CardTitle, null, Theme.S(14));
            Row health = new Row { Title = "Battery health", Description = BatteryInfo.HasBattery ? "Press \"Check battery health\"." : "No battery found." };
            p.Controls.Add(health);
            ButtonBar bar = new ButtonBar();
            FlatBtn check = bar.Add("Check battery health", false, null);
            FlatBtn report = bar.Add("Full battery report", false, null);
            FlatBtn wake = bar.Add("What woke my laptop?", false, null);
            p.Controls.Add(bar);

            check.Click += async delegate
            {
                await Busy(check, async delegate
                {
                    string text = await Task.Run(delegate { return BatteryInfo.Health(); });
                    if (!health.IsDisposed) { health.Description = text; health.Fit(); }
                });
            };
            report.Click += async delegate
            {
                await Busy(report, async delegate
                {
                    string path = await Task.Run(delegate { return BatteryInfo.FullReport(); });
                    if (path != null) Process.Start("explorer.exe", "\"" + path + "\""); // opens in your normal browser
                    else Dialogs.Problem(this, "Windows couldn't make a battery report.");
                });
            };
            wake.Click += async delegate
            {
                await Busy(wake, async delegate
                {
                    string text = await Task.Run(delegate { return BatteryInfo.LastWake(); });
                    Dialogs.ShowText(this, "What woke my laptop", text.Length > 0 ? text : "Windows didn't record anything.");
                });
            };

            busyTimer.Start();
            _ = SampleBusy(); // first reading; numbers appear on the next tick
        }

        async Task SampleBusy()
        {
            if (sampling || busyList == null || busyList.IsDisposed || !Visible) return;
            sampling = true;
            try
            {
                List<BusyItem> items = await Task.Run(delegate { return sampler.Sample(); });
                if (busyList == null || busyList.IsDisposed) return;
                busyList.SetItems(items);
                if (busyTotal != null && !busyTotal.IsDisposed)
                    busyTotal.Text = "Processor: " + Math.Round(sampler.TotalCpu) + "% busy" +
                                     (sampler.TotalCpu < 15 ? " (calm)" : sampler.TotalCpu < 50 ? "" : " (working hard: the fan may spin)");
            }
            catch (Exception ex) { Log.Write("sample: " + ex.Message); }
            finally { sampling = false; }
        }

        void EndProgram(BusyItem item)
        {
            if (Dialogs.Ask(this, "End \"" + item.Title + "\"?\n\nAnything unsaved in it will be lost.") != DialogResult.Yes) return;
            foreach (int pid in item.Pids)
            {
                try { using (Process proc = Process.GetProcessById(pid)) proc.Kill(); }
                catch { }
            }
            Log.Write("ended " + item.Key);
        }

        // ------------------------------------------------------------------ startup apps

        void BuildStartup(Stack p)
        {
            p.Heading("Startup apps", Theme.PageTitle);
            p.Note("Apps that start by themselves when you sign in. Fewer = faster start and longer battery. Turning one off doesn't remove it.");
            Label loading = p.Note("Looking...");
            LoadStartup(p, loading);
        }

        async void LoadStartup(Stack p, Label loading)
        {
            List<StartupItem> items = await Task.Run(delegate { return StartupApps.List(); });
            if (p.IsDisposed) return;
            loading.Text = items.Count == 0 ? "No startup apps found." : items.Count + " apps start with Windows.";
            p.SuspendLayout();
            foreach (StartupItem item in items)
            {
                StartupItem it = item;
                Row row = new Row { Title = it.Title, Description = it.Advice };
                if (it.Keep) { row.Status = "Recommended: keep on"; row.StatusColor = Theme.Dim; }
                ToggleSwitch sw = new ToggleSwitch();
                sw.On = it.Enabled;
                row.Trailing = sw;
                p.Controls.Add(row);
                sw.Toggled += async delegate(bool wanted)
                {
                    sw.Busy = true;
                    try { await Task.Run(delegate { StartupApps.SetEnabled(it, wanted); }); }
                    catch (Exception ex) { Dialogs.Problem(this, "That didn't work:\n\n" + ex.Message); }
                    if (row.IsDisposed) return;
                    sw.On = it.Enabled;
                    sw.Busy = false;
                };
            }
            p.ResumeLayout();
        }

        // ------------------------------------------------------------------ clean up windows

        void BuildCleanup(Stack p)
        {
            p.Heading("Clean up Windows", Theme.PageTitle);
            p.Note("Turn off ads, nags and background extras. Each switch can be turned back on any time.");
            foreach (Tweak t in Catalog.InSection(Sections.Cleanup)) TweakRow(p, t);

            ButtonBar bar = new ButtonBar();
            FlatBtn restart = bar.Add("Restart taskbar & File Explorer", false, null);
            p.Controls.Add(bar);
            p.Note("Some changes (like the right-click menu) show after this, or after signing out.");
            restart.Click += async delegate { await Busy(restart, async delegate { await Task.Run(delegate { ExplorerTools.Restart(); }); }); };
        }

        // ------------------------------------------------------------------ junk apps

        void BuildJunk(Stack p)
        {
            p.Heading("Remove junk apps", Theme.PageTitle);
            p.Note("Preinstalled apps and trials most people never use. Only removes what you click.");
            Label loading = p.Note("Looking for junk apps (takes a few seconds)...");
            LoadJunk(p, loading);
        }

        async void LoadJunk(Stack p, Label loading)
        {
            List<JunkApp> apps = await Task.Run(delegate { return JunkApps.Find(); });
            if (p.IsDisposed) return;
            loading.Text = apps.Count == 0 ? "No junk apps found. Nice and clean." : "Found " + apps.Count + ":";
            p.SuspendLayout();
            foreach (JunkApp a in apps)
            {
                JunkApp app1 = a;
                Row row = new Row { Title = app1.Title, Description = app1.Note };
                FlatBtn remove = new FlatBtn(app1.IsStoreApp ? "Remove" : "Uninstall", false);
                row.Trailing = remove;
                p.Controls.Add(row);
                remove.Click += async delegate
                {
                    string q = app1.IsStoreApp
                        ? "Remove \"" + app1.Title + "\"?\n\nYou can reinstall it from the Microsoft Store any time."
                        : "Open the uninstaller for \"" + app1.Title + "\"?";
                    if (Dialogs.Ask(this, q) != DialogResult.Yes) return;
                    await Busy(remove, async delegate
                    {
                        string error = await Task.Run(delegate { return JunkApps.Remove(app1); });
                        if (row.IsDisposed) return;
                        if (error != null) Dialogs.Problem(this, error);
                        else
                        {
                            row.Status = app1.IsStoreApp ? "✔ Removed" : "Uninstaller opened";
                            row.StatusColor = Theme.Good;
                            remove.Visible = !app1.IsStoreApp;
                            row.Fit();
                        }
                    });
                };
            }
            p.ResumeLayout();
        }

        // ------------------------------------------------------------------ free up space

        void BuildSpace(Stack p)
        {
            p.Heading("Free up space", Theme.PageTitle);
            p.Note("Deletes only leftovers Windows and apps don't need. Your own files, photos and downloads are never touched.");

            List<CleanArea> areas = Cleaner.Areas();
            List<Row> rows = new List<Row>();
            foreach (CleanArea a in areas)
            {
                Row r = new Row { Title = a.Title, Description = a.Note, Status = "Measuring...", StatusColor = Theme.Dim };
                rows.Add(r);
                p.Controls.Add(r);
            }
            ButtonBar bar = new ButtonBar();
            FlatBtn clean = bar.Add("Clean it all", true, null);
            p.Controls.Add(bar);
            Label result = p.Note("");

            MeasureAreas(areas, rows);
            clean.Click += async delegate
            {
                if (Dialogs.Ask(this, "Delete these leftover files now?\n\nFiles that are in use are skipped.") != DialogResult.Yes) return;
                await Busy(clean, async delegate
                {
                    long freed = await Task.Run(delegate
                    {
                        long total = 0;
                        foreach (CleanArea a in areas) total += Cleaner.Clean(a);
                        return total;
                    });
                    if (result.IsDisposed) return;
                    result.Text = "✔ Freed " + Cleaner.Size(freed) + ".";
                    result.ForeColor = Theme.Good;
                    MeasureAreas(areas, rows);
                });
            };
        }

        async void MeasureAreas(List<CleanArea> areas, List<Row> rows)
        {
            for (int i = 0; i < areas.Count; i++)
            {
                CleanArea a = areas[i];
                await Task.Run(delegate { Cleaner.Measure(a); });
                if (rows[i].IsDisposed) return;
                rows[i].Status = Cleaner.Size(a.Bytes);
                rows[i].StatusColor = a.Bytes > 0 ? Theme.Text : Theme.Dim;
                rows[i].Fit();
            }
        }

        // ------------------------------------------------------------------ settings

        void BuildSettings(Stack p)
        {
            p.Heading("Settings", Theme.PageTitle);

            Row start = new Row { Title = "Start QuietPC when I sign in", Description = "Keeps the moon icon by the clock and reminds you about updates." };
            ToggleSwitch sw = new ToggleSwitch();
            sw.Busy = true;
            start.Trailing = sw;
            p.Controls.Add(start);
            LoadStartWithWindows(sw);
            sw.Toggled += async delegate(bool wanted)
            {
                sw.Busy = true;
                bool ok = await Task.Run(delegate { return Installer.SetStartAtSignIn(wanted); });
                if (sw.IsDisposed) return;
                if (!ok) Dialogs.Problem(this, "Windows didn't allow that change.");
                sw.On = await Task.Run(delegate { return Installer.StartsAtSignIn(); });
                sw.Busy = false;
            };

            Row version = new Row { Title = "QuietPC " + AppInfo.Version, Description = "QuietPC checks for its own updates and tells you when a new version is ready." };
            FlatBtn check = new FlatBtn("Check for updates", false);
            version.Trailing = check;
            p.Controls.Add(version);
            check.Click += delegate { app.CheckForUpdates(true); };

            Row icon = new Row { Title = "Icon by the clock", Description = "If the moon icon is hidden behind the ^ arrow, this puts it back next to Wi-Fi and sound." };
            FlatBtn show = new FlatBtn("Show it", false);
            icon.Trailing = show;
            p.Controls.Add(icon);
            show.Click += delegate
            {
                bool done = app.PromoteTrayIcon(true);
                Dialogs.Info(this, done
                    ? "Done. If it's still hidden: Settings > Personalization > Taskbar > Other system tray icons > turn on QuietPC."
                    : "Open Settings > Personalization > Taskbar > Other system tray icons and turn on QuietPC.");
            };

            ButtonBar bar = new ButtonBar();
            FlatBtn undo = bar.Add("Undo everything", false, null);
            bar.Add("Open the log", false, delegate
            {
                string log = Path.Combine(Paths.DataDir, "log.txt");
                if (File.Exists(log)) Process.Start("notepad.exe", "\"" + log + "\"");
                else Dialogs.Info(this, "Nothing logged yet.");
            });
            p.Controls.Add(bar);
            Label progress = p.Note("", 6);
            undo.Click += async delegate { await UndoAll(undo, progress); };

            p.Note("To remove QuietPC: Settings > Apps > Installed apps > QuietPC > Uninstall. Use \"Undo everything\" first if you want all settings back.");
        }

        async void LoadStartWithWindows(ToggleSwitch sw)
        {
            bool on = await Task.Run(delegate { return Installer.StartsAtSignIn(); });
            if (sw.IsDisposed) return;
            sw.On = on;
            sw.Busy = false;
        }
    }

    // The "busy right now" list on the Fan & battery page.
    class BusyList : Control
    {
        List<BusyItem> items = new List<BusyItem>();
        readonly List<KeyValuePair<Rectangle, BusyItem>> endButtons = new List<KeyValuePair<Rectangle, BusyItem>>();
        const int Rows = 8;
        public event Action<BusyItem> EndClicked;

        public BusyList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(16) + Rows * Theme.S(50);
            Margin = new Padding(0, 0, 0, Theme.S(6));
        }

        public void SetItems(List<BusyItem> list)
        {
            items = list;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(8)))
            using (SolidBrush b = new SolidBrush(Theme.Card))
                g.FillPath(b, p);

            endButtons.Clear();
            int pad = Theme.S(16), rowH = Theme.S(50), y = Theme.S(8);
            int pctW = Theme.S(60), endW = Theme.S(64);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "Measuring...", Theme.Body, new Rectangle(pad, y, Width - pad * 2, rowH), Theme.Dim, Theme.Card,
                    TextFormatFlags.VerticalCenter);
                return;
            }
            for (int i = 0; i < Math.Min(Rows, items.Count); i++)
            {
                BusyItem it = items[i];
                int textW = Width - pad * 2 - pctW - endW - Theme.S(12);
                TextRenderer.DrawText(g, it.Title, Theme.RowTitle, new Rectangle(pad, y + Theme.S(5), textW, Theme.S(22)), Theme.Text, Theme.Card,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (it.Why.Length > 0)
                    TextRenderer.DrawText(g, it.Why, Theme.Small, new Rectangle(pad, y + Theme.S(26), textW, Theme.S(20)), Theme.Dim, Theme.Card,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                Color pc = it.Cpu >= 25 ? Theme.Warn : Theme.Text;
                TextRenderer.DrawText(g, it.Cpu.ToString(it.Cpu < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + "%", Theme.RowTitle,
                    new Rectangle(Width - pad - pctW, y, pctW, rowH), pc, Theme.Card, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

                if (it.CanEnd)
                {
                    Rectangle r = new Rectangle(Width - pad - pctW - endW, y + Theme.S(11), endW - Theme.S(10), Theme.S(28));
                    using (GraphicsPath bp = Theme.Rounded(r, Theme.S(5)))
                    using (Pen pen = new Pen(Theme.Line))
                        g.DrawPath(pen, bp);
                    TextRenderer.DrawText(g, "End", Theme.Small, r, Theme.Text, Theme.Card, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                    endButtons.Add(new KeyValuePair<Rectangle, BusyItem>(r, it));
                }
                if (i < Math.Min(Rows, items.Count) - 1)
                    using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, pad, y + rowH, Width - pad, y + rowH);
                y += rowH;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = false;
            foreach (KeyValuePair<Rectangle, BusyItem> kv in endButtons) if (kv.Key.Contains(e.Location)) over = true;
            Cursor = over ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            foreach (KeyValuePair<Rectangle, BusyItem> kv in endButtons)
                if (kv.Key.Contains(e.Location) && EndClicked != null) { EndClicked(kv.Value); break; }
            base.OnMouseClick(e);
        }
    }
}
