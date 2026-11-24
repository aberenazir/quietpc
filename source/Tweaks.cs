// Every change QuietPC can make is a "tweak": it can tell whether it's on, switch on (saving the
// original values first) and switch off (putting the originals back exactly).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QuietPC
{
    static class Sections
    {
        public const string Updates = "updates";
        public const string Fan = "fan";
        public const string Cleanup = "cleanup";
    }

    abstract class Tweak
    {
        public string Id;
        public string Title;
        public string Description;
        public string Warning = "";
        public string Section;
        public bool Recommended;
        public bool NeedsExplorerRestart;

        public abstract bool IsOn();
        public abstract void Apply();
        public abstract void Revert();

        public virtual bool HasBackup() { return Backup.HasPrefix(Id + "|"); }
        public virtual bool IsAvailable() { return true; }
    }

    class RegSet
    {
        public RegistryHive Hive;
        public string Path;
        public string Name;
        public object Value;
        public RegistryValueKind Kind;

        public RegSet(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
        {
            Hive = hive; Path = path; Name = name; Value = value; Kind = kind;
        }

        public static RegSet Dword(RegistryHive hive, string path, string name, int value)
        {
            return new RegSet(hive, path, name, value, RegistryValueKind.DWord);
        }

        public static RegSet Text(RegistryHive hive, string path, string name, string value)
        {
            return new RegSet(hive, path, name, value, RegistryValueKind.String);
        }
    }

    class RegTweak : Tweak
    {
        protected List<RegSet> sets = new List<RegSet>();

        public RegTweak Add(RegSet s) { sets.Add(s); return this; }

        protected virtual List<RegSet> SetsToApply() { return sets; }

        string Key(RegSet s) { return Id + "|reg|" + s.Hive + "|" + s.Path + "|" + s.Name; }

        static bool Same(object current, object target)
        {
            if (current == null) return false;
            if (target is int)
            {
                if (current is int) return (int)current == (int)target;
                return false;
            }
            return string.Equals(Convert.ToString(current, CultureInfo.InvariantCulture),
                                 Convert.ToString(target, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        public override bool IsOn()
        {
            foreach (RegSet s in sets)
                if (!Same(Reg.Read(s.Hive, s.Path, s.Name), s.Value)) return false;
            return sets.Count > 0;
        }

        public override void Apply()
        {
            foreach (RegSet s in SetsToApply())
            {
                object current = Reg.Read(s.Hive, s.Path, s.Name);
                Backup.SaveOnce(Key(s), current == null ? "absent" : Serialize(current, Reg.Kind(s.Hive, s.Path, s.Name)));
                Reg.Write(s.Hive, s.Path, s.Name, s.Value, s.Kind);
            }
        }

        public override void Revert()
        {
            foreach (RegSet s in SetsToApply())
            {
                string original;
                if (!Backup.TryGet(Key(s), out original)) continue;
                try
                {
                    if (original == "absent") Reg.Delete(s.Hive, s.Path, s.Name);
                    else
                    {
                        RegistryValueKind kind;
                        object value = Deserialize(original, out kind);
                        Reg.Write(s.Hive, s.Path, s.Name, value, kind);
                    }
                    Backup.Remove(Key(s));
                }
                catch (Exception ex) { Log.Write("revert " + Id + " " + s.Name + ": " + ex.Message); }
            }
        }

        static string Serialize(object v, RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord: return "dword:" + Convert.ToInt32(v).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord: return "qword:" + Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.ExpandString: return "expand:" + v;
                case RegistryValueKind.MultiString: return "multi:" + string.Join("\u0001", (string[])v);
                case RegistryValueKind.Binary: return "binary:" + BitConverter.ToString((byte[])v).Replace("-", "");
                case RegistryValueKind.String: return "string:" + Convert.ToString(v, CultureInfo.InvariantCulture);
                default:
                    byte[] raw = v as byte[];
                    if (raw != null) return "raw" + (int)kind + ":" + BitConverter.ToString(raw).Replace("-", "");
                    return "string:" + Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        static object Deserialize(string s, out RegistryValueKind kind)
        {
            int colon = s.IndexOf(':');
            string type = s.Substring(0, colon), data = s.Substring(colon + 1);
            switch (type)
            {
                case "dword": kind = RegistryValueKind.DWord; return int.Parse(data, CultureInfo.InvariantCulture);
                case "qword": kind = RegistryValueKind.QWord; return long.Parse(data, CultureInfo.InvariantCulture);
                case "expand": kind = RegistryValueKind.ExpandString; return data;
                case "multi": kind = RegistryValueKind.MultiString; return data.Split('\u0001');
                case "binary":
                    kind = RegistryValueKind.Binary;
                    byte[] b = new byte[data.Length / 2];
                    for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(data.Substring(i * 2, 2), 16);
                    return b;
                default:
                    if (type.StartsWith("raw", StringComparison.Ordinal))
                    {
                        kind = (RegistryValueKind)int.Parse(type.Substring(3), CultureInfo.InvariantCulture);
                        byte[] r = new byte[data.Length / 2];
                        for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(data.Substring(i * 2, 2), 16);
                        return r;
                    }
                    kind = RegistryValueKind.String; return data;
            }
        }
    }

    // Power plan settings, changed with Windows' own powercfg tool.
    class PowerSet
    {
        public string Sub, Setting;
        public bool Ac;
        public int Value;

        public PowerSet(string sub, string setting, bool ac, int value)
        {
            Sub = sub; Setting = setting; Ac = ac; Value = value;
        }
    }

    class PowerTweak : Tweak
    {
        readonly List<PowerSet> sets = new List<PowerSet>();

        public PowerTweak Add(string sub, string setting, bool ac, int value)
        {
            sets.Add(new PowerSet(sub, setting, ac, value));
            return this;
        }

        // The backup key names the power plan, so Undo fixes the plan that was changed even if
        // you (or the laptop maker's tool) switched plans since.
        string Key(string scheme, PowerSet s) { return Id + "|pwr|" + scheme + "|" + s.Sub + "|" + s.Setting + "|" + (s.Ac ? "ac" : "dc"); }

        static readonly Regex Guid = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        static readonly Regex CurrentValue = new Regex(@":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);

        public static string ActiveScheme()
        {
            Shell.Result r = Shell.Run(Paths.System32("powercfg.exe"), "/getactivescheme", 15000);
            Match m = Guid.Match(r.Output);
            return r.Ok && m.Success ? m.Value.ToLowerInvariant() : null;
        }

        // Reads (AC, DC) values of a setting in a power plan. They are the last two lines of
        // powercfg's output ("...: 0x00000064"), in any Windows language.
        public static bool Read(string scheme, string sub, string setting, out int ac, out int dc)
        {
            ac = dc = 0;
            Shell.Result r = Shell.Run(Paths.System32("powercfg.exe"), "/qh " + scheme + " " + sub + " " + setting, 15000);
            if (!r.Ok) return false;
            List<string> lines = new List<string>();
            foreach (string l in r.Output.Split('\n')) if (l.Trim().Length > 0) lines.Add(l.TrimEnd());
            if (lines.Count < 2) return false;
            Match mac = CurrentValue.Match(lines[lines.Count - 2]);
            Match mdc = CurrentValue.Match(lines[lines.Count - 1]);
            if (!mac.Success || !mdc.Success) return false;
            ac = int.Parse(mac.Groups[1].Value, NumberStyles.HexNumber);
            dc = int.Parse(mdc.Groups[1].Value, NumberStyles.HexNumber);
            return true;
        }

        static bool Write(string scheme, PowerSet s, int value)
        {
            return Shell.Run(Paths.System32("powercfg.exe"),
                (s.Ac ? "/setacvalueindex " : "/setdcvalueindex ") + scheme + " " + s.Sub + " " + s.Setting + " " + value, 15000).Ok;
        }

        static void Activate()
        {
            Shell.Run(Paths.System32("powercfg.exe"), "/setactive SCHEME_CURRENT", 15000);
        }

        public override bool IsOn()
        {
            string scheme = ActiveScheme();
            if (scheme == null || sets.Count == 0) return false;
            bool any = false;
            foreach (PowerSet s in sets)
            {
                int ac, dc;
                if (!Read(scheme, s.Sub, s.Setting, out ac, out dc)) continue; // not on this PC
                any = true;
                if ((s.Ac ? ac : dc) != s.Value) return false;
            }
            return any;
        }

        public override bool IsAvailable()
        {
            string scheme = ActiveScheme();
            if (scheme == null) return false;
            int ac, dc;
            foreach (PowerSet s in sets) if (Read(scheme, s.Sub, s.Setting, out ac, out dc)) return true;
            return false;
        }

        public override void Apply()
        {
            string scheme = ActiveScheme();
            if (scheme == null) throw new Exception("Windows didn't say which power plan is in use.");
            bool any = false;
            foreach (PowerSet s in sets)
            {
                int ac, dc;
                if (!Read(scheme, s.Sub, s.Setting, out ac, out dc)) continue;
                Backup.SaveOnce(Key(scheme, s), (s.Ac ? ac : dc).ToString(CultureInfo.InvariantCulture));
                if (!Write(scheme, s, s.Value)) throw new Exception("Windows didn't accept the power setting.");
                any = true;
            }
            if (!any) throw new Exception("This laptop doesn't offer this setting.");
            Activate();
        }

        public override void Revert()
        {
            foreach (string key in Backup.KeysWithPrefix(Id + "|pwr|"))
            {
                string[] p = key.Substring(Id.Length + 5).Split('|'); // scheme|sub|setting|ac
                string original;
                if (p.Length != 4 || !Backup.TryGet(key, out original)) continue;
                PowerSet s = new PowerSet(p[1], p[2], p[3] == "ac", 0);
                int value;
                if (!int.TryParse(original, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) continue;
                if (Write(p[0], s, value)) Backup.Remove(key);
                else Log.Write("power revert failed, keeping the original for later: " + key);
            }
            Activate();
        }
    }

    // Scheduled tasks that run in the background (for example Microsoft's telemetry checks).
    class TaskTweak : Tweak
    {
        readonly List<string> tasks = new List<string>();

        public TaskTweak Add(string task) { tasks.Add(task); return this; }

        string Key(string task) { return Id + "|task|" + task; }

        // null = task doesn't exist on this PC
        static bool? Enabled(string task)
        {
            Shell.Result r = Shell.Run(Paths.System32("schtasks.exe"), "/Query /TN \"" + task + "\" /XML", 15000);
            if (!r.Ok) return null;
            string xml = r.Output;
            int a = xml.IndexOf("<Settings>", StringComparison.Ordinal);
            int b = xml.IndexOf("</Settings>", StringComparison.Ordinal);
            if (a < 0 || b < a) return true;
            return xml.Substring(a, b - a).IndexOf("<Enabled>false</Enabled>", StringComparison.Ordinal) < 0;
        }

        public override bool IsOn()
        {
            bool any = false;
            foreach (string t in tasks)
            {
                bool? e = Enabled(t);
                if (e == null) continue;
                any = true;
                if (e.Value) return false;
            }
            return any;
        }

        public override void Apply()
        {
            foreach (string t in tasks)
            {
                bool? e = Enabled(t);
                if (e == null) continue;
                Backup.SaveOnce(Key(t), e.Value ? "1" : "0");
                if (e.Value) Shell.Run(Paths.System32("schtasks.exe"), "/Change /TN \"" + t + "\" /Disable", 15000);
            }
        }

        public override void Revert()
        {
            foreach (string t in tasks)
            {
                string original;
                if (!Backup.TryGet(Key(t), out original)) continue;
                bool ok = original != "1" || Shell.Run(Paths.System32("schtasks.exe"), "/Change /TN \"" + t + "\" /Enable", 15000).Ok
                          || Enabled(t) == null; // task no longer exists: nothing to restore
                if (ok) Backup.Remove(Key(t));
            }
        }
    }

    // Several tweaks shown as one switch.
    class MultiTweak : Tweak
    {
        readonly List<Tweak> parts = new List<Tweak>();
        readonly List<string> partIds = new List<string>();

        // Each part has a fixed name, so later versions can add parts without losing old undo data.
        public MultiTweak Add(string partId, Tweak t)
        {
            parts.Add(t);
            partIds.Add(partId);
            return this;
        }

        public void NameParts()
        {
            for (int i = 0; i < parts.Count; i++) parts[i].Id = Id + "." + partIds[i];
        }

        public override bool IsOn()
        {
            foreach (Tweak t in parts) if (!t.IsOn()) return false;
            return parts.Count > 0;
        }

        public override void Apply()
        {
            List<string> errors = new List<string>();
            foreach (Tweak t in parts)
            {
                try { t.Apply(); }
                catch (Exception ex) { errors.Add(ex.Message); Log.Write("apply " + t.Id + ": " + ex); }
            }
            if (errors.Count == parts.Count && errors.Count > 0) throw new Exception(errors[0]);
        }

        public override void Revert()
        {
            foreach (Tweak t in parts)
            {
                try { t.Revert(); }
                catch (Exception ex) { Log.Write("revert " + t.Id + ": " + ex); }
            }
        }

        public override bool HasBackup()
        {
            foreach (Tweak t in parts) if (t.HasBackup()) return true;
            return false;
        }
    }

    // Pause Windows Update for as long as you want (Windows itself normally stops at 5 weeks).
    class PauseUpdatesTweak : RegTweak
    {
        const string UxPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

        public static int Days
        {
            get
            {
                int d;
                return int.TryParse(Store.Get("pauseDays", "365"), out d) && d > 0 ? d : 365;
            }
            set { Store.Set("pauseDays", value.ToString(CultureInfo.InvariantCulture)); }
        }

        protected override List<RegSet> SetsToApply()
        {
            string start = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string end = DateTime.UtcNow.AddDays(Days).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            List<RegSet> list = new List<RegSet>();
            list.Add(RegSet.Dword(RegistryHive.LocalMachine, UxPath, "FlightSettingsMaxPauseDays", 3650));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseFeatureUpdatesStartTime", start));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseFeatureUpdatesEndTime", end));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseQualityUpdatesStartTime", start));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseQualityUpdatesEndTime", end));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseUpdatesStartTime", start));
            list.Add(RegSet.Text(RegistryHive.LocalMachine, UxPath, "PauseUpdatesExpiryTime", end));
            return list;
        }

        public static DateTime? PausedUntil()
        {
            string s = Reg.Read(RegistryHive.LocalMachine, UxPath, "PauseUpdatesExpiryTime") as string;
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) && t > DateTime.UtcNow)
                return t.ToLocalTime();
            return null;
        }

        public override bool IsOn() { return PausedUntil() != null; }

        public override void Apply()
        {
            bool wasPaused = IsOn();
            base.Apply();
            if (!wasPaused || Store.Get("pausedSince").Length == 0)
                Store.Set("pausedSince", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        public override void Revert()
        {
            base.Revert();
            // Make sure no pause is left behind, even one set before QuietPC.
            foreach (string name in new[] { "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime",
                                            "PauseQualityUpdatesEndTime", "PauseUpdatesStartTime", "PauseUpdatesExpiryTime" })
                Reg.Delete(RegistryHive.LocalMachine, UxPath, name);
            Store.Set("pausedSince", null);
        }
    }

    // Lighter virus scans: Windows Security scans at a lower priority and mostly when you're away.
    class GentleScansTweak : Tweak
    {
        string Key { get { return Id + "|defender"; } }

        public override bool IsOn() { return Backup.Has(Key); }

        public override void Apply()
        {
            Shell.Result r = Shell.PowerShell(
                "$p = Get-MpPreference -ErrorAction Stop; '{0}|{1}|{2}' -f $p.ScanAvgCPULoadFactor, [int]$p.EnableLowCpuPriority, [int]$p.ScanOnlyIfIdleEnabled");
            string line = r.Output.Trim();
            if (!r.Ok || line.Split('|').Length != 3)
                throw new Exception("Windows Security isn't the antivirus on this PC, so there's nothing to change here.");
            Backup.SaveOnce(Key, line);
            Shell.Result set = Shell.PowerShell("Set-MpPreference -ScanAvgCPULoadFactor 20 -EnableLowCpuPriority $true -ScanOnlyIfIdleEnabled $true -ErrorAction Stop");
            if (!set.Ok) { Backup.Remove(Key); throw new Exception("Windows Security didn't allow the change."); }
        }

        public override void Revert()
        {
            string original;
            if (!Backup.TryGet(Key, out original)) return;
            string[] p = original.Split('|');
            if (p.Length == 3)
            {
                int load;
                if (!int.TryParse(p[0], out load) || load < 5 || load > 100) load = 50;
                Shell.Result r = Shell.PowerShell("Set-MpPreference -ScanAvgCPULoadFactor " + load +
                                 " -EnableLowCpuPriority $" + (p[1] == "1" ? "true" : "false") +
                                 " -ScanOnlyIfIdleEnabled $" + (p[2] == "1" ? "true" : "false") + " -ErrorAction Stop");
                if (!r.Ok) throw new Exception("Windows Security didn't allow putting the scan settings back.");
            }
            Backup.Remove(Key);
        }
    }

    // The full right-click menu, without "Show more options".
    class ClassicMenuTweak : Tweak
    {
        const string ClsidPath = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

        string Key { get { return Id + "|classic"; } }

        public override bool IsOn()
        {
            return Reg.KeyExists(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32") &&
                   (Reg.Read(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32", "") as string) == "";
        }

        public override void Apply()
        {
            Backup.SaveOnce(Key, !Reg.KeyExists(RegistryHive.CurrentUser, ClsidPath) ? "absent"
                               : Reg.KeyExists(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32") ? "existed" : "noinproc");
            Reg.Write(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32", "", "", RegistryValueKind.String);
        }

        public override void Revert()
        {
            string original;
            if (!Backup.TryGet(Key, out original)) original = "absent";
            if (original == "absent") Reg.DeleteTree(RegistryHive.CurrentUser, ClsidPath);
            else if (original == "noinproc") Reg.DeleteTree(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32");
            else Reg.Delete(RegistryHive.CurrentUser, ClsidPath + @"\InprocServer32", "");
            Backup.Remove(Key);
        }
    }

    // Hibernate (instead of sleep) when the lid closes on battery, so the laptop doesn't drain or heat up in a bag.
    class LidHibernateTweak : PowerTweak
    {
        string HibKey { get { return Id + "|hibernate"; } }

        public override void Apply()
        {
            object enabled = Reg.Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
            Backup.SaveOnce(HibKey, enabled is int && (int)enabled == 0 ? "off" : "on");
            Shell.Run(Paths.System32("powercfg.exe"), "/hibernate on", 30000);
            base.Apply();
        }

        public override void Revert()
        {
            base.Revert();
            string was;
            if (Backup.TryGet(HibKey, out was))
            {
                if (was != "off" || Shell.Run(Paths.System32("powercfg.exe"), "/hibernate off", 30000).Ok) Backup.Remove(HibKey);
            }
        }
    }

    static class Catalog
    {
        public static readonly List<Tweak> All = new List<Tweak>();
        static readonly RegistryHive HKLM = RegistryHive.LocalMachine;
        static readonly RegistryHive HKCU = RegistryHive.CurrentUser;

        public static PauseUpdatesTweak PauseUpdates;
        public static Tweak QuietBattery;

        static T Add<T>(T t, string id, string section, bool recommended, string title, string description, string warning = "") where T : Tweak
        {
            t.Id = id; t.Section = section; t.Recommended = recommended;
            t.Title = title; t.Description = description; t.Warning = warning;
            MultiTweak multi = t as MultiTweak;
            if (multi != null) multi.NameParts();
            All.Add(t);
            return t;
        }

        static Catalog()
        {
            const string WU = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
            const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

            // ---------- updates
            PauseUpdates = Add(new PauseUpdatesTweak(), "pause-updates", Sections.Updates, true,
                "Pause updates until I say so",
                "Windows won't download or install updates by itself. Use \"Update now\" whenever it suits you.",
                "Security fixes also wait. QuietPC reminds you after a month.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, WU + @"\AU", "NoAutoRebootWithLoggedOnUsers", 1))
                    .Add(RegSet.Dword(HKLM, WU + @"\AU", "AlwaysAutoRebootAtScheduledTime", 0)),
                "no-auto-restart", Sections.Updates, true,
                "No surprise restarts",
                "Windows won't restart on its own while you're signed in. It asks instead.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, WU, "ExcludeWUDriversInQualityUpdate", 1))
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0)),
                "no-driver-updates", Sections.Updates, true,
                "Don't replace my drivers",
                "Stops Windows Update swapping sound, Wi-Fi, touchpad and graphics drivers, a common cause of new glitches.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0)),
                "no-update-sharing", Sections.Updates, true,
                "Don't share updates with other PCs",
                "Windows normally uploads update files to strangers' PCs. This saves battery and internet.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, WU + @"\AU", "NoAutoUpdate", 0))
                    .Add(RegSet.Dword(HKLM, WU + @"\AU", "AUOptions", 2)),
                "ask-before-download", Sections.Updates, false,
                "Ask before downloading updates",
                "Windows tells you updates are available instead of downloading them. Works fully on Windows Pro; Home may ignore it.");

            // ---------- fan & battery
            QuietBattery = Add(new PowerTweak()
                    .Add("SUB_PROCESSOR", "PROCTHROTTLEMAX", false, 99)
                    .Add("SUB_PROCESSOR", "PERFBOOSTMODE", false, 0),
                "quiet-battery", Sections.Fan, true,
                "Quiet mode on battery",
                "Turns off the processor's turbo boost on battery: much less heat and fan noise, longer battery. Everyday apps feel the same.");

            Add(new PowerTweak()
                    .Add("SUB_PROCESSOR", "PROCTHROTTLEMAX", true, 99)
                    .Add("SUB_PROCESSOR", "PERFBOOSTMODE", true, 0),
                "quiet-plugged", Sections.Fan, false,
                "Quiet mode when plugged in too",
                "Same when charging. Turn it off for gaming or video editing.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "PreventIndexOnBattery", 1)),
                "index-battery", Sections.Fan, true,
                "Pause file indexing on battery",
                "Windows Search catches up when you're plugged in instead of draining the battery.");

            Add(new GentleScansTweak(), "gentle-scans", Sections.Fan, true,
                "Gentler virus scans",
                "Windows Security still protects you in real time, but scheduled scans use less power and run when you're away.");

            Add(new MultiTweak()
                    .Add("timers", new PowerTweak().Add("SUB_SLEEP", "RTCWAKE", true, 0).Add("SUB_SLEEP", "RTCWAKE", false, 0))
                    .Add("maintenance", new RegTweak().Add(RegSet.Dword(HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "WakeUp", 0))),
                "no-night-wake", Sections.Fan, true,
                "Don't wake up at night",
                "Stops Windows waking the laptop from sleep to run maintenance or updates.");

            Add(new LidHibernateTweak().Add("SUB_BUTTONS", "LIDACTION", false, 2),
                "lid-hibernate", Sections.Fan, false,
                "Hibernate when I close the lid (on battery)",
                "The laptop stays cool and keeps its charge in a bag. Opening it takes a few seconds longer.",
                "Uses some disk space for the hibernation file.");

            // ---------- clean up
            Add(new RegTweak()
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-338388Enabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-338389Enabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-338387Enabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-353694Enabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-353696Enabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SystemPaneSuggestionsEnabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SilentInstalledAppsEnabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SoftLandingEnabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "RotatingLockScreenOverlayEnabled", 0))
                    .Add(RegSet.Dword(HKCU, ADV, "Start_IrisRecommendations", 0))
                    .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0))
                    .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)),
                "no-ads", Sections.Cleanup, true,
                "No ads or \"suggestions\"",
                "Removes promoted apps and tips from Start, the lock screen and Settings, and stops apps silently installing themselves.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1))
                    .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)),
                "no-web-search", Sections.Cleanup, true,
                "No web results in Start search",
                "Start search only looks at your PC, so it's faster and doesn't send what you type to Bing.",
                "Takes effect after you sign out or restart.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0))
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0)),
                "no-widgets", Sections.Cleanup, true,
                "Turn off Widgets",
                "Removes the news and weather panel that runs in the background.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKCU, ADV, "ShowCopilotButton", 0))
                    .Add(RegSet.Dword(HKCU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)),
                "no-copilot", Sections.Cleanup, false,
                "Hide the Copilot button",
                "Removes Copilot from the taskbar. You can still open the Copilot app if you install it.");

            Add(new MultiTweak()
                    .Add("policies", new RegTweak()
                        .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0))
                        .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1))
                        .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)))
                    .Add("tasks", new TaskTweak()
                        .Add(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser")
                        .Add(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater")
                        .Add(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator")
                        .Add(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip")
                        .Add(@"\Microsoft\Windows\Feedback\Siuf\DmClient")),
                "less-tracking", Sections.Cleanup, true,
                "Less background tracking",
                "Sends Microsoft the minimum data and stops the \"Compatibility Telemetry\" checks that often spin the fan.");

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0))
                    .Add(RegSet.Dword(HKCU, CDM, "SubscribedContent-310093Enabled", 0))
                    .Add(RegSet.Dword(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0)),
                "no-nags", Sections.Cleanup, true,
                "No \"finish setting up\" nags",
                "Stops the full-screen \"Let's finish setting up your device\" screens and suggestion pop-ups.");

            Add(new ClassicMenuTweak(), "classic-menu", Sections.Cleanup, false,
                "Full right-click menu",
                "Right-click shows every option straight away, without \"Show more options\".");
            Find("classic-menu").NeedsExplorerRestart = true;

            Add(new RegTweak()
                    .Add(RegSet.Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2)),
                "no-background-apps", Sections.Cleanup, false,
                "Stop Store apps running in the background",
                "Microsoft Store apps only run while open. Saves battery.",
                "Some Store apps (like Mail or WhatsApp) only notify you while they're open.");
        }

        public static Tweak Find(string id)
        {
            foreach (Tweak t in All) if (t.Id == id) return t;
            return null;
        }

        public static List<Tweak> InSection(string section)
        {
            List<Tweak> list = new List<Tweak>();
            foreach (Tweak t in All) if (t.Section == section) list.Add(t);
            return list;
        }
    }

    // Runs changes one at a time (never two at once) and handles "Calm my PC" and "Undo everything".
    static class Engine
    {
        static readonly object opLock = new object();

        public static void Apply(Tweak t)
        {
            lock (opLock)
            {
                Log.Write("apply " + t.Id);
                t.Apply();
            }
        }

        public static void Revert(Tweak t)
        {
            lock (opLock)
            {
                Log.Write("revert " + t.Id);
                if (t.Recommended) IsCalm = false;
                t.Revert();
            }
        }

        // Makes a Windows restore point the first time, as an extra safety net.
        public static void RestorePointOnce()
        {
            string state = Store.Get("restorePoint");
            if (state == "made") return;
            DateTime last;
            if (DateTime.TryParse(state, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) &&
                (DateTime.UtcNow - last.ToUniversalTime()).TotalHours < 24) return;
            Shell.Result r = Shell.PowerShell(
                "Checkpoint-Computer -Description 'Before QuietPC changes' -RestorePointType MODIFY_SETTINGS -ErrorAction SilentlyContinue; " +
                "if (Get-ComputerRestorePoint | Where-Object { $_.Description -eq 'Before QuietPC changes' }) { 'MADE' }", 180000);
            Store.Set("restorePoint", r.Output.Contains("MADE") ? "made" : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        // One click: switch on everything recommended. Returns what couldn't be done.
        public static List<string> CalmNow(Action<string> progress)
        {
            List<string> problems = new List<string>();
            if (progress != null) progress("Making a safety restore point...");
            RestorePointOnce();
            foreach (Tweak t in Catalog.All)
            {
                if (!t.Recommended) continue;
                try
                {
                    if (t.IsOn()) continue;
                    if (progress != null) progress(t.Title + "...");
                    Apply(t);
                }
                catch (Exception ex)
                {
                    problems.Add(t.Title + ": " + ex.Message);
                    Log.Write("calm " + t.Id + ": " + ex);
                }
            }
            IsCalm = true;
            return problems;
        }

        // Remembered after "Calm my PC", forgotten when something recommended is switched off or undone.
        public static bool IsCalm
        {
            get { return Store.Get("calm") == "1"; }
            set { Store.Set("calm", value ? "1" : null); }
        }

        // Puts every setting QuietPC changed back the way it was.
        // Returns the things that couldn't be put back (their originals are kept for another try).
        public static List<string> UndoEverything(Action<string> progress)
        {
            List<string> failed = new List<string>();
            foreach (Tweak t in Catalog.All)
            {
                if (!t.HasBackup()) continue;
                if (progress != null) progress("Undoing: " + t.Title + "...");
                try { Revert(t); }
                catch (Exception ex) { Log.Write("undo " + t.Id + ": " + ex); }
                if (t.HasBackup()) failed.Add(t.Title);
            }
            IsCalm = false;
            if (progress != null) progress("Putting startup apps back...");
            try { StartupApps.UndoAll(); } catch (Exception ex) { Log.Write("undo startup: " + ex); }
            if (Backup.HasPrefix("startup|")) failed.Add("Startup apps");
            return failed;
        }
    }
}
