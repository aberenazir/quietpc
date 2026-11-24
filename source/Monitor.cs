// "What's making my fan spin?" - measures which programs use the processor, in plain English.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QuietPC
{
    class BusyItem
    {
        public string Key;          // process name, lower case
        public string Title;        // plain-English name
        public string Why;          // what it is doing
        public double Cpu;          // percent of the whole processor
        public bool CanEnd;         // safe to offer "End"
        public List<int> Pids = new List<int>();
    }

    static class Friendly
    {
        class Info
        {
            public string Title, Why;
            public bool Protected;
            public Info(string title, string why, bool isProtected) { Title = title; Why = why; Protected = isProtected; }
        }

        static readonly Dictionary<string, Info> known = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void K(string name, string title, string why, bool isProtected = true)
        {
            known[name] = new Info(title, why, isProtected);
        }

        static Friendly()
        {
            K("msmpeng", "Windows Security virus scan", "Checking files for viruses. Normal, and it finishes on its own. \"Gentler virus scans\" makes it lighter.");
            K("mpdefendercoreservice", "Windows Security", "Part of the antivirus. Normal.");
            K("nissrv", "Windows Security network check", "Watches network traffic for attacks. Normal.");
            K("searchindexer", "Windows Search indexing", "Building the search index of your files. \"Pause file indexing on battery\" helps.");
            K("searchprotocolhost", "Windows Search indexing", "Reading files for the search index.");
            K("searchfilterhost", "Windows Search indexing", "Reading files for the search index.");
            K("searchhost", "Start menu search", "The search box in Start.");
            K("tiworker", "Windows Update installing", "Installing updates. Heavy, but it finishes. Pausing updates stops this.");
            K("trustedinstaller", "Windows Update installing", "Installing updates or Windows features.");
            K("mousocoreworker", "Windows Update", "Checking for or downloading updates. Pausing updates stops this.");
            K("usocoreworker", "Windows Update", "Checking for or downloading updates.");
            K("wuauclt", "Windows Update", "Checking for or downloading updates.");
            K("compattelrunner", "Microsoft compatibility check", "Telemetry that scans your apps. \"Less background tracking\" turns it off.");
            K("devicecensus", "Microsoft device report", "Telemetry. \"Less background tracking\" reduces it.");
            K("svchost", "Windows background services", "Many small Windows services grouped together.");
            K("system", "Windows itself (drivers)", "Drivers and the core of Windows. High use can mean a driver problem.");
            K("registry", "Windows registry", "Normal.");
            K("memory compression", "Memory compression", "Windows squeezing memory to fit more. Normal when RAM is full.");
            K("dwm", "Screen drawing", "Draws everything you see. Normal.");
            K("csrss", "Windows core", "Normal.");
            K("winlogon", "Windows sign-in", "Normal.");
            K("lsass", "Windows security accounts", "Normal.");
            K("services", "Windows services manager", "Normal.");
            K("explorer", "File Explorer and taskbar", "The taskbar, Start and folders. Restarting it can fix glitches.");
            K("sihost", "Windows shell", "Normal.");
            K("ctfmon", "Keyboard and typing", "Normal.");
            K("fontdrvhost", "Fonts", "Normal.");
            K("taskhostw", "Windows tasks", "Runs scheduled background jobs.");
            K("runtimebroker", "App permissions helper", "Checks what Store apps may do.");
            K("startmenuexperiencehost", "Start menu", "Normal.");
            K("shellexperiencehost", "Taskbar panels", "Normal.");
            K("textinputhost", "Touch keyboard and emoji panel", "Normal.");
            K("audiodg", "Sound processing", "Sound effects and audio. High use can mean an audio driver problem.");
            K("wmiprvse", "System information service", "Often busy because a laptop maker's tool keeps asking for information.");
            K("spoolsv", "Printing", "Printer service.");
            K("securityhealthservice", "Windows Security", "Normal.");
            K("securityhealthsystray", "Windows Security icon", "Normal.");
            K("smartscreen", "SmartScreen", "Checks downloads for danger. Normal.");
            K("widgets", "Widgets", "News and weather panel. \"Turn off Widgets\" removes it.", false);
            K("widgetservice", "Widgets", "News and weather panel. \"Turn off Widgets\" removes it.", false);
            K("phoneexperiencehost", "Phone Link", "Connects your phone.", false);
            K("onedrive", "OneDrive syncing", "Uploading or downloading your files. It calms down once synced.", false);
            K("msedge", "Microsoft Edge", "Your browser. Many open tabs use a lot.", false);
            K("msedgewebview2", "Web parts inside apps", "Apps like Widgets, Teams and Outlook use this to show web pages.", false);
            K("chrome", "Google Chrome", "Your browser. Many open tabs use a lot.", false);
            K("firefox", "Firefox", "Your browser.", false);
            K("ms-teams", "Microsoft Teams", "Uses a lot even in the background.", false);
            K("teams", "Microsoft Teams", "Uses a lot even in the background.", false);
            K("officeclicktorun", "Microsoft Office updating", "Office keeping itself up to date.");
            K("quietpc", "QuietPC", "This app.");
        }

        public static string Title(string name, string path)
        {
            Info i;
            if (known.TryGetValue(name, out i)) return i.Title;
            string d;
            if (descriptions.TryGetValue(name, out d)) return d;
            d = name;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) d = desc.Trim();
                }
            }
            catch { }
            descriptions[name] = d;
            return d;
        }

        public static string Why(string name)
        {
            Info i;
            return known.TryGetValue(name, out i) ? i.Why : "";
        }

        public static bool IsProtected(string name)
        {
            Info i;
            return known.TryGetValue(name, out i) && i.Protected;
        }
    }

    class ProcessSampler
    {
        readonly Dictionary<int, long> lastTimes = new Dictionary<int, long>();
        readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastWall = -1;
        long lastIdle, lastKernel, lastUser;
        readonly int myPid = Process.GetCurrentProcess().Id;
        readonly int mySession = Process.GetCurrentProcess().SessionId;
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        public double TotalCpu;

        // Returns the busiest programs since the last call (call every 1-3 seconds).
        public List<BusyItem> Sample()
        {
            long wall = clock.ElapsedTicks;
            double wallSeconds = lastWall < 0 ? 0 : (wall - lastWall) / (double)Stopwatch.Frequency;
            lastWall = wall;
            int cores = Math.Max(1, Environment.ProcessorCount);

            Native.FILETIME idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                long di = idle.Value - lastIdle, dk = kernel.Value - lastKernel, du = user.Value - lastUser;
                if (lastKernel != 0 && dk + du > 0) TotalCpu = Math.Max(0, Math.Min(100, 100.0 * (dk + du - di) / (dk + du)));
                lastIdle = idle.Value; lastKernel = kernel.Value; lastUser = user.Value;
            }

            Dictionary<string, BusyItem> groups = new Dictionary<string, BusyItem>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> seen = new HashSet<int>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return new List<BusyItem>(); }

            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == 0 || pid == myPid) continue;
                    string name = p.ProcessName;
                    int session = 0;
                    try { session = p.SessionId; } catch { }

                    long cpuTime;
                    string path;
                    if (!ReadTimes(pid, name, out cpuTime, out path)) continue;
                    seen.Add(pid);

                    long before;
                    double percent = 0;
                    if (wallSeconds > 0 && lastTimes.TryGetValue(pid, out before) && cpuTime >= before)
                        percent = 100.0 * ((cpuTime - before) / 1e7) / (wallSeconds * cores);
                    lastTimes[pid] = cpuTime;

                    BusyItem item;
                    if (!groups.TryGetValue(name, out item))
                    {
                        item = new BusyItem();
                        item.Key = name.ToLowerInvariant();
                        item.Title = Friendly.Title(name, path);
                        item.Why = Friendly.Why(name);
                        // Only your own apps: not Windows' own programs, not services, not other users' programs.
                        item.CanEnd = !Friendly.IsProtected(name) && !string.IsNullOrEmpty(path) &&
                                      !path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
                        groups[name] = item;
                    }
                    item.Cpu += percent;
                    if (session == mySession) item.Pids.Add(pid);
                }
                catch { }
                finally { p.Dispose(); }
            }

            // forget processes that have ended
            List<int> gone = new List<int>();
            foreach (int pid in lastTimes.Keys) if (!seen.Contains(pid)) gone.Add(pid);
            foreach (int pid in gone) lastTimes.Remove(pid);

            List<BusyItem> list = new List<BusyItem>(groups.Values);
            foreach (BusyItem b in list) if (b.Pids.Count == 0) b.CanEnd = false;
            list.Sort(delegate(BusyItem a, BusyItem b) { return b.Cpu.CompareTo(a.Cpu); });
            return list;
        }

        // Uses the "limited" access level so even protected processes (like the antivirus) can be measured.
        bool ReadTimes(int pid, string name, out long cpuTime, out string path)
        {
            cpuTime = 0;
            path = null;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                Native.FILETIME c, e, k, u;
                if (!Native.GetProcessTimes(h, out c, out e, out k, out u)) return false;
                cpuTime = k.Value + u.Value;
                if (!paths.TryGetValue(name, out path))
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    path = Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
                    paths[name] = path;
                }
                return true;
            }
            finally { Native.CloseHandle(h); }
        }
    }
}
