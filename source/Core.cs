// QuietPC - calms Windows down: updates only when you say so, a quieter fan,
// longer battery, and no ads or nags. Every change can be undone.
//
// Core pieces: app info, Windows functions, the log, saved settings and the undo backup.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QuietPC
{
    static class AppInfo
    {
        public const string Name = "QuietPC";
        public const string Version = "1.0.0";
        public const string Repo = "aberenazir/quietpc";
        public const string VersionUrl = "https://github.com/" + Repo + "/releases/latest/download/version.txt";

        public static string SetupUrl(Version v)
        {
            return "https://github.com/" + Repo + "/releases/download/v" + v + "/QuietPC-Setup.exe";
        }

        public static Version Current { get { return new Version(Version); } }
    }

    static class Paths
    {
        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuietPC"); }
        }

        // Creates the data folder and locks it to administrators, so nothing without admin
        // rights can plant files that QuietPC (which runs as administrator) would trust.
        public static void EnsureDataDir()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                System.Security.AccessControl.DirectorySecurity ds = new System.Security.AccessControl.DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);
                System.Security.AccessControl.InheritanceFlags inherit =
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                foreach (System.Security.Principal.WellKnownSidType sid in new[] {
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.Principal.WellKnownSidType.LocalSystemSid })
                {
                    ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(sid, null),
                        System.Security.AccessControl.FileSystemRights.FullControl, inherit,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                }
                ds.SetOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
                Directory.SetAccessControl(DataDir, ds);
            }
            catch (Exception ex) { Log.Write("data folder lock: " + ex.Message); }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QuietPC"); }
        }

        public static string InstalledExe
        {
            get { return Path.Combine(InstallDir, "QuietPC.exe"); }
        }

        public static string System32(string exe)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint Low;
            public uint High;
            public long Value { get { return ((long)High << 32) | Low; } }
        }

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(IntPtr h, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // Rounded corners on Windows 11 (ignored on Windows 10).
        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            }
            catch { }
        }

        // Dark title bar to match the app.
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    string path = Path.Combine(Paths.DataDir, "log.txt");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 500000) fi.Delete();
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  v" + AppInfo.Version + "  " + message + "\r\n");
                }
            }
            catch { }
        }
    }

    // Runs Windows' own command-line tools (powercfg, schtasks, PowerShell) without showing a window.
    static class Shell
    {
        public class Result
        {
            public int ExitCode;
            public string Output = "";
            public bool Ok { get { return ExitCode == 0; } }
        }

        public static Result Run(string exe, string args, int timeoutMs = 60000)
        {
            Result r = new Result();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.ExitCode = -1;
                        Log.Write("timed out: " + Path.GetFileName(exe) + " " + args);
                        return r;
                    }
                    p.WaitForExit();
                    r.ExitCode = p.ExitCode;
                    lock (sb) r.Output = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                r.ExitCode = -1;
                r.Output = ex.Message;
            }
            if (!r.Ok) Log.Write(Path.GetFileName(exe) + " " + args + " -> " + r.ExitCode + " " + r.Output.Trim());
            return r;
        }

        public static Result PowerShell(string command, int timeoutMs = 120000)
        {
            // Encoded so quotes and special characters can never break the command.
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command));
            return Run(Paths.System32(@"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMs);
        }
    }

    // Small key=value file for the app's own settings.
    static class Store
    {
        static readonly object gate = new object();
        static Dictionary<string, string> values;

        static string FilePath { get { return Path.Combine(Paths.DataDir, "settings.txt"); } }

        static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
        }

        public static string Get(string key, string fallback = "")
        {
            lock (gate)
            {
                Load();
                string v;
                return values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static void Set(string key, string value)
        {
            lock (gate)
            {
                Load();
                if (value == null) values.Remove(key); else values[key] = value;
                try
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                    File.WriteAllLines(FilePath, lines.ToArray());
                }
                catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
            }
        }
    }

    // Remembers what every setting was BEFORE QuietPC changed it, so "Undo" can put it back exactly.
    // Only the first (original) value is ever stored.
    static class Backup
    {
        static readonly object gate = new object();
        static Dictionary<string, string> values;

        static string FilePath { get { return Path.Combine(Paths.DataDir, "backup.txt"); } }

        // If the file can't be read, nothing is saved over it (that would lose the originals):
        // changes are refused until it can be read again.
        static void Load()
        {
            if (values != null) return;
            Dictionary<string, string> loaded = new Dictionary<string, string>();
            if (File.Exists(FilePath))
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) loaded[line.Substring(0, tab)] = Unescape(line.Substring(tab + 1));
                }
            }
            values = loaded;
        }

        static void LoadOrThrow()
        {
            try { Load(); }
            catch (Exception ex)
            {
                Log.Write("backup load: " + ex.Message);
                throw new Exception("QuietPC couldn't read its undo list, so it made no change. Please try again in a moment.");
            }
        }

        static void Save()
        {
            Directory.CreateDirectory(Paths.DataDir);
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in values) sb.Append(kv.Key).Append('\t').Append(Escape(kv.Value)).Append("\r\n");
            byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
            string temp = FilePath + ".tmp";
            using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true); // really on disk before swapping
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
            else File.Move(temp, FilePath);
        }

        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }

        static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 't' ? '\t' : c == 'r' ? '\r' : c == 'n' ? '\n' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // Saves the original value before a change. Throws if it can't be saved, so the change is never made.
        public static void SaveOnce(string key, string original)
        {
            lock (gate)
            {
                LoadOrThrow();
                if (values.ContainsKey(key)) return;
                values[key] = original;
                try { Save(); }
                catch (Exception ex)
                {
                    values.Remove(key);
                    Log.Write("backup save: " + ex.Message);
                    throw new Exception("QuietPC couldn't save its undo list, so it made no change.");
                }
            }
        }

        public static bool TryGet(string key, out string original)
        {
            lock (gate)
            {
                original = null;
                try { Load(); } catch (Exception ex) { Log.Write("backup load: " + ex.Message); return false; }
                return values.TryGetValue(key, out original);
            }
        }

        public static bool Has(string key)
        {
            string unused;
            return TryGet(key, out unused);
        }

        public static bool HasPrefix(string prefix)
        {
            return KeysWithPrefix(prefix).Count > 0;
        }

        public static List<string> KeysWithPrefix(string prefix)
        {
            lock (gate)
            {
                List<string> list = new List<string>();
                try { Load(); } catch (Exception ex) { Log.Write("backup load: " + ex.Message); return list; }
                foreach (string k in values.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) list.Add(k);
                return list;
            }
        }

        // Throws if the undo list exists but can't be read.
        public static void EnsureReadable()
        {
            lock (gate) LoadOrThrow();
        }

        public static int Count
        {
            get { return KeysWithPrefix("").Count; }
        }

        public static void Remove(string key)
        {
            lock (gate)
            {
                try
                {
                    Load();
                    if (values.Remove(key)) Save();
                }
                catch (Exception ex) { Log.Write("backup remove: " + ex.Message); }
            }
        }
    }

    // Registry helpers that always use the real 64-bit registry.
    static class Reg
    {
        public static RegistryKey Root(RegistryHive hive)
        {
            return RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        }

        public static object Read(RegistryHive hive, string path, string name)
        {
            try
            {
                using (RegistryKey root = Root(hive))
                using (RegistryKey k = root.OpenSubKey(path, false))
                    return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch { return null; }
        }

        public static RegistryValueKind Kind(RegistryHive hive, string path, string name)
        {
            try
            {
                using (RegistryKey root = Root(hive))
                using (RegistryKey k = root.OpenSubKey(path, false))
                    return k == null ? RegistryValueKind.Unknown : k.GetValueKind(name);
            }
            catch { return RegistryValueKind.Unknown; }
        }

        public static bool KeyExists(RegistryHive hive, string path)
        {
            try
            {
                using (RegistryKey root = Root(hive))
                using (RegistryKey k = root.OpenSubKey(path, false))
                    return k != null;
            }
            catch { return false; }
        }

        public static void Write(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
        {
            using (RegistryKey root = Root(hive))
            using (RegistryKey k = root.CreateSubKey(path, true))
                k.SetValue(name, value, kind);
        }

        public static void Delete(RegistryHive hive, string path, string name)
        {
            try
            {
                using (RegistryKey root = Root(hive))
                using (RegistryKey k = root.OpenSubKey(path, true))
                    if (k != null && k.GetValue(name) != null) k.DeleteValue(name, false);
            }
            catch (Exception ex) { Log.Write("reg delete " + path + "\\" + name + ": " + ex.Message); }
        }

        public static void DeleteTree(RegistryHive hive, string path)
        {
            try
            {
                using (RegistryKey root = Root(hive)) root.DeleteSubKeyTree(path, false);
            }
            catch (Exception ex) { Log.Write("reg delete tree " + path + ": " + ex.Message); }
        }
    }
}
