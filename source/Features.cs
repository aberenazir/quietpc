// Startup apps, junk apps, freeing up space, battery health and "Update now".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuietPC
{
    // ------------------------------------------------------------------ startup apps

    class StartupItem
    {
        public string Name;        // as Windows lists it
        public string Command;
        public string Title;       // plain-English name
        public string Advice;
        public bool Keep;          // needed for the laptop to work properly
        public bool Enabled;
        public RegistryHive ApprovedHive;
        public string ApprovedPath;
    }

    // Turns startup apps on/off exactly like Task Manager does (the "StartupApproved" list),
    // so Task Manager and Settings show the same thing and nothing is deleted.
    static class StartupApps
    {
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        static readonly string[] KeepWords = { "securityhealth", "realtek", "rtkaud", "synaptics", "elan", "touchpad", "hotkey",
                                               "keypatch", "audio", "bluetooth", "fingerprint", "smartaudio", "dolby", "waves" };

        public static List<StartupItem> List()
        {
            List<StartupItem> list = new List<StartupItem>();
            AddRun(list, RegistryHive.CurrentUser, RunPath, RegistryView.Default, Approved + "Run");
            AddRun(list, RegistryHive.LocalMachine, RunPath, RegistryView.Registry64, Approved + "Run");
            AddRun(list, RegistryHive.LocalMachine, RunPath, RegistryView.Registry32, Approved + "Run32");
            AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser);
            AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine);
            list.Sort(delegate(StartupItem a, StartupItem b) { return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        static void AddRun(List<StartupItem> list, RegistryHive hive, string path, RegistryView view, string approvedPath)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey k = root.OpenSubKey(path, false))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        if (name.Length == 0) continue;
                        string cmd = Convert.ToString(k.GetValue(name, ""));
                        list.Add(Make(name, cmd, ExeFromCommand(cmd), hive, approvedPath));
                    }
                }
            }
            catch (Exception ex) { Log.Write("startup list: " + ex.Message); }
        }

        static void AddFolder(List<StartupItem> list, string folder, RegistryHive hive)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(Make(name, file, file, hive, Approved + "StartupFolder"));
                }
            }
            catch (Exception ex) { Log.Write("startup folder: " + ex.Message); }
        }

        static StartupItem Make(string name, string cmd, string exe, RegistryHive hive, string approvedPath)
        {
            StartupItem s = new StartupItem();
            s.Name = name;
            s.Command = cmd;
            s.ApprovedHive = hive;
            s.ApprovedPath = approvedPath;
            s.Title = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
            try
            {
                if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
                {
                    FileVersionInfo fv = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(fv.FileDescription) && fv.FileDescription.Trim().Length > 2)
                        s.Title = fv.FileDescription.Trim();
                }
            }
            catch { }

            string all = (name + " " + cmd).ToLowerInvariant();
            foreach (string w in KeepWords) if (all.Contains(w)) s.Keep = true;
            s.Advice = s.Keep
                ? "Keep on: your laptop's sound, touchpad, keys or security need it."
                : "Safe to turn off. The app still works when you open it yourself.";
            if (all.Contains("onedrive")) s.Advice = "Off = OneDrive only syncs when you open it.";
            if (all.Contains("keypatch")) s.Advice = "Your missing-keys app. Keep it on.";

            s.Enabled = IsEnabled(s);
            return s;
        }

        static string ExeFromCommand(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? cmd.Substring(0, exe + 4) : cmd;
        }

        static bool IsEnabled(StartupItem s)
        {
            byte[] b = Reg.Read(s.ApprovedHive, s.ApprovedPath, s.Name) as byte[];
            return b == null || b.Length == 0 || (b[0] & 1) == 0;
        }

        static string Key(StartupItem s) { return "startup|" + s.ApprovedHive + "|" + s.ApprovedPath + "|" + s.Name; }

        public static void SetEnabled(StartupItem s, bool on)
        {
            object current = Reg.Read(s.ApprovedHive, s.ApprovedPath, s.Name);
            byte[] cb = current as byte[];
            Backup.SaveOnce(Key(s), cb == null ? "absent" : BitConverter.ToString(cb).Replace("-", ""));

            byte[] value = new byte[12];
            value[0] = on ? (byte)2 : (byte)3;
            if (!on)
            {
                byte[] time = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                Array.Copy(time, 0, value, 4, 8);
            }
            Reg.Write(s.ApprovedHive, s.ApprovedPath, s.Name, value, RegistryValueKind.Binary);
            s.Enabled = on;
            Log.Write("startup " + s.Name + " -> " + (on ? "on" : "off"));
        }

        public static void UndoAll()
        {
            foreach (string key in Backup.KeysWithPrefix("startup|"))
            {
                string[] parts = key.Split(new[] { '|' }, 4);
                if (parts.Length != 4) continue;
                RegistryHive hive = parts[1] == RegistryHive.LocalMachine.ToString() ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                string original;
                if (!Backup.TryGet(key, out original)) continue;
                try
                {
                    if (original == "absent") Reg.Delete(hive, parts[2], parts[3]);
                    else
                    {
                        byte[] b = new byte[original.Length / 2];
                        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(original.Substring(i * 2, 2), 16);
                        Reg.Write(hive, parts[2], parts[3], b, RegistryValueKind.Binary);
                    }
                    Backup.Remove(key);
                }
                catch (Exception ex) { Log.Write("startup undo " + key + ": " + ex.Message); }
            }
        }
    }

    // ------------------------------------------------------------------ junk apps

    class JunkApp
    {
        public string Title;
        public string Note;
        public string PackageFullName;   // Store app
        public string UninstallCommand;  // desktop program
        public bool IsStoreApp { get { return PackageFullName != null; } }
    }

    static class JunkApps
    {
        // Store apps that come preinstalled and that most people never use.
        static readonly string[,] StoreJunk =
        {
            { "Microsoft.BingNews", "Microsoft News", "News feed app." },
            { "Microsoft.BingWeather", "Weather", "Weather app. Remove if you check weather elsewhere." },
            { "Microsoft.GetHelp", "Get Help", "Microsoft support app." },
            { "Microsoft.Getstarted", "Tips", "Windows tips app." },
            { "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Card games with ads." },
            { "Microsoft.People", "People", "Old contacts app." },
            { "Microsoft.WindowsFeedbackHub", "Feedback Hub", "Sends feedback to Microsoft." },
            { "Microsoft.ZuneVideo", "Movies & TV", "Old video app." },
            { "Microsoft.ZuneMusic", "Media Player", "Windows' music and video player. Remove only if you use another player (like VLC)." },
            { "Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) ad app", "Advertises Office. Your installed Office keeps working." },
            { "Microsoft.PowerAutomateDesktop", "Power Automate", "Business automation tool." },
            { "Microsoft.549981C3F5F10", "Cortana", "Old voice assistant." },
            { "Microsoft.Todos", "Microsoft To Do", "To-do list. Remove if you don't use it." },
            { "Microsoft.MixedReality.Portal", "Mixed Reality Portal", "For VR headsets." },
            { "Microsoft.SkypeApp", "Skype", "Discontinued by Microsoft." },
            { "MicrosoftTeams", "Teams (personal chat)", "The built-in personal Teams chat." },
            { "Clipchamp.Clipchamp", "Clipchamp", "Video editor. Remove if you don't edit videos with it." },
            { "MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", "Parental controls app." },
            { "Microsoft.BingSearch", "Bing Search", "Bing app." },
            { "SpotifyAB.SpotifyMusic", "Spotify", "Preinstalled promo copy. Remove if you don't use Spotify." },
            { "Disney.37853FC22B2CE", "Disney+", "Preinstalled promo app." },
            { "BytedancePte.Ltd.TikTok", "TikTok", "Preinstalled promo app." },
            { "AmazonVideo.PrimeVideo", "Prime Video", "Preinstalled promo app." },
            { "Facebook.Facebook", "Facebook", "Preinstalled promo app." },
            { "Facebook.InstagramBeta", "Instagram", "Preinstalled promo app." },
            { "7EE7776C.LinkedInforWindows", "LinkedIn", "Preinstalled promo app." },
            { "king.com.CandyCrushSaga", "Candy Crush Saga", "Preinstalled game with ads." },
            { "king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga", "Preinstalled game with ads." },
            { "king.com.BubbleWitch3Saga", "Bubble Witch 3", "Preinstalled game with ads." },
        };

        // Trial and promo programs that laptop makers preinstall.
        static readonly string[] DesktopJunkWords = { "mcafee", "norton", "wildtangent", "expressvpn", "booking.com", "dropbox promotion" };

        public static List<JunkApp> Find()
        {
            List<JunkApp> list = new List<JunkApp>();
            Shell.Result r = Shell.PowerShell("Get-AppxPackage | ForEach-Object { $_.Name + '|' + $_.PackageFullName }", 90000);
            if (r.Ok)
            {
                Dictionary<string, string> installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in r.Output.Split('\n'))
                {
                    string line = raw.Trim();
                    int bar = line.IndexOf('|');
                    if (bar > 0 && !installed.ContainsKey(line.Substring(0, bar))) installed[line.Substring(0, bar)] = line.Substring(bar + 1);
                }
                for (int i = 0; i < StoreJunk.GetLength(0); i++)
                {
                    string full;
                    if (installed.TryGetValue(StoreJunk[i, 0], out full))
                        list.Add(new JunkApp { Title = StoreJunk[i, 1], Note = StoreJunk[i, 2] + " Can be reinstalled from the Microsoft Store.", PackageFullName = full });
                }
            }

            HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FindDesktop(list, seenNames, RegistryHive.LocalMachine, RegistryView.Registry64);
            FindDesktop(list, seenNames, RegistryHive.LocalMachine, RegistryView.Registry32);
            FindDesktop(list, seenNames, RegistryHive.CurrentUser, RegistryView.Default);
            return list;
        }

        static void FindDesktop(List<JunkApp> list, HashSet<string> seen, RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", false))
                {
                    if (un == null) return;
                    foreach (string sub in un.GetSubKeyNames())
                    {
                        using (RegistryKey k = un.OpenSubKey(sub, false))
                        {
                            if (k == null) continue;
                            string name = Convert.ToString(k.GetValue("DisplayName", ""));
                            string cmd = Convert.ToString(k.GetValue("UninstallString", ""));
                            if (name.Length == 0 || cmd.Length == 0 || seen.Contains(name)) continue;
                            string lower = name.ToLowerInvariant();
                            foreach (string w in DesktopJunkWords)
                            {
                                if (!lower.Contains(w)) continue;
                                seen.Add(name);
                                list.Add(new JunkApp { Title = name, Note = "Often a preinstalled trial. Keep it if you paid for it; Windows Security already protects you for free. Its own uninstaller opens.", UninstallCommand = cmd });
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("junk desktop: " + ex.Message); }
        }

        // Returns null on success, or a message.
        public static string Remove(JunkApp app)
        {
            if (app.IsStoreApp)
            {
                Shell.Result r = Shell.PowerShell("Remove-AppxPackage -Package '" + app.PackageFullName.Replace("'", "''") + "' -ErrorAction Stop", 180000);
                if (r.Ok) { Log.Write("removed " + app.PackageFullName); return null; }
                return "Windows didn't allow removing it.";
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + app.UninstallCommand + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ------------------------------------------------------------------ free up space

    class CleanArea
    {
        public string Title;
        public string Note;
        public List<string> Folders = new List<string>();
        public bool OnlyOld = true;      // only files older than a day (they might still be in use)
        public long Bytes;
    }

    static class Cleaner
    {
        public static List<CleanArea> Areas()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            List<CleanArea> list = new List<CleanArea>();
            CleanArea temp = new CleanArea { Title = "Temporary files", Note = "Leftovers apps forgot to delete." };
            temp.Folders.Add(Path.GetTempPath());
            temp.Folders.Add(Path.Combine(win, "Temp"));
            list.Add(temp);

            CleanArea wu = new CleanArea { Title = "Windows Update leftovers", Note = "Update files that were already installed." };
            wu.Folders.Add(Path.Combine(win, @"SoftwareDistribution\Download"));
            list.Add(wu);

            CleanArea crash = new CleanArea { Title = "Crash reports", Note = "Error reports and memory dumps from old crashes.", OnlyOld = false };
            crash.Folders.Add(Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive"));
            crash.Folders.Add(Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"));
            crash.Folders.Add(Path.Combine(local, "CrashDumps"));
            crash.Folders.Add(Path.Combine(win, "Minidump"));
            list.Add(crash);

            // Only the old web cache itself: the rest of INetCache holds Outlook attachments and Office files.
            CleanArea web = new CleanArea { Title = "Old web cache", Note = "Windows' old internet cache. Rebuilt when needed." };
            web.Folders.Add(Path.Combine(local, @"Microsoft\Windows\INetCache\IE"));
            web.Folders.Add(Path.Combine(local, @"Microsoft\Windows\INetCache\Low\IE"));
            list.Add(web);

            foreach (CleanArea a in list)
            {
                List<string> safe = new List<string>();
                foreach (string f in a.Folders)
                {
                    string full;
                    try { full = Path.GetFullPath(f).TrimEnd('\\'); } catch { continue; }
                    if (!IsSafeFolder(full)) { Log.Write("cleaner skipped unsafe folder: " + full); continue; }
                    bool dup = false; // e.g. TEMP pointing at C:\Windows\Temp: count and clean it once
                    foreach (string g in safe) if (string.Equals(g, full, StringComparison.OrdinalIgnoreCase)) dup = true;
                    if (!dup) safe.Add(full);
                }
                a.Folders = safe;
            }
            return list;
        }

        // Never clean a drive, the user's profile, Windows, Program Files or anything that isn't a
        // temp/cache folder (guards against an odd TEMP setting like "D:\").
        static bool IsSafeFolder(string full)
        {
            if (full.Length < 4) return false;
            string root = Path.GetPathRoot(full) ?? "";
            if (string.Equals(full + "\\", root, StringComparison.OrdinalIgnoreCase) || string.Equals(full, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            string[] never =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            foreach (string n in never)
                if (n.Length > 0 && string.Equals(full, n.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(full).ToLowerInvariant();
            string[] allowed = { "temp", "tmp", "download", "reportarchive", "reportqueue", "crashdumps", "minidump", "ie" };
            return Array.IndexOf(allowed, name) >= 0;
        }

        public static void Measure(CleanArea a)
        {
            long total = 0;
            DateTime cutoff = DateTime.Now.AddDays(-1);
            foreach (string f in a.Folders) Walk(f, a.OnlyOld ? cutoff : DateTime.MaxValue, false, ref total);
            a.Bytes = total;
        }

        public static long Clean(CleanArea a)
        {
            long freed = 0;
            DateTime cutoff = DateTime.Now.AddDays(-1);
            foreach (string f in a.Folders) Walk(f, a.OnlyOld ? cutoff : DateTime.MaxValue, true, ref freed);
            Log.Write("cleaned " + a.Title + ": " + freed + " bytes");
            return freed;
        }

        // Goes through a folder; never follows links into other folders (so it can't wander outside).
        static void Walk(string folder, DateTime olderThan, bool delete, ref long bytes)
        {
            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(folder);
                if (!dir.Exists) return;
            }
            catch { return; }

            FileInfo[] files;
            try { files = dir.GetFiles(); } catch { files = new FileInfo[0]; }
            foreach (FileInfo f in files)
            {
                try
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (f.LastWriteTime > olderThan && olderThan != DateTime.MaxValue) continue;
                    long len = f.Length;
                    if (delete)
                    {
                        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                        f.Delete();
                    }
                    bytes += len;
                }
                catch { } // in use - skip
            }

            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); } catch { subs = new DirectoryInfo[0]; }
            foreach (DirectoryInfo d in subs)
            {
                try
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (d.Name.StartsWith("Content.", StringComparison.OrdinalIgnoreCase)) continue; // Outlook / Office files
                    Walk(d.FullName, olderThan, delete, ref bytes);
                    if (delete && d.GetFileSystemInfos().Length == 0) d.Delete();
                }
                catch { }
            }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";
            if (bytes > 0) return Math.Max(1, bytes / 1024) + " KB";
            return "nothing";
        }
    }

    // ------------------------------------------------------------------ battery & updates

    static class BatteryInfo
    {
        public static bool HasBattery
        {
            get { return SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery; }
        }

        public static bool OnBattery
        {
            get { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
        }

        // Battery health: how much charge it can hold now compared with when it was new.
        public static string Health()
        {
            Shell.Result r = Shell.PowerShell(
                "$d = (Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData -ErrorAction Stop | Select-Object -First 1).DesignedCapacity; " +
                "$f = (Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity -ErrorAction Stop | Select-Object -First 1).FullChargedCapacity; " +
                "'{0}|{1}' -f $d, $f", 60000);
            string[] p = r.Output.Trim().Split('|');
            double design, full;
            if (!r.Ok || p.Length != 2 || !double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out design) ||
                !double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out full) || design <= 0)
                return "Windows didn't report the battery's capacity. The full report below may have it.";
            int pct = (int)Math.Round(100 * full / design);
            string verdict = pct >= 80 ? "Healthy." : pct >= 60 ? "Worn, but fine for now." : "Quite worn. A new battery would last much longer.";
            return "Your battery holds " + pct + "% of what it did when new (" + (full / 1000).ToString("0.0", CultureInfo.CurrentCulture) +
                   " of " + (design / 1000).ToString("0.0", CultureInfo.CurrentCulture) + " Wh). " + verdict;
        }

        public static string FullReport()
        {
            // In your own temp folder, so your normal (non-admin) browser can open it.
            string path = Path.Combine(Path.GetTempPath(), "QuietPC-battery-report.html");
            Shell.Run(Paths.System32("powercfg.exe"), "/batteryreport /output \"" + path + "\"", 60000);
            return File.Exists(path) ? path : null;
        }

        public static string LastWake()
        {
            Shell.Result a = Shell.Run(Paths.System32("powercfg.exe"), "/lastwake", 15000);
            Shell.Result b = Shell.Run(Paths.System32("powercfg.exe"), "/waketimers", 15000);
            return (a.Output.Trim() + "\r\n\r\n" + b.Output.Trim()).Trim();
        }
    }

    static class WindowsUpdates
    {
        // Lifts the pause and opens Windows Update, which starts checking straight away.
        public static void UpdateNow()
        {
            if (Catalog.PauseUpdates.IsOn() || Catalog.PauseUpdates.HasBackup()) Engine.Revert(Catalog.PauseUpdates);
            Store.Set("lastUpdateNow", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            try { Process.Start("ms-settings:windowsupdate-action"); }
            catch { try { Process.Start("ms-settings:windowsupdate"); } catch { } }
        }

        public static int DaysPaused()
        {
            DateTime since;
            if (DateTime.TryParse(Store.Get("pausedSince"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out since))
                return (int)(DateTime.UtcNow - since.ToUniversalTime()).TotalDays;
            return 0;
        }
    }

    static class ExplorerTools
    {
        // Restarts the taskbar and File Explorer for this user only. If Windows doesn't bring them back
        // by itself, they're started WITHOUT administrator rights (through a one-time scheduled task),
        // so the desktop never ends up running as administrator.
        public static void Restart()
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { if (p.SessionId == session) { p.Kill(); p.WaitForExit(5000); } } catch { }
                finally { p.Dispose(); }
            }
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (ExplorerRunning(session)) return;
            }
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            const string task = "QuietPC-RestartExplorer";
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>4</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + System.Security.SecurityElement.Escape(explorer) + "\"</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
            Paths.EnsureDataDir();
            string file = Path.Combine(Paths.DataDir, "explorer-task.xml");
            try
            {
                File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
                Shell.Run(Paths.System32("schtasks.exe"), "/Create /TN " + task + " /XML \"" + file + "\" /F", 15000);
                Shell.Run(Paths.System32("schtasks.exe"), "/Run /TN " + task, 15000);
                System.Threading.Thread.Sleep(2000);
            }
            finally
            {
                Shell.Run(Paths.System32("schtasks.exe"), "/Delete /TN " + task + " /F", 15000);
                try { File.Delete(file); } catch { }
            }
        }

        static bool ExplorerRunning(int session)
        {
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { if (p.SessionId == session) return true; } catch { }
                finally { p.Dispose(); }
            }
            return false;
        }
    }
}
