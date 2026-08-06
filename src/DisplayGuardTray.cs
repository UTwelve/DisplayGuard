// DisplayGuardTray - resident tray watchdog (v2.1).
// Moves windows off watched "phantom" displays back to a destination display.
// Tray mode: (no args) | --quiet -> NotifyIcon + ContextMenuStrip watchdog.
// CLI mode : --list | --dry | --once | --quit (console output, then exit).
// P/Invoke only (user32.dll / dwmapi.dll), .NET Framework 4.x, C# 5 syntax.
// Build:
//   csc /target:exe /codepage:65001 /out:DisplayGuardTray.exe
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll DisplayGuardTray.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class DisplayGuardTray
{
    internal const string Version = "2.1.1";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "DisplayGuard";
    internal const string ConfigFileName = "config.ini";

    // ---------------- Win32 interop ----------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public long Area  { get { return (long)Math.Max(0, Width) * Math.Max(0, Height); } }
        public override string ToString()
        {
            return "(" + Left + "," + Top + ")-(" + Right + "," + Bottom + ") " + Width + "x" + Height;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_RESTORE = 9;
    private const int DWMWA_CLOAKED = 14;

    // ---------------- Model ----------------
    internal sealed class Mon
    {
        public IntPtr Handle;
        public string DeviceName;   // e.g. \\.\DISPLAY5
        public string Description;  // monitor description from EnumDisplayDevices
        public string DeviceId;     // hardware id path from EnumDisplayDevices
        public string Friendly;     // EDID friendly name from registry (best effort)
        public RECT Bounds;
        public RECT Work;
        public bool Primary;
        public bool IsTarget;       // watched screen under current config
        public bool IsDest;         // destination screen under current config
    }

    // ---------------- File log (logs\DisplayGuard-YYYYMMDD.log) ----------------
    internal static class Log
    {
        private static string _dir;
        private static readonly object Sync = new object();

        public static string Dir { get { return _dir; } }

        public static void Init(string exeDir)
        {
            try
            {
                _dir = Path.Combine(exeDir, "logs");
                Directory.CreateDirectory(_dir);
            }
            catch { _dir = null; /* logging disabled: watchdog keeps working */ }
        }

        public static void Write(string text)
        {
            try
            {
                if (_dir == null) return;
                string file = Path.Combine(_dir, "DisplayGuard-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text;
                lock (Sync)
                {
                    File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { /* logging must never break the watchdog */ }
        }
    }

    // ---------------- Config ----------------
    internal sealed class Config
    {
        public bool Enabled = true;
        public string WatchMode = "Auto";          // "Auto" | "Manual"
        public string WatchDevices = "";           // ';' separated, e.g. \\.\DISPLAY5;\\.\DISPLAY6
        public string DestDevice = "Primary";      // "Primary" | \\.\DISPLAY4
        public int IntervalMs = 5000;
        public string PhantomKeyword = "EP-HDMI-RX";
        public string Dir = ".";
        public bool Migrated;                      // old-format keys seen: rewrite on save

        public string PathOnDisk { get { return Path.Combine(Dir, ConfigFileName); } }

        public List<string> WatchList()
        {
            var list = new List<string>();
            if (WatchDevices == null) return list;
            foreach (string part in WatchDevices.Split(';'))
            {
                string d = part.Trim();
                if (d.Length > 0 && !list.Contains(d)) list.Add(d);
            }
            return list;
        }

        public void SetWatchList(List<string> devices)
        {
            WatchDevices = string.Join(";", devices.ToArray());
        }

        public bool IsPrimaryDest()
        {
            return string.IsNullOrEmpty(DestDevice) ||
                   DestDevice.Equals("Primary", StringComparison.OrdinalIgnoreCase);
        }

        public static Config Load(string exeDir)
        {
            var c = new Config();
            c.Dir = exeDir;
            bool hasNew = false;
            try
            {
                if (!File.Exists(c.PathOnDisk)) return c;
                foreach (string raw in File.ReadAllLines(c.PathOnDisk))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                        c.Enabled = !(val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase));
                    else if (key.Equals("WatchMode", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                    {
                        c.WatchMode = val.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Auto";
                        hasNew = true;
                    }
                    else if (key.Equals("WatchDevices", StringComparison.OrdinalIgnoreCase))
                    {
                        c.WatchDevices = val;
                        hasNew = true;
                    }
                    else if (key.Equals("DestDevice", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                    {
                        c.DestDevice = val;
                        hasNew = true;
                    }
                    else if (key.Equals("IntervalMs", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (int.TryParse(val, out v) && v >= 100) c.IntervalMs = v;
                    }
                    else if (key.Equals("PhantomKeyword", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                        c.PhantomKeyword = val;
                    else if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
                    {
                        // v2.0 legacy key
                        c.Migrated = true;
                        if (!hasNew)
                            c.WatchMode = val.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Auto";
                    }
                    else if (key.Equals("ManualDevice", StringComparison.OrdinalIgnoreCase))
                    {
                        // v2.0 legacy key
                        c.Migrated = true;
                        if (!hasNew && c.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                            c.WatchDevices = val;
                    }
                }
            }
            catch { /* fall back to defaults */ }
            return c;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("Enabled=" + (Enabled ? "1" : "0"));
                lines.Add("WatchMode=" + WatchMode);
                lines.Add("WatchDevices=" + (WatchDevices ?? ""));
                lines.Add("DestDevice=" + (string.IsNullOrEmpty(DestDevice) ? "Primary" : DestDevice));
                lines.Add("IntervalMs=" + IntervalMs.ToString());
                lines.Add("PhantomKeyword=" + (PhantomKeyword ?? ""));
                File.WriteAllLines(PathOnDisk, lines.ToArray(), new UTF8Encoding(false));
                Migrated = false;
            }
            catch { /* config persistence is best effort */ }
        }
    }

    private static Config _cfg;

    // ---------------- Entry ----------------
    [STAThread]
    private static int Main(string[] args)
    {
        string exePath = Assembly.GetExecutingAssembly().Location;
        string exeDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDir)) exeDir = ".";

        string mode = "";
        bool quiet = false;
        foreach (string a in args)
        {
            string t = (a ?? "").Trim().ToLowerInvariant();
            if (t == "--quiet") { quiet = true; continue; }
            if (mode.Length == 0) mode = t;
        }

        if (mode == "--quit")
            return CliQuit(exePath);

        _cfg = Config.Load(exeDir);
        if (_cfg.Migrated) _cfg.Save();      // auto-rewrite legacy config to v2.1 format
        Log.Init(exeDir);

        if (mode.Length > 0 && mode != "--quiet")
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* keep default */ }
            switch (mode)
            {
                case "--list":
                    Log.Write("START mode=CLI(--list)");
                    int rcList = CliList();
                    Log.Write("EXIT mode=CLI(--list) code=" + rcList);
                    return rcList;
                case "--dry":
                    Log.Write("START mode=CLI(--dry) " + ConfigSummary());
                    int rcDry = CliScan(false);
                    Log.Write("EXIT mode=CLI(--dry) code=" + rcDry);
                    return rcDry;
                case "--once":
                    Log.Write("START mode=CLI(--once) " + ConfigSummary());
                    int rcOnce = CliScan(true);
                    Log.Write("EXIT mode=CLI(--once) code=" + rcOnce);
                    return rcOnce;
                default:
                    Console.WriteLine("Usage: DisplayGuardTray.exe [--list | --dry | --once | --quit] [--quiet]");
                    Console.WriteLine("  (no args) | --quiet  tray watchdog mode (--quiet is also written by autostart)");
                    return 1;
            }
        }

        // Tray mode: detach the console immediately so no console window lingers.
        try { FreeConsole(); } catch { /* no console: fine */ }
        Log.Write("START mode=Tray quiet=" + quiet + " " + ConfigSummary());
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(_cfg, exePath));
        Log.Write("EXIT mode=Tray");
        return 0;
    }

    private static string ConfigSummary()
    {
        string watch = _cfg.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? "Manual[" + (_cfg.WatchDevices.Length > 0 ? _cfg.WatchDevices : "<empty>") + "]"
            : "Auto(keyword=\"" + _cfg.PhantomKeyword + "\")";
        return "interval=" + _cfg.IntervalMs + "ms enabled=" + (_cfg.Enabled ? "1" : "0") +
               " watch=" + watch + " dest=" + (_cfg.IsPrimaryDest() ? "Primary" : _cfg.DestDevice);
    }

    // ---------------- Graceful-quit IPC (--quit) ----------------
    private static string QuitEventName(string exePath)
    {
        try
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return "DisplayGuardTray_Quit_" + sb.ToString();
            }
        }
        catch { return "DisplayGuardTray_Quit"; }
    }

    private static int CliQuit(string exePath)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        EventWaitHandle ev;
        try
        {
            ev = EventWaitHandle.OpenExisting(QuitEventName(exePath));
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Console.WriteLine("No running tray instance for this exe path.");
            return 3;
        }
        using (ev)
        {
            ev.Set();
        }
        // Give the tray a moment to exit gracefully, then verify.
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            if (!TrayInstanceAlive(exePath))
            {
                Console.WriteLine("Tray instance exited gracefully.");
                return 0;
            }
        }
        Console.WriteLine("WARNING: quit signal sent but tray instance is still alive.");
        return 4;
    }

    private static bool TrayInstanceAlive(string exePath)
    {
        try
        {
            string want = Path.GetFullPath(exePath);
            foreach (Process p in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(exePath)))
            {
                try
                {
                    string got = p.MainModule != null ? p.MainModule.FileName : null;
                    if (got != null &&
                        string.Equals(Path.GetFullPath(got), want, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* access denied etc.: skip */ }
            }
        }
        catch { /* assume gone */ }
        return false;
    }

    // ---------------- Monitor enumeration ----------------
    internal static List<Mon> EnumMonitors()
    {
        var list = new List<Mon>();
        MonitorEnumProc cb = delegate (IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var m = new Mon();
                m.Handle = hMonitor;
                m.DeviceName = mi.szDevice;
                m.Bounds = mi.rcMonitor;
                m.Work = mi.rcWork;
                m.Primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                FillMonitorDescription(m);
                m.Friendly = GetRegistryFriendlyName(m.DeviceId);
                list.Add(m);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return list;
    }

    private static void FillMonitorDescription(Mon m)
    {
        m.Description = "";
        m.DeviceId = "";
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(m.DeviceName, i, ref dd, 0)) break;
                bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                if (i == 0 || attached)
                {
                    m.Description = dd.DeviceString ?? "";
                    m.DeviceId = dd.DeviceID ?? "";
                    if (attached) break;
                }
            }
        }
        catch { /* leave blanks */ }
    }

    // The EDID friendly name (e.g. "EP-HDMI-RX") is not exposed by EnumDisplayDevices;
    // read it from HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<HWID>\<instance>\FriendlyName.
    private static string GetRegistryFriendlyName(string deviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            string[] parts = deviceId.Split('\\');
            if (parts.Length < 2) return "";
            string hwid = parts[1];
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + hwid))
            {
                if (root == null) return "";
                var names = new List<string>();
                foreach (string sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey inst = root.OpenSubKey(sub))
                        {
                            if (inst == null) continue;
                            object v = inst.GetValue("FriendlyName");
                            if (v != null)
                            {
                                string s = v.ToString();
                                if (!names.Contains(s)) names.Add(s);
                            }
                        }
                    }
                    catch { /* single instance failed: continue */ }
                }
                return string.Join(" | ", names.ToArray());
            }
        }
        catch { return ""; }
    }

    // ---------------- Target / destination selection ----------------
    // Marks Mon.IsTarget. Auto: keyword match on Description/DeviceId/Friendly,
    // fallback = all non-primary monitors. Manual: any exact DeviceName in WatchDevices.
    internal static void SelectTargets(List<Mon> monitors, Config cfg, out bool keywordMatched)
    {
        keywordMatched = false;
        foreach (Mon m in monitors) m.IsTarget = false;

        if (cfg.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            List<string> wanted = cfg.WatchList();
            foreach (Mon m in monitors)
            {
                foreach (string d in wanted)
                {
                    if (string.Equals(m.DeviceName, d, StringComparison.OrdinalIgnoreCase))
                    {
                        m.IsTarget = true;
                        keywordMatched = true;
                        break;
                    }
                }
            }
            return;
        }

        if (!string.IsNullOrEmpty(cfg.PhantomKeyword))
        {
            foreach (Mon m in monitors)
            {
                string hay = (m.Description ?? "") + "\n" + (m.DeviceId ?? "") + "\n" + (m.Friendly ?? "");
                if (hay.IndexOf(cfg.PhantomKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    m.IsTarget = true;
                    keywordMatched = true;
                }
            }
        }
        if (!keywordMatched)
        {
            foreach (Mon m in monitors)
                if (!m.Primary) m.IsTarget = true;
        }
    }

    // Marks Mon.IsDest and returns the effective destination monitor.
    // DestDevice=Primary (or unset) -> current primary; explicit device -> exact match,
    // fallback to primary when the configured device is not connected.
    internal static Mon SelectDest(List<Mon> monitors, Config cfg)
    {
        foreach (Mon m in monitors) m.IsDest = false;
        Mon primary = monitors.Find(m => m.Primary);
        Mon dest = null;
        if (!cfg.IsPrimaryDest())
            dest = monitors.Find(m => string.Equals(m.DeviceName, cfg.DestDevice, StringComparison.OrdinalIgnoreCase));
        if (dest == null) dest = primary;
        if (dest == null && monitors.Count > 0) dest = monitors[0];
        if (dest != null) dest.IsDest = true;
        return dest;
    }

    // Device name of the effective destination (for menu exclusion / conflict checks).
    internal static string EffectiveDestDeviceName(List<Mon> monitors, Config cfg)
    {
        if (!cfg.IsPrimaryDest()) return cfg.DestDevice;
        Mon primary = monitors.Find(m => m.Primary);
        return primary != null ? primary.DeviceName : "";
    }

    // ---------------- Scan / move ----------------
    // Returns number of windows moved (or that would move in dry mode).
    internal static int Scan(List<Mon> monitors, Config cfg, bool doMove, bool verbose)
    {
        List<Mon> targets = monitors.FindAll(m => m.IsTarget);
        if (targets.Count == 0)
        {
            if (verbose) Console.WriteLine("No watched monitor present; standby (nothing moved).");
            return 0;
        }
        Mon dest = SelectDest(monitors, cfg);
        if (dest == null)
        {
            if (verbose) Console.WriteLine("ERROR: no destination monitor; nothing moved.");
            return 0;
        }

        int moved = 0;
        int selfPid = Process.GetCurrentProcess().Id;
        RECT work = dest.Work;

        EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) <= 0) return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if ((int)pid == selfPid) return true;

                try
                {
                    int cloaked;
                    if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                        return true;
                }
                catch { /* dwmapi unavailable: ignore */ }

                RECT rc;
                if (!GetWindowRect(hWnd, out rc)) return true;
                if (rc.Width <= 0 || rc.Height <= 0) return true;

                Mon src = FindTargetMon(rc, targets);
                if (src == null) return true;
                if (src.Handle == dest.Handle) return true;   // watched screen == destination: no-op

                var sb = new StringBuilder(GetWindowTextLength(hWnd) + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                bool maximized = IsZoomed(hWnd);
                int newX = Clamp(rc.Left, work.Left, Math.Max(work.Left, work.Right - rc.Width));
                int newY = Clamp(rc.Top, work.Top, Math.Max(work.Top, work.Bottom - rc.Height));

                if (!doMove)
                {
                    if (verbose)
                        Console.WriteLine("[DRY ] \"" + title + "\" pid=" + pid + " at " + rc +
                                          (maximized ? " [maximized]" : "") + " -> would move to (" + newX + "," + newY + ")");
                    moved++;
                    return true;
                }

                if (maximized)
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    Thread.Sleep(60);
                    GetWindowRect(hWnd, out rc);
                    newX = Clamp(rc.Left, work.Left, Math.Max(work.Left, work.Right - rc.Width));
                    newY = Clamp(rc.Top, work.Top, Math.Max(work.Top, work.Bottom - rc.Height));
                }

                bool ok = SetWindowPos(hWnd, IntPtr.Zero, newX, newY, 0, 0,
                                       SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                if (ok)
                {
                    if (verbose)
                        Console.WriteLine("[MOVE] \"" + title + "\" pid=" + pid + " from (" + rc.Left + "," + rc.Top +
                                          ") to (" + newX + "," + newY + ")" + (maximized ? " [was maximized]" : ""));
                    Log.Write("MOVE \"" + title + "\" (pid=" + pid + ") from (" + rc.Left + "," + rc.Top +
                              ") to (" + newX + "," + newY + ") via " + src.DeviceName + " -> " + dest.DeviceName);
                    moved++;
                }
                else if (verbose)
                {
                    Console.WriteLine("[FAIL] \"" + title + "\" pid=" + pid + " SetWindowPos error=" +
                                      Marshal.GetLastWin32Error());
                }
            }
            catch { /* never let one window kill the scan */ }
            return true;
        };

        EnumWindows(cb, IntPtr.Zero);
        if (verbose) Console.WriteLine((doMove ? "Moved " : "Would move ") + moved + " window(s).");
        return moved;
    }

    private static Mon FindTargetMon(RECT rc, List<Mon> targets)
    {
        IntPtr hMon = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
        foreach (Mon p in targets)
        {
            if (hMon == p.Handle) return p;
            RECT inter = Intersect(rc, p.Bounds);
            long winArea = rc.Area;
            if (winArea > 0 && inter.Area * 2 > winArea) return p;
        }
        return null;
    }

    private static RECT Intersect(RECT a, RECT b)
    {
        var r = new RECT();
        r.Left = Math.Max(a.Left, b.Left);
        r.Top = Math.Max(a.Top, b.Top);
        r.Right = Math.Min(a.Right, b.Right);
        r.Bottom = Math.Min(a.Bottom, b.Bottom);
        if (r.Right < r.Left) r.Right = r.Left;
        if (r.Bottom < r.Top) r.Bottom = r.Top;
        return r;
    }

    private static int Clamp(int v, int lo, int hi)
    {
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    // ---------------- CLI modes ----------------
    private static int CliList()
    {
        List<Mon> monitors = EnumMonitors();
        if (monitors.Count == 0)
        {
            Console.WriteLine("ERROR: no monitors enumerated.");
            return 2;
        }
        bool kw;
        SelectTargets(monitors, _cfg, out kw);
        Mon dest = SelectDest(monitors, _cfg);

        Console.WriteLine("WatchMode     : " + _cfg.WatchMode +
                          (_cfg.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
                              ? " (devices=" + (_cfg.WatchDevices.Length > 0 ? _cfg.WatchDevices : "<empty>") +
                                (kw ? ", matched" : ", none present") + ")"
                              : " (keyword=\"" + _cfg.PhantomKeyword + "\"" +
                                (kw ? ", matched" : ", NOT matched -> fallback: all non-primary") + ")"));
        Console.WriteLine("DestDevice    : " + (_cfg.IsPrimaryDest() ? "Primary" : _cfg.DestDevice) +
                          (dest != null ? " (resolved " + dest.DeviceName + ")" : " (unresolved!)"));
        Console.WriteLine("Enabled       : " + _cfg.Enabled);
        Console.WriteLine("Interval      : " + _cfg.IntervalMs + " ms");
        Console.WriteLine("Monitors      : " + monitors.Count);
        Console.WriteLine(new string('-', 96));
        foreach (Mon m in monitors)
        {
            Console.WriteLine("Device      : " + m.DeviceName);
            Console.WriteLine("Description : " + m.Description);
            Console.WriteLine("DeviceID    : " + m.DeviceId);
            Console.WriteLine("FriendlyName: " + m.Friendly);
            Console.WriteLine("Bounds      : " + m.Bounds);
            Console.WriteLine("WorkArea    : " + m.Work);
            Console.WriteLine("Primary     : " + m.Primary);
            Console.WriteLine("Watched     : " + m.IsTarget);
            Console.WriteLine("Destination : " + m.IsDest);
            Console.WriteLine(new string('-', 96));
        }
        return 0;
    }

    private static int CliScan(bool doMove)
    {
        List<Mon> monitors = EnumMonitors();
        if (monitors.Count == 0)
        {
            Console.WriteLine("ERROR: no monitors enumerated.");
            return 2;
        }
        bool kw;
        SelectTargets(monitors, _cfg, out kw);
        Scan(monitors, _cfg, doMove, true);
        return 0;
    }

    // ---------------- Tray application ----------------
    private sealed class TrayApp : ApplicationContext
    {
        private static readonly int[] IntervalOptions = { 500, 1000, 2000, 5000, 10000, 30000 };

        private readonly Config _cfg;
        private readonly string _exePath;
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _statusItem;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _quitPoll;
        private EventWaitHandle _quitEvent;
        private bool _hadTarget;
        private bool _exiting;

        public TrayApp(Config cfg, string exePath)
        {
            _cfg = cfg;
            _exePath = exePath;

            _menu = new ContextMenuStrip();
            _menu.Opening += delegate { RebuildMenu(); };

            _icon = new NotifyIcon();
            try { _icon.Icon = Icon.ExtractAssociatedIcon(_exePath) ?? SystemIcons.Shield; }
            catch { _icon.Icon = SystemIcons.Shield; }
            _icon.Text = "DisplayGuard";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { RunOnceFromMenu(); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Tick += delegate { Tick(); };
            ApplyTimer();

            // Graceful-quit IPC: a --quit CLI invocation signals this named event.
            try
            {
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName(exePath));
                _quitPoll = new System.Windows.Forms.Timer();
                _quitPoll.Interval = 300;
                _quitPoll.Tick += delegate
                {
                    try
                    {
                        if (_quitEvent != null && _quitEvent.WaitOne(0)) ExitApp();
                    }
                    catch { /* polling is best effort */ }
                };
                _quitPoll.Start();
            }
            catch { /* IPC unavailable: tray still works, --quit will not */ }

            // Initial state: establish baseline before the first tick so we do not
            // balloon-tip for a state that was already true at startup.
            List<Mon> monitors = EnumMonitors();
            bool kw;
            SelectTargets(monitors, _cfg, out kw);
            _hadTarget = monitors.Exists(m => m.IsTarget);

            // v2.1: no startup balloon (with or without --quiet); only state-change
            // balloons remain. Startup is recorded in the log instead.
            Tick();
        }

        private void ApplyTimer()
        {
            _timer.Stop();
            _timer.Interval = Math.Max(100, _cfg.IntervalMs);
            if (_cfg.Enabled) _timer.Start();
        }

        private void Balloon(string title, string text, int ms)
        {
            try { _icon.ShowBalloonTip(ms, title, text, ToolTipIcon.Info); }
            catch { /* balloon tips are best effort */ }
        }

        private string StatusText()
        {
            if (!_cfg.Enabled) return "已暂停";
            return _hadTarget ? "防护中" : "未检测到检测屏幕";
        }

        // One watchdog pass: re-enumerate monitors, update status, move windows.
        private void Tick()
        {
            try
            {
                List<Mon> monitors = EnumMonitors();
                bool kw;
                SelectTargets(monitors, _cfg, out kw);
                bool present = monitors.Exists(m => m.IsTarget);

                if (present != _hadTarget)
                {
                    if (_cfg.Enabled)
                    {
                        if (present)
                        {
                            Mon t = monitors.Find(m => m.IsTarget);
                            string name = (t != null && t.Friendly.Length > 0) ? t.Friendly
                                        : (t != null ? t.DeviceName : _cfg.PhantomKeyword);
                            Balloon("DisplayGuard", "已检测到检测屏幕：" + name + "，防护恢复。", 2500);
                            Log.Write("TARGET FOUND " + (t != null ? t.DeviceName : "?") +
                                      " (" + name + ")");
                        }
                        else
                        {
                            Balloon("DisplayGuard", "检测屏幕已断开，进入待机。", 2500);
                            Log.Write("TARGET LOST (no watched monitor present; standby)");
                        }
                    }
                    _hadTarget = present;
                }

                if (!_cfg.Enabled) return;      // paused: timer is stopped anyway
                if (!present) return;           // standby: do not touch any window

                Scan(monitors, _cfg, true, false);
            }
            catch { /* the watchdog must never die on one bad pass */ }
        }

        private void RunOnceFromMenu()
        {
            try
            {
                List<Mon> monitors = EnumMonitors();
                bool kw;
                SelectTargets(monitors, _cfg, out kw);
                bool present = monitors.Exists(m => m.IsTarget);
                _hadTarget = present;
                if (!present)
                {
                    Balloon("DisplayGuard", "未检测到检测屏幕，未执行搬移。", 2000);
                    return;
                }
                int moved = Scan(monitors, _cfg, true, false);
                Balloon("DisplayGuard", "检查完成：搬移 " + moved + " 个窗口。", 2000);
            }
            catch { /* ignore */ }
        }

        // ---------------- Menu ----------------
        private void RebuildMenu()
        {
            _menu.Items.Clear();

            _statusItem = new ToolStripMenuItem("状态：" + StatusText());
            _statusItem.Enabled = false;
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());

            // Master switch
            var enableItem = new ToolStripMenuItem("启用防护");
            enableItem.Checked = _cfg.Enabled;
            enableItem.Click += delegate
            {
                _cfg.Enabled = !_cfg.Enabled;
                _cfg.Save();
                ApplyTimer();
            };
            _menu.Items.Add(enableItem);

            _menu.Items.Add(BuildWatchMenu());
            _menu.Items.Add(BuildDestMenu());
            _menu.Items.Add(BuildIntervalMenu());

            // Autostart: check state reflects the actual registry value; silently
            // upgrade legacy entries (no --quiet) to the v2.1 format.
            FixLegacyAutostart();
            var startItem = new ToolStripMenuItem("开机自启动");
            startItem.Checked = IsAutostartEnabled();
            startItem.Click += delegate
            {
                bool want = !IsAutostartEnabled();
                bool ok = SetAutostart(want);
                if (!ok)
                    MessageBox.Show("写入注册表自启动项失败（可能被安全软件拦截）。",
                                    "DisplayGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            _menu.Items.Add(startItem);

            _menu.Items.Add(new ToolStripSeparator());

            var onceItem = new ToolStripMenuItem("立即检查一次");
            onceItem.Click += delegate { RunOnceFromMenu(); };
            _menu.Items.Add(onceItem);

            var logsItem = new ToolStripMenuItem("打开日志文件夹");
            logsItem.Click += delegate
            {
                try
                {
                    if (Log.Dir != null)
                    {
                        Directory.CreateDirectory(Log.Dir);
                        Process.Start("explorer.exe", "\"" + Log.Dir + "\"");
                    }
                }
                catch { /* best effort */ }
            };
            _menu.Items.Add(logsItem);

            _menu.Items.Add(new ToolStripSeparator());

            var aboutItem = new ToolStripMenuItem("关于 DisplayGuard");
            aboutItem.Click += delegate
            {
                MessageBox.Show(
                    "DisplayGuard\r\n\r\n" +
                    "版本：2.1.1\r\n\r\n" +
                    "功能说明：\r\n" +
                    "自动监测指定显示器上的窗口，并将其迁移至目标显示器，" +
                    "保障多屏环境下的窗口可见性。\r\n\r\n" +
                    "运行环境：\r\n" +
                    "Windows 10 / 11，.NET Framework 4.x，免安装。\r\n\r\n" +
                    "作者：UTwevle",
                    "关于 DisplayGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            _menu.Items.Add(aboutItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };
            _menu.Items.Add(exitItem);
        }

        private static string MonLabel(Mon m)
        {
            return m.DeviceName + " — " +
                   (m.Friendly.Length > 0 ? m.Friendly : m.Description) + " " +
                   m.Bounds.Width + "×" + m.Bounds.Height;
        }

        // 检测屏幕 submenu: auto keyword mode + multi-select watched screens.
        private ToolStripMenuItem BuildWatchMenu()
        {
            var root = new ToolStripMenuItem("检测屏幕");
            List<Mon> monitors = EnumMonitors();
            string destName = EffectiveDestDeviceName(monitors, _cfg);

            var autoItem = new ToolStripMenuItem("自动检测（" + _cfg.PhantomKeyword + "）");
            autoItem.Checked = _cfg.WatchMode.Equals("Auto", StringComparison.OrdinalIgnoreCase);
            autoItem.Click += delegate
            {
                _cfg.WatchMode = "Auto";
                _cfg.Save();
                Tick();
            };
            root.DropDownItems.Add(autoItem);
            root.DropDownItems.Add(new ToolStripSeparator());

            List<string> watchList = _cfg.WatchList();
            var listed = new List<string>();
            foreach (Mon m in monitors)
            {
                // Never offer the effective destination screen as a watched screen.
                if (string.Equals(m.DeviceName, destName, StringComparison.OrdinalIgnoreCase)) continue;
                listed.Add(m.DeviceName);

                var item = new ToolStripMenuItem(MonLabel(m));
                item.Tag = m.DeviceName;
                item.Checked = _cfg.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase) &&
                               watchList.Contains(m.DeviceName);
                item.Click += delegate(object s, EventArgs e)
                {
                    var clicked = (ToolStripMenuItem)s;
                    string dev = (string)clicked.Tag;
                    List<string> cur = _cfg.WatchList();
                    if (cur.Contains(dev))
                    {
                        cur.Remove(dev);
                        _cfg.SetWatchList(cur);
                        // Stay in Manual mode even with an empty list (explicit standby).
                        _cfg.WatchMode = "Manual";
                    }
                    else
                    {
                        // Manual selection disables auto-detect.
                        _cfg.WatchMode = "Manual";
                        // Conflict guard: a watched screen can never be the destination.
                        if (string.Equals(dev, _cfg.DestDevice, StringComparison.OrdinalIgnoreCase) &&
                            !_cfg.IsPrimaryDest())
                        {
                            _cfg.DestDevice = "Primary";
                            Balloon("DisplayGuard",
                                    dev + " 已加入检测列表，转移目标已自动切回“主屏（自动）”。", 2500);
                        }
                        cur.Add(dev);
                        _cfg.SetWatchList(cur);
                    }
                    _cfg.Save();
                    Tick();
                };
                root.DropDownItems.Add(item);
            }

            // Selected devices that are not currently connected.
            if (_cfg.WatchMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string dev in watchList)
                {
                    if (listed.Contains(dev)) continue;
                    var missing = new ToolStripMenuItem("（已选择，当前未连接：" + dev + "）");
                    missing.Enabled = false;
                    root.DropDownItems.Add(missing);
                }
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            var rescan = new ToolStripMenuItem("重新扫描显示器");
            rescan.Click += delegate { Tick(); };
            root.DropDownItems.Add(rescan);

            return root;
        }

        // 转移到 submenu: single-select destination screen.
        private ToolStripMenuItem BuildDestMenu()
        {
            var root = new ToolStripMenuItem("转移到");
            List<Mon> monitors = EnumMonitors();

            var primaryItem = new ToolStripMenuItem("主屏（自动）");
            primaryItem.Checked = _cfg.IsPrimaryDest();
            primaryItem.Click += delegate(object s, EventArgs e)
            {
                _cfg.DestDevice = "Primary";
                _cfg.Save();
                UncheckSiblings((ToolStripMenuItem)s);
                Tick();
            };
            root.DropDownItems.Add(primaryItem);

            foreach (Mon m in monitors)
            {
                var item = new ToolStripMenuItem(MonLabel(m));
                item.Tag = m.DeviceName;
                item.Checked = !_cfg.IsPrimaryDest() &&
                               string.Equals(_cfg.DestDevice, m.DeviceName, StringComparison.OrdinalIgnoreCase);
                item.Click += delegate(object s, EventArgs e)
                {
                    var clicked = (ToolStripMenuItem)s;
                    string dev = (string)clicked.Tag;
                    _cfg.DestDevice = dev;
                    // Conflict guard: the destination can never stay in the watch list.
                    List<string> cur = _cfg.WatchList();
                    if (cur.Contains(dev))
                    {
                        cur.Remove(dev);
                        _cfg.SetWatchList(cur);
                        Balloon("DisplayGuard",
                                dev + " 已设为转移目标，并已从检测列表中移除。", 2500);
                    }
                    _cfg.Save();
                    UncheckSiblings(clicked);
                    Tick();
                };
                root.DropDownItems.Add(item);
            }

            return root;
        }

        private static void UncheckSiblings(ToolStripMenuItem self)
        {
            try
            {
                if (self == null || self.OwnerItem == null) return;
                var parent = self.OwnerItem as ToolStripMenuItem;
                if (parent == null) return;
                foreach (ToolStripItem it in parent.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null && mi != self) mi.Checked = false;
                }
                self.Checked = true;
            }
            catch { /* cosmetic only */ }
        }

        private ToolStripMenuItem BuildIntervalMenu()
        {
            var root = new ToolStripMenuItem("检查间隔");
            foreach (int ms in IntervalOptions)
            {
                string label = (ms < 1000)
                    ? (ms / 1000.0).ToString("0.#") + " 秒"
                    : (ms / 1000).ToString() + " 秒";
                if (ms == 5000) label += "（默认）";
                var item = new ToolStripMenuItem(label);
                item.Tag = ms;
                item.Checked = (_cfg.IntervalMs == ms);
                item.Click += delegate(object s, EventArgs e)
                {
                    _cfg.IntervalMs = (int)((ToolStripMenuItem)s).Tag;
                    _cfg.Save();
                    ApplyTimer();
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        // ---------------- Autostart (HKCU Run key) ----------------
        private static string AutostartValue(string exePath)
        {
            return "\"" + exePath + "\" --quiet";
        }

        private string ReadAutostartRaw()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return null;
                    object v = key.GetValue(RunValueName);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        private bool IsAutostartEnabled()
        {
            string raw = ReadAutostartRaw();
            if (raw == null) return false;
            string exe = ExtractExePath(raw);
            return exe != null && string.Equals(exe, _exePath, StringComparison.OrdinalIgnoreCase);
        }

        // Extracts the exe path from a Run value like "\"C:\\a b.exe\" --quiet" or "C:\\a.exe".
        private static string ExtractExePath(string raw)
        {
            try
            {
                string s = raw.Trim();
                if (s.Length == 0) return null;
                if (s.StartsWith("\""))
                {
                    int end = s.IndexOf('"', 1);
                    if (end > 1) return s.Substring(1, end - 1);
                    return null;
                }
                int sp = s.IndexOf(' ');
                return sp > 0 ? s.Substring(0, sp) : s;
            }
            catch { return null; }
        }

        // v2.1: autostart entries must carry --quiet. Rewrite legacy values in place.
        private void FixLegacyAutostart()
        {
            try
            {
                string raw = ReadAutostartRaw();
                if (raw == null) return;
                string exe = ExtractExePath(raw);
                if (exe == null || !string.Equals(exe, _exePath, StringComparison.OrdinalIgnoreCase)) return;
                if (raw.IndexOf("--quiet", StringComparison.OrdinalIgnoreCase) >= 0) return;
                SetAutostart(true);
            }
            catch { /* best effort */ }
        }

        private bool SetAutostart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    if (enable)
                        key.SetValue(RunValueName, AutostartValue(_exePath));
                    else
                        key.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch { return false; }
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try { _timer.Stop(); } catch { }
            try { if (_quitPoll != null) _quitPoll.Stop(); } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            ExitThread();   // Main() logs EXIT after Application.Run returns
        }
    }
}
