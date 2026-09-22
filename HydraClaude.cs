// HydraClaude - a profile manager for the Claude Desktop app on Windows.
//
// Claude Desktop ships as an MSIX package. Two consequences drive the whole design:
//
//   1. The exe under C:\Program Files\WindowsApps cannot be started directly (always
//      "Access is denied"), so the app has to be *activated* through its package identity
//      (AUMID). Activation is also the only way to pass --user-data-dir, which is what
//      actually separates one profile from another.
//   2. The package auto-updates, and every update changes the install path and the package
//      full name. Anything that pins a path rots. So nothing here lives inside the package:
//      profile data, this exe and its config all sit under %LOCALAPPDATA%\ClaudeProfiles,
//      and the AUMID is re-resolved from the package family at every launch rather than
//      baked in.
//
// Run with no arguments for the manager window; run as `HydraClaude.exe <ProfileName>` to
// launch one profile straight away (that is what the desktop shortcuts do).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace HydraClaude
{
    // ---------------------------------------------------------------- config

    [DataContract]
    public class ProfileEntry
    {
        [DataMember(Name = "name", Order = 0)] public string Name;
        // Empty Dir means "launch with no --user-data-dir", i.e. the stock Claude instance.
        [DataMember(Name = "dir", Order = 1)] public string Dir;
        [DataMember(Name = "extraArgs", Order = 2)] public string ExtraArgs;
        [DataMember(Name = "lastLaunched", Order = 3)] public string LastLaunched;

        public bool IsStock { get { return string.IsNullOrEmpty(Dir); } }
    }

    [DataContract]
    public class HydraConfig
    {
        [DataMember(Name = "schema", Order = 0)] public int Schema;
        // Overridable so a future package rename can be fixed from the UI, not a rebuild.
        [DataMember(Name = "packageFamilyName", Order = 1)] public string PackageFamilyName;
        [DataMember(Name = "aumidOverride", Order = 2)] public string AumidOverride;
        // The "I'm still in the notification area" balloon is shown once, then never again.
        [DataMember(Name = "trayHintShown", Order = 3)] public bool TrayHintShown;
        [DataMember(Name = "profiles", Order = 4)] public List<ProfileEntry> Profiles;
    }

    public class Store
    {
        public HydraConfig Config;

        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeProfiles");
            }
        }

        public static string ConfigPath { get { return Path.Combine(Root, "hydraclaude.json"); } }

        // Where the stock (no --user-data-dir) instance keeps its data. Confirmed from the
        // command line of a running Claude: Electron resolves it to %APPDATA%\Claude, which
        // MSIX mirrors into ...\Packages\<family>\LocalCache\Roaming\Claude.
        public static string StockDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            }
        }

        public static string DefaultDirFor(string name) { return Path.Combine(Root, name); }

        public static Store Load()
        {
            var store = new Store();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    using (var fs = File.OpenRead(ConfigPath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(HydraConfig));
                        store.Config = (HydraConfig)ser.ReadObject(fs);
                    }
                }
            }
            catch
            {
                // A corrupt config must never block a launch: keep the bad file for inspection
                // and carry on with a freshly seeded one.
                try { File.Copy(ConfigPath, ConfigPath + ".bad", true); }
                catch { }
                store.Config = null;
            }

            if (store.Config == null) store.Config = Seed();
            if (store.Config.Profiles == null) store.Config.Profiles = new List<ProfileEntry>();
            if (string.IsNullOrEmpty(store.Config.PackageFamilyName))
                store.Config.PackageFamilyName = Packaging.DefaultFamily;
            store.Config.Schema = 1;
            return store;
        }

        // First run: adopt whatever profile folders already exist, plus the stock instance.
        static HydraConfig Seed()
        {
            var cfg = new HydraConfig
            {
                Schema = 1,
                PackageFamilyName = Packaging.DefaultFamily,
                AumidOverride = "",
                Profiles = new List<ProfileEntry>()
            };

            cfg.Profiles.Add(new ProfileEntry { Name = "Default", Dir = "", ExtraArgs = "", LastLaunched = "" });

            try
            {
                foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d))
                {
                    var leaf = Path.GetFileName(dir);
                    if (leaf.StartsWith(".")) continue;
                    cfg.Profiles.Add(new ProfileEntry { Name = leaf, Dir = dir, ExtraArgs = "", LastLaunched = "" });
                }
            }
            catch { }

            return cfg;
        }

        public ProfileEntry Find(string name)
        {
            return Config.Profiles.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public void Save()
        {
            Directory.CreateDirectory(Root);
            var tmp = ConfigPath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
            {
                var ser = new DataContractJsonSerializer(typeof(HydraConfig));
                ser.WriteObject(w, Config);
                w.Flush();
            }
            // Replace rather than overwrite, so an interrupted save cannot truncate the config.
            if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
            else File.Move(tmp, ConfigPath);
        }
    }

    // ------------------------------------------------------------- packaging

    public class PackageInfo
    {
        public string Aumid;
        public string FullName;
        public string Version;
        public string Family;
        public string Source;   // "package" | "override" | "fallback"
        public string Error;

        public bool Resolved { get { return Source == "package" || Source == "override"; } }
    }

    // Resolves the AUMID from the installed package instead of hard-coding it, so an
    // auto-update (new version, new package full name) cannot break the launcher.
    public static class Packaging
    {
        public const string DefaultFamily = "Claude_pzs8sxrjxfjjc";
        public const string FallbackAumid = "Claude_pzs8sxrjxfjjc!Claude";
        const string PreferredAppId = "Claude";     // the package also ships SshAskpass / SshProxy
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const uint PACKAGE_FILTER_HEAD_DIRECT = 0x30;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int FindPackagesByPackageFamily(string packageFamilyName, uint packageFilters,
            ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer,
            IntPtr packageProperties);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int OpenPackageInfoByFullName(string packageFullName, uint reserved,
            out IntPtr packageInfoReference);

        [DllImport("kernel32.dll")]
        static extern int GetPackageApplicationIds(IntPtr packageInfoReference, ref uint bufferLength,
            IntPtr buffer, out uint count);

        [DllImport("kernel32.dll")]
        static extern int ClosePackageInfo(IntPtr packageInfoReference);

        public static string[] FindFullNames(string family)
        {
            uint count = 0, bufLen = 0;
            int rc = FindPackagesByPackageFamily(family, PACKAGE_FILTER_HEAD_DIRECT,
                ref count, IntPtr.Zero, ref bufLen, IntPtr.Zero, IntPtr.Zero);
            if (rc != ERROR_INSUFFICIENT_BUFFER || count == 0) return new string[0];

            IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            IntPtr buffer = Marshal.AllocHGlobal((int)bufLen * 2);
            IntPtr props = Marshal.AllocHGlobal((int)count * 4);
            try
            {
                rc = FindPackagesByPackageFamily(family, PACKAGE_FILTER_HEAD_DIRECT,
                    ref count, names, ref bufLen, buffer, props);
                if (rc != 0) return new string[0];

                var result = new string[count];
                for (int i = 0; i < count; i++)
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(names);
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(props);
            }
        }

        public static string[] GetAppIds(string packageFullName)
        {
            IntPtr info;
            if (OpenPackageInfoByFullName(packageFullName, 0, out info) != 0) return new string[0];
            try
            {
                uint len = 0, count = 0;
                int rc = GetPackageApplicationIds(info, ref len, IntPtr.Zero, out count);
                if (rc != ERROR_INSUFFICIENT_BUFFER || count == 0) return new string[0];

                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    rc = GetPackageApplicationIds(info, ref len, buf, out count);
                    if (rc != 0) return new string[0];

                    var ids = new string[count];
                    for (int i = 0; i < count; i++)
                        ids[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size));
                    return ids;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { ClosePackageInfo(info); }
        }

        // Package full names look like Claude_2.2553.1.0_x64__pzs8sxrjxfjjc.
        static Version VersionOf(string fullName)
        {
            try
            {
                var parts = fullName.Split('_');
                Version v;
                if (parts.Length > 1 && Version.TryParse(parts[1], out v)) return v;
            }
            catch { }
            return new Version(0, 0);
        }

        static string Qualify(string family, string id)
        {
            return id != null && id.Contains("!") ? id : family + "!" + id;
        }

        static string AppIdOf(string aumid)
        {
            int bang = aumid.IndexOf('!');
            return bang < 0 ? aumid : aumid.Substring(bang + 1);
        }

        public static PackageInfo Resolve(HydraConfig cfg)
        {
            string family = string.IsNullOrEmpty(cfg.PackageFamilyName)
                ? DefaultFamily : cfg.PackageFamilyName.Trim();

            var info = new PackageInfo { Family = family };

            string[] fullNames;
            try { fullNames = FindFullNames(family); }
            catch (Exception ex) { fullNames = new string[0]; info.Error = ex.Message; }

            if (fullNames.Length > 0)
            {
                var best = fullNames.OrderByDescending(VersionOf).First();
                info.FullName = best;
                info.Version = VersionOf(best).ToString();

                // GetPackageApplicationIds already hands back complete AUMIDs
                // ("Claude_pzs8sxrjxfjjc!Claude"), so these must not be prefixed again.
                var ids = GetAppIds(best).Select(i => Qualify(family, i)).ToArray();
                string aumid = ids.FirstOrDefault(
                    i => string.Equals(AppIdOf(i), PreferredAppId, StringComparison.OrdinalIgnoreCase));
                if (aumid == null && ids.Length > 0) aumid = ids[0];
                if (aumid != null)
                {
                    info.Aumid = aumid;
                    info.Source = "package";
                }
                else info.Error = "Package found but it declares no applications.";
            }
            else if (info.Error == null)
            {
                info.Error = "No installed package matches the family name \"" + family + "\".";
            }

            // An explicit override always wins - it is the escape hatch when detection is wrong.
            if (!string.IsNullOrEmpty(cfg.AumidOverride) && cfg.AumidOverride.Trim().Length > 0)
            {
                info.Aumid = cfg.AumidOverride.Trim();
                info.Source = "override";
            }
            else if (info.Aumid == null)
            {
                info.Aumid = FallbackAumid;
                info.Source = "fallback";
            }

            return info;
        }
    }

    // ------------------------------------------------------------- launching

    public static class Launcher
    {
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            int ActivateApplication(string appUserModelId, string arguments, int options, out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        class ApplicationActivationManager { }

        public static string BuildArgs(ProfileEntry p)
        {
            var sb = new StringBuilder();
            if (!p.IsStock) sb.Append("--user-data-dir=\"").Append(p.Dir.TrimEnd('\\')).Append("\"");
            if (!string.IsNullOrEmpty(p.ExtraArgs))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(p.ExtraArgs.Trim());
            }
            return sb.ToString();
        }

        // Returns null on success, or a human-readable error.
        public static string Launch(ProfileEntry p, PackageInfo pkg)
        {
            try
            {
                if (!p.IsStock) Directory.CreateDirectory(p.Dir);

                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                uint pid;
                int hr = mgr.ActivateApplication(pkg.Aumid, BuildArgs(p), 0, out pid);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                p.LastLaunched = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                return null;
            }
            catch (Exception ex)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Could not start the \"" + p.Name + "\" profile.");
                sb.AppendLine();
                sb.AppendLine("AUMID:   " + pkg.Aumid + "  (" + pkg.Source + ")");
                if (!string.IsNullOrEmpty(pkg.FullName)) sb.AppendLine("Package: " + pkg.FullName);
                sb.AppendLine("Args:    " + BuildArgs(p));
                sb.AppendLine();
                sb.AppendLine(ex.Message);
                if (pkg.Source == "fallback")
                {
                    sb.AppendLine();
                    sb.AppendLine("The package could not be detected, so a built-in AUMID was used. Run");
                    sb.AppendLine("    Get-StartApps | ? Name -like \"*laude*\"");
                    sb.AppendLine("in PowerShell and paste the AppID into Settings.");
                }
                return sb.ToString();
            }
        }
    }

    // ------------------------------------------------- which profiles are up

    // Which profiles currently have a window open.
    //
    // The main process is no help: when Claude is started from the Start menu its command line
    // carries no --user-data-dir at all. Every *child* process (renderer, gpu, utility) is
    // spawned with the path Electron resolved, including the stock instance, which shows up as
    // %APPDATA%\Claude. So only command lines that actually carry the flag are counted.
    public static class RunningScan
    {
        // The WMI query has to reach into every process's PEB to read a command line: measured
        // at ~40ms against ~2.7ms for a plain process count. The count only moves when a window
        // opens or closes, so it gates the expensive query and an idle tick stays ~15x cheaper.
        public static int ProcessCount()
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("claude");
            try { return procs.Length; }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        public static HashSet<string> Scan()
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        var cmd = mo["CommandLine"] as string;
                        if (cmd == null) continue;
                        var dir = ExtractDataDir(cmd);
                        if (dir == null) continue;
                        dirs.Add(Normalize(dir));
                    }
                }
            }
            catch { }
            return dirs;
        }

        // Returns null when the command line says nothing about the data directory.
        static string ExtractDataDir(string commandLine)
        {
            const string flag = "--user-data-dir=";
            int i = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;

            int start = i + flag.Length;
            if (start >= commandLine.Length) return null;
            if (commandLine[start] == '"')
            {
                int end = commandLine.IndexOf('"', start + 1);
                return end < 0 ? commandLine.Substring(start + 1)
                               : commandLine.Substring(start + 1, end - start - 1);
            }
            int space = commandLine.IndexOf(' ', start);
            return space < 0 ? commandLine.Substring(start)
                             : commandLine.Substring(start, space - start);
        }

        public static string Normalize(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            try { return Path.GetFullPath(dir).TrimEnd('\\'); }
            catch { return dir.TrimEnd('\\'); }
        }
    }

    // Holds the last known answer and only pays for a full scan when the process count moved.
    public class RunningTracker
    {
        int _count = -1;
        public HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Returns true when Dirs changed. Safe to call from a background thread.
        public bool Refresh(bool force)
        {
            int now;
            try { now = RunningScan.ProcessCount(); }
            catch { return false; }

            if (!force && now == _count) return false;
            _count = now;

            var found = now == 0
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : RunningScan.Scan();
            if (found.SetEquals(Dirs)) return false;

            Dirs = found;
            return true;
        }

        public bool IsRunning(ProfileEntry p)
        {
            return Dirs.Contains(RunningScan.Normalize(p.IsStock ? Store.StockDir : p.Dir));
        }
    }

    // Icons sit next to the exe so the build can drop in new artwork without a recompile.
    public static class Assets
    {
        public static string Dir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string App { get { return Path.Combine(Dir, "hydra.ico"); } }

        public static string Tray
        {
            get
            {
                // A monochrome tray glyph has to invert with the taskbar, or it disappears.
                return Path.Combine(Dir, Theme.IsSystemDark() ? "hydra-tray-dark.ico"
                                                              : "hydra-tray-light.ico");
            }
        }

        public static Icon Load(string path, int size)
        {
            try
            {
                if (File.Exists(path)) return new Icon(path, size, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }
    }

    // ------------------------------------------------------------- shortcuts

    public static class Shortcuts
    {
        // Late-bound WScript.Shell keeps this free of an interop assembly, so the whole app
        // stays a single-file csc build.
        public static string Create(ProfileEntry p, string exePath, string iconPath)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var safe = p.Name;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '-');
            var lnk = Path.Combine(desktop, "Claude - " + safe + ".lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell is not available.");
            object shell = Activator.CreateInstance(shellType);

            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { lnk });
            var linkType = link.GetType();

            SetProp(linkType, link, "TargetPath", exePath);
            SetProp(linkType, link, "Arguments", "\"" + p.Name + "\"");
            SetProp(linkType, link, "WorkingDirectory", Path.GetDirectoryName(exePath));
            SetProp(linkType, link, "Description", "Claude Desktop - " + p.Name + " profile (HydraClaude)");
            SetProp(linkType, link, "IconLocation", File.Exists(iconPath) ? iconPath + ",0" : exePath + ",0");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

            return lnk;
        }

        static void SetProp(Type t, object target, string name, object value)
        {
            t.InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });
        }
    }
}
