// The running app (icon by the clock, reminders, self-updates), the installer and the start-up code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuietPC
{
    // ------------------------------------------------------------------ self-updates

    class UpdateInfo
    {
        public Version Version;
        public string Sha256 = "";
        public List<string> Notes = new List<string>();
    }

    static class Updater
    {
        static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "QuietPC/" + AppInfo.Version;
            wc.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        public static UpdateInfo Fetch()
        {
            string text;
            using (WebClient wc = NewClient()) text = wc.DownloadString(AppInfo.VersionUrl);
            UpdateInfo info = new UpdateInfo();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                if (k == "version") info.Version = new Version(v);
                else if (k == "sha256") info.Sha256 = v.ToLowerInvariant();
                else if (k == "notes") foreach (string n in v.Split('|')) if (n.Trim().Length > 0) info.Notes.Add(n.Trim());
            }
            if (info.Version == null) throw new Exception("The update information was empty.");
            return info;
        }

        public static string Download(UpdateInfo info)
        {
            // Saved in QuietPC's admin-only folder, so nothing else can swap the file before it runs.
            Paths.EnsureDataDir();
            string dir = Path.Combine(Paths.DataDir, "update");
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, "*.exe")) { try { File.Delete(old); } catch { } }
            string path = Path.Combine(dir, "QuietPC-Setup-" + info.Version + ".exe");
            using (WebClient wc = NewClient()) wc.DownloadFile(AppInfo.SetupUrl(info.Version), path);
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] head = new byte[2];
                if (fs.Length < 10000 || fs.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z')
                    throw new Exception("The downloaded file was not a Windows program.");
            }
            if (info.Sha256.Length > 0)
            {
                string hash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (hash != info.Sha256)
                {
                    try { File.Delete(path); } catch { }
                    throw new Exception("The download was damaged (it didn't match). Please try again.");
                }
            }
            return path;
        }
    }

    // ------------------------------------------------------------------ the running app

    class QuietApp : ApplicationContext
    {
        readonly Icon icon;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly MainForm main;
        readonly Flyout flyout;
        readonly System.Windows.Forms.Timer reminderTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer promoteTimer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem installUpdateItem;
        readonly EventWaitHandle showSignal;
        readonly RegisteredWaitHandle showWait;

        public bool Quitting;
        string balloonAction = "";
        bool checking, userWaitingForCheck, installing;
        UpdateInfo availableUpdate;
        Version notifiedVersion;
        int promoteTries;

        public QuietApp(bool showWindow, bool justUpdated, EventWaitHandle showSignal)
        {
            icon = Theme.MakeIcon(Theme.S(32));
            main = new MainForm(this, icon);
            IntPtr unused = main.Handle; // used to run things on the window's thread
            flyout = new Flyout(this);

            ContextMenuStrip menu = new ContextMenuStrip();
            installUpdateItem = new ToolStripMenuItem("Install update", null, delegate { if (availableUpdate != null) AskInstall(availableUpdate); });
            installUpdateItem.Font = new Font(installUpdateItem.Font, FontStyle.Bold);
            installUpdateItem.Visible = false;
            menu.Items.Add(installUpdateItem);
            menu.Items.Add("Open QuietPC", null, delegate { OpenMain(null); });
            menu.Items.Add("Calm my PC now", null, delegate { OpenMain("home"); });
            menu.Items.Add("Check for updates", null, delegate { CheckForUpdates(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit QuietPC", null, delegate { Quit(); });

            tray.Icon = icon;
            tray.Text = "QuietPC";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (flyout.Visible) { flyout.Hide(); return; }
                // a click on the icon first closes the panel (it loses focus); don't reopen it straight away
                if ((DateTime.UtcNow - flyout.HiddenAt).TotalMilliseconds < 350) return;
                flyout.ShowNearTray();
            };
            tray.BalloonTipClicked += delegate
            {
                if (balloonAction == "update" && availableUpdate != null) AskInstall(availableUpdate);
                else if (balloonAction == "security") OpenMain("updates");
            };
            tray.Visible = true;

            // Put the icon next to Wi-Fi and sound (not hidden behind the ^ arrow).
            promoteTimer.Interval = 5000;
            promoteTimer.Tick += delegate
            {
                promoteTries++;
                if (PromoteTrayIcon(false) || promoteTries >= 12) promoteTimer.Stop();
            };
            promoteTimer.Start();

            // Monthly reminder about security updates while they're paused.
            reminderTimer.Interval = 60 * 60 * 1000;
            reminderTimer.Tick += delegate { RemindAboutUpdates(); };
            reminderTimer.Start();

            // QuietPC's own updates: shortly after start, then every 6 hours.
            updateTimer.Interval = 30000;
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = 6 * 60 * 60 * 1000;
                CheckForUpdates(false);
            };
            updateTimer.Start();

            // A second launch (Start menu) just opens this window.
            this.showSignal = showSignal;
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
            {
                RunOnUi(delegate { OpenMain(null); });
            }, null, -1, false);

            if (justUpdated) Balloon("QuietPC updated", "You now have version " + AppInfo.Version + ".", "");
            if (showWindow) OpenMain("home");
        }

        void RunOnUi(Action a)
        {
            try { if (!Quitting && main.IsHandleCreated) main.BeginInvoke(a); } catch { }
        }

        void Balloon(string title, string text, string action)
        {
            balloonAction = action;
            tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
        }

        public void OpenMain(string page)
        {
            if (flyout.Visible) flyout.Hide();
            main.Open(page);
        }

        public void RefreshTraySoon()
        {
            RunOnUi(delegate { if (flyout.Visible) flyout.RefreshTiles(); });
        }

        public void RefreshMainSoon()
        {
            // The main window rebuilds its page every time it's opened, so nothing to do while hidden.
        }

        void RemindAboutUpdates()
        {
            try
            {
                if (PauseUpdatesTweak.PausedUntil() == null) return;
                int days = WindowsUpdates.DaysPaused();
                if (days < 30) return;
                DateTime last;
                if (DateTime.TryParse(Store.Get("lastReminder"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) &&
                    (DateTime.UtcNow - last.ToUniversalTime()).TotalDays < 7) return;
                Store.Set("lastReminder", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                Balloon("Security updates are waiting",
                    "Updates have been paused for " + days + " days. Click here to update when it suits you.", "security");
            }
            catch (Exception ex) { Log.Write("reminder: " + ex.Message); }
        }

        // Windows remembers tray icons in HKCU\Control Panel\NotifyIconSettings; IsPromoted=1 shows it by the clock.
        public bool PromoteTrayIcon(bool force)
        {
            if (!force && Store.Get("promoted") == "1") return true;
            bool found = false;
            try
            {
                using (RegistryKey root = Reg.Root(RegistryHive.CurrentUser))
                using (RegistryKey settings = root.OpenSubKey(@"Control Panel\NotifyIconSettings", false))
                {
                    if (settings == null) return false;
                    foreach (string sub in settings.GetSubKeyNames())
                    {
                        using (RegistryKey k = settings.OpenSubKey(sub, true))
                        {
                            string path = k == null ? null : k.GetValue("ExecutablePath") as string;
                            if (path == null || !path.EndsWith(@"\QuietPC\QuietPC.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            found = true;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("promote icon: " + ex.Message); }
            if (found) Store.Set("promoted", "1");
            return found;
        }

        // ---------- updates of QuietPC itself

        public void CheckForUpdates(bool userAsked)
        {
            if (checking) { if (userAsked) userWaitingForCheck = true; return; }
            checking = true;
            userWaitingForCheck = userAsked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                Exception error = null;
                try { info = Updater.Fetch(); }
                catch (Exception ex) { error = ex; }
                RunOnUi(delegate
                {
                    checking = false;
                    bool asked = userWaitingForCheck;
                    userWaitingForCheck = false;
                    OnCheckDone(info, error, asked);
                });
            });
        }

        IWin32Window Owner { get { return main.Visible ? (IWin32Window)main : null; } }

        DialogResult Message(string text, MessageBoxIcon kind, MessageBoxButtons buttons = MessageBoxButtons.OK)
        {
            if (Owner != null) return MessageBox.Show(Owner, text, "QuietPC", buttons, kind);
            return MessageBox.Show(text, "QuietPC", buttons, kind, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }

        void OnCheckDone(UpdateInfo info, Exception error, bool userAsked)
        {
            if (error != null)
            {
                Log.Write("update check: " + error.Message);
                if (userAsked) Message("Couldn't check for updates right now.\n\nMake sure you're connected to the internet, then try again.", MessageBoxIcon.Warning);
                return;
            }
            if (info.Version > AppInfo.Current)
            {
                availableUpdate = info;
                installUpdateItem.Text = "Install update (version " + info.Version + ")";
                installUpdateItem.Visible = true;
                if (userAsked) AskInstall(info);
                else if (notifiedVersion == null || notifiedVersion != info.Version)
                {
                    notifiedVersion = info.Version;
                    Balloon("QuietPC update ready", "Version " + info.Version + " is ready. Click here to install it.", "update");
                }
            }
            else if (userAsked) Message("You have the newest version (" + AppInfo.Version + ").", MessageBoxIcon.Information);
        }

        void AskInstall(UpdateInfo info)
        {
            string text = "QuietPC " + info.Version + " is ready (you have " + AppInfo.Version + ").";
            if (info.Notes.Count > 0)
            {
                text += "\n\nWhat's new:";
                foreach (string n in info.Notes) text += "\n  • " + n;
            }
            text += "\n\nInstall it now? It takes a few seconds and keeps all your settings.";
            if (Message(text, MessageBoxIcon.Question, MessageBoxButtons.YesNo) == DialogResult.Yes) InstallUpdate(info);
        }

        void InstallUpdate(UpdateInfo info)
        {
            if (installing) return;
            installing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                Exception error = null;
                try { path = Updater.Download(info); }
                catch (Exception ex) { error = ex; }
                RunOnUi(delegate
                {
                    if (error != null)
                    {
                        installing = false;
                        Log.Write("update download: " + error);
                        Message("The update couldn't be downloaded.\n\n" + error.Message, MessageBoxIcon.Warning);
                        return;
                    }
                    try
                    {
                        Process.Start(path);
                        Quit();
                    }
                    catch (Exception ex)
                    {
                        installing = false;
                        Log.Write("update start: " + ex);
                        Message("The update couldn't start.\n\n" + ex.Message, MessageBoxIcon.Warning);
                    }
                });
            });
            Balloon("Downloading QuietPC " + info.Version, "It installs itself in a moment.", "");
        }

        public void Quit()
        {
            if (Quitting) return;
            Quitting = true;
            try
            {
                if (showWait != null) showWait.Unregister(null);
                reminderTimer.Stop();
                updateTimer.Stop();
                promoteTimer.Stop();
                tray.Visible = false;
                tray.Dispose();
                flyout.Close();
                main.Close();
            }
            catch { }
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ install / uninstall

    static class Installer
    {
        public const string MutexName = "QuietPC_SingleInstance_1";
        public const string ShowEventName = "QuietPC_Show_1";
        const string TaskName = "QuietPC";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QuietPC";

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "QuietPC.lnk"); }
        }

        public static bool RunningInstalledCopy()
        {
            try
            {
                return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static Version InstalledVersion()
        {
            try
            {
                if (!File.Exists(Paths.InstalledExe)) return null;
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(Paths.InstalledExe);
                return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { return null; }
        }

        static bool IsRunning()
        {
            bool created;
            using (Mutex probe = new Mutex(false, MutexName, out created)) return !created;
        }

        static void SignalShow()
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out ev)) using (ev) ev.Set();
            }
            catch { }
        }

        static void StartInstalled(string args)
        {
            try { if (File.Exists(Paths.InstalledExe)) Process.Start(Paths.InstalledExe, args); } catch { }
        }

        static void CloseRunningCopies()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || !p.ProcessName.StartsWith("QuietPC", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // The file you double-click (QuietPC-Setup.exe) installs or updates QuietPC.
        public static void RunSetup()
        {
            Version installed = InstalledVersion();
            if (installed != null && installed >= AppInfo.Current)
            {
                // Already up to date: just open it.
                if (IsRunning()) SignalShow(); else StartInstalled("");
                return;
            }

            Mutex m = null;
            for (int i = 0; i < 40 && m == null; i++)
            {
                bool created;
                Mutex attempt = new Mutex(true, MutexName, out created);
                if (created) { m = attempt; break; }
                attempt.Dispose();
                if (i == 8) CloseRunningCopies();
                Thread.Sleep(250);
            }
            if (m == null)
            {
                MessageBox.Show("Please quit QuietPC first (right-click the moon icon by the clock > Quit), then run this again.", "QuietPC");
                return;
            }
            CloseRunningCopies();
            bool ok = Install();
            m.ReleaseMutex();
            m.Dispose();
            StartInstalled(ok && installed != null ? "/updated" : "");
        }

        static bool Install()
        {
            Paths.EnsureDataDir();
            Exception last = null;
            string temp = Paths.InstalledExe + ".new";
            try
            {
                Directory.CreateDirectory(Paths.InstallDir);
                File.Copy(Application.ExecutablePath, temp, true);
            }
            catch (Exception ex) { last = ex; }

            bool swapped = false;
            for (int i = 0; i < 20 && !swapped && last == null; i++)
            {
                try
                {
                    if (File.Exists(Paths.InstalledExe)) File.Replace(temp, Paths.InstalledExe, null, true);
                    else File.Move(temp, Paths.InstalledExe);
                    swapped = true;
                }
                catch (IOException ex) { Thread.Sleep(300); if (i == 19) last = ex; }
                catch (UnauthorizedAccessException ex) { Thread.Sleep(300); if (i == 19) last = ex; }
                catch (Exception ex) { last = ex; }
            }
            if (!swapped)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                MessageBox.Show("QuietPC could not be installed.\n\n" + (last != null ? last.Message : ""), "QuietPC");
                return false;
            }

            MakeShortcut(ShortcutPath, Paths.InstalledExe);
            if (Store.Get("startAtSignIn", "1") == "1") SetStartAtSignIn(true);
            try
            {
                using (RegistryKey root = Reg.Root(RegistryHive.LocalMachine))
                using (RegistryKey k = root.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "QuietPC");
                    k.SetValue("DisplayIcon", Paths.InstalledExe);
                    k.SetValue("DisplayVersion", AppInfo.Version);
                    k.SetValue("Publisher", "QuietPC");
                    k.SetValue("InstallLocation", Paths.InstallDir);
                    k.SetValue("UninstallString", "\"" + Paths.InstalledExe + "\" /uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 400, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Write("uninstall entry: " + ex.Message); }
            Log.Write("installed " + AppInfo.Version);
            return true;
        }

        static void MakeShortcut(string lnk, string target)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Calm Windows down" });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { Log.Write("shortcut: " + ex.Message); }
        }

        // QuietPC needs administrator rights, so it starts at sign-in through Task Scheduler
        // (a normal "startup app" can't start with administrator rights without asking each time).
        public static bool SetStartAtSignIn(bool on)
        {
            Store.Set("startAtSignIn", on ? "1" : "0");
            if (!on)
            {
                Shell.Result del = Shell.Run(Paths.System32("schtasks.exe"), "/Delete /TN \"" + TaskName + "\" /F", 15000);
                return del.Ok || !StartsAtSignIn();
            }
            string user = WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Starts QuietPC when you sign in.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Xml(user) + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Xml(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + Xml(Paths.InstalledExe) + "\"</Command><Arguments>/tray</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            Paths.EnsureDataDir();
            string file = Path.Combine(Paths.DataDir, "task.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                Shell.Result r = Shell.Run(Paths.System32("schtasks.exe"), "/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", 15000);
                return r.Ok;
            }
            catch (Exception ex) { Log.Write("task: " + ex.Message); return false; }
            finally { try { File.Delete(file); } catch { } }
        }

        public static bool StartsAtSignIn()
        {
            return Shell.Run(Paths.System32("schtasks.exe"), "/Query /TN \"" + TaskName + "\"", 15000).Ok;
        }

        static string Xml(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static void Uninstall()
        {
            bool first;
            using (Mutex only = new Mutex(true, "QuietPC_Uninstall_1", out first))
            {
                if (!first) return; // already uninstalling
                UninstallNow();
            }
        }

        static void UninstallNow()
        {
            DialogResult answer = MessageBox.Show(
                "Remove QuietPC from this computer?\n\nYes = remove it AND put back every Windows setting it changed.\nNo = remove it but keep the changes.\nCancel = don't remove.",
                "QuietPC", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;

            CloseRunningCopies();
            List<string> failed = new List<string>();
            if (answer == DialogResult.Yes)
            {
                using (Form wait = new Form())
                {
                    wait.Text = "QuietPC";
                    wait.FormBorderStyle = FormBorderStyle.FixedDialog;
                    wait.ControlBox = false;
                    wait.StartPosition = FormStartPosition.CenterScreen;
                    wait.TopMost = true;
                    wait.ClientSize = new Size(Theme.S(380), Theme.S(90));
                    Label l = new Label();
                    l.Text = "Putting your Windows settings back...\nThis takes up to a minute.";
                    l.AutoSize = true;
                    l.Location = new Point(Theme.S(20), Theme.S(24));
                    wait.Controls.Add(l);
                    wait.Show();
                    System.Threading.Tasks.Task<List<string>> undo = System.Threading.Tasks.Task.Run(delegate
                    {
                        try { return Engine.UndoEverything(null); }
                        catch (Exception ex) { Log.Write("uninstall undo: " + ex); return new List<string> { "some settings" }; }
                    });
                    while (!undo.IsCompleted) { Application.DoEvents(); Thread.Sleep(50); }
                    failed = undo.Result;
                    wait.Close();
                }
            }
            Shell.Run(Paths.System32("schtasks.exe"), "/Delete /TN \"" + TaskName + "\" /F", 15000);
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { using (RegistryKey root = Reg.Root(RegistryHive.LocalMachine)) root.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (answer == DialogResult.Yes && failed.Count == 0)
            {
                try { if (Directory.Exists(Paths.DataDir)) Directory.Delete(Paths.DataDir, true); } catch { }
            }

            if (failed.Count > 0)
                MessageBox.Show("QuietPC has been removed, but these couldn't be put back:\n\n\u2022 " + string.Join("\n\u2022 ", failed.ToArray()) +
                    "\n\nThe original values are kept in " + Paths.DataDir + " in case you reinstall QuietPC and press \"Undo everything\".", "QuietPC");
            else MessageBox.Show("QuietPC has been removed.", "QuietPC");

            // The program can't delete itself while it runs; a hidden helper removes the folder a moment later.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    static class Program
    {
        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args) if (a.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("error: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("fatal: " + e.ExceptionObject); };

            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = Math.Max(1f, g.DpiX / 96f);

            if (HasArg(args, "/uninstall")) { Installer.Uninstall(); return; }
            if (Installer.RunningInstalledCopy()) Paths.EnsureDataDir();

            if (!Installer.RunningInstalledCopy())
            {
                Installer.RunSetup();
                return;
            }

            bool created;
            Mutex m = new Mutex(true, Installer.MutexName, out created);
            if (!created)
            {
                m.Dispose();
                if (!HasArg(args, "/tray"))
                {
                    try
                    {
                        EventWaitHandle ev;
                        if (EventWaitHandle.TryOpenExisting(Installer.ShowEventName, out ev)) using (ev) ev.Set();
                    }
                    catch { }
                }
                return;
            }

            using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ShowEventName))
            {
                Application.Run(new QuietApp(!HasArg(args, "/tray"), HasArg(args, "/updated"), show));
            }
            GC.KeepAlive(m);
            try { m.ReleaseMutex(); } catch { }
            m.Dispose();
        }
    }
}
