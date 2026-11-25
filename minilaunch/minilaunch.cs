// minilaunch: tiny Spotlight-style app launcher and logon shell for a session without explorer.exe.
// Tap the Windows key (or Alt+Space) to toggle the search box; Win+E opens a file window.
// Build: csc /target:winexe /out:minilaunch.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll minilaunch.cs
// Run:   minilaunch.exe --shell          as the Winlogon shell: also draws the desktop and starts the Run-key apps
//        minilaunch.exe --desktop        launcher + desktop without the shell duties
// Test:  minilaunch.exe --query <text>   (prints ranked results to stdout and exits)
//        minilaunch.exe --render out.png [w h]   (renders the desktop to a PNG and exits)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class Entry {
    public string Name, Target, Kind, Sub;   // Kind: App, System, Run, Shell
    public char Glyph;                       // Segoe MDL2 Assets glyph; '\0' = use the file's icon
    public string[] Aliases;                 // extra search terms ("reboot" -> Restart)
    public int Score;
}

static class Index {
    // Per-user settings the installer writes; each has a default that matches the standard install.
    public static string Setting(string name, string fallback) {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\minilaunch")) {
                var v = k == null ? null : k.GetValue(name) as string;
                if (!string.IsNullOrEmpty(v)) return Environment.ExpandEnvironmentVariables(v);
            }
        } catch { }
        return fallback;
    }

    public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);

    // Portable Windows Terminal: conhost garbles Claude Code-style TUIs (no glyph fallback, poor VT redraw),
    // and the Store build can't start without explorer. Per-user, because it writes its settings next to itself.
    public static readonly string TerminalExe = Setting("TerminalExe", Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"postmodern-windows\terminal\WindowsTerminal.exe"));
    public static readonly bool HasTerminal = File.Exists(TerminalExe);

    // PowerShell 7 when installed, else the Windows PowerShell 5.1 every Windows 10 ships with.
    public static readonly string PowerShell = FindPwsh() ?? "powershell.exe";

    static string FindPwsh() {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7"));
        foreach (var d in dirs) {
            try { if (d.Length > 0 && File.Exists(Path.Combine(d, "pwsh.exe"))) return Path.Combine(d, "pwsh.exe"); } catch { }
        }
        return null;
    }

    // Runs a command line in a terminal window: Windows Terminal when installed, else a bare console.
    public static ProcessStartInfo InTerminal(string exe, string args) {
        if (!HasTerminal) return new ProcessStartInfo(exe, args) { UseShellExecute = true };
        // wt treats ';' as its own subcommand separator; escape it so it reaches the shell intact
        return new ProcessStartInfo(TerminalExe, exe + " " + args.Replace(";", "\\;")) { UseShellExecute = true };
    }

    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "minilaunch");
    static readonly string HistoryFile = Path.Combine(DataDir, "history.txt");
    public static Dictionary<string, int> History = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public static List<Entry> All = new List<Entry>();
    public static bool ShellMode;

    public static void Log(string msg) {
        try {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "error.log"), DateTime.Now.ToString("s") + " " + msg + "\r\n");
        } catch { }
    }

    public static void LoadHistory() {
        History.Clear();
        try {
            foreach (var line in File.ReadAllLines(HistoryFile)) {
                var p = line.Split('\t');
                int n;
                if (p.Length == 2 && int.TryParse(p[0], out n)) History[p[1]] = n;
            }
        } catch { }
    }

    public static void Bump(Entry e) {
        if (e.Kind == "Run" || e.Kind == "Shell") return;
        int n;
        History.TryGetValue(e.Name, out n);
        History[e.Name] = n + 1;
        try {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(HistoryFile, History.Select(kv => kv.Value + "\t" + kv.Key).ToArray());
        } catch { }
    }

    public static void Rebuild() {
        var list = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] {
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "" },
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "" },
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Desktop" },
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop" }
        };
        foreach (var root in roots) {
            if (string.IsNullOrEmpty(root[0]) || !Directory.Exists(root[0])) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root[0], "*.*", SearchOption.AllDirectories); } catch { continue; }
            foreach (var f in files) {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                var name = Path.GetFileNameWithoutExtension(f);
                var lower = name.ToLowerInvariant();
                if (lower.Contains("uninstall") || lower.Contains("readme") || lower.EndsWith(" help")) continue;
                if (lower == "immersive control panel" || lower == "settings") continue;  // UWP, broken without explorer
                if (HasTerminal && lower.StartsWith("powershell 7")) continue;   // opens bare conhost; the Terminal entry covers it
                var folder = root[1];
                if (folder.Length == 0) {
                    folder = Path.GetDirectoryName(f).Substring(root[0].Length).Trim('\\');
                    if (folder.StartsWith("Programs", StringComparison.OrdinalIgnoreCase)) folder = folder.Substring(8).Trim('\\');
                }
                var sub = folder.Length > 0 ? "Application  \u00b7  " + folder.Replace("\\", " \u203a ") : "Application";
                if (seen.Add(name)) list.Add(new Entry { Name = name, Target = f, Kind = "App", Sub = sub });
            }
        }

        // Store (UWP) apps and the Settings app can't activate without explorer's immersive shell,
        // so only classic Win32 tools are offered here.
        var sys = new object[][] {
            new object[] { "Control Panel (settings)", "control.exe", '\uE713', "Classic settings" },
            new object[] { "Task Manager", "taskmgr.exe", '\uE9D9', "Processes and performance" },
            new object[] { "File Explorer", "explorer.exe", '\uEC50', "Browse files  \u00b7  Win+E" },
            new object[] { "Terminal", "!terminal", '\uE756', "New PowerShell window" },
            new object[] { "Command Prompt", "cmd.exe", '\uE756', "New cmd window" },
            new object[] { "Wi-Fi networks", "!wifi", '\uE701', "Pick and connect to a network" },
            new object[] { "Network Connections", "ncpa.cpl", '\uE968', "Adapters" },
            new object[] { "Sound", "mmsys.cpl", '\uE767', "Playback and recording devices" },
            new object[] { "Volume Mixer", "sndvol.exe", '\uE767', "Per-app volume" },
            new object[] { "Power Options", "powercfg.cpl", '\uE945', "Power plans" },
            new object[] { "Device Manager", "devmgmt.msc", '\uE772', "Hardware and drivers" },
            new object[] { "Devices and Printers", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}", '\uE749', "Connected devices" },
            new object[] { "Add Bluetooth device", "DevicePairingWizard.exe", '\uE702', "Pair a new device" },
            new object[] { "Mouse / Touchpad", "main.cpl", '\uE962', "Pointer settings" },
            new object[] { "Keyboard layouts (Language)", "!intl", '\uE765', "Region and input" },
            new object[] { "Date and Time", "timedate.cpl", '\uE823', "Clock and time zone" },
            new object[] { "Theme", "!theme", '\uE790', "Wallpaper, lock screen and colours" },
            new object[] { "Lock", "!lock", '\uE72E', "Lock the session" },
            new object[] { "Log off", "!logoff", '\uE748', "Sign out" },
            new object[] { "Restart", "!restart", '\uE777', "Restart now" },
            new object[] { "Shut down", "!shutdown", '\uE7E8', "Power off now" },
            new object[] { "Sleep", "!sleep", '\uE708', "Suspend" },
            new object[] { "Quit launcher", "!quit", '\uE711', "Close minilaunch" },
        };
        foreach (var s in sys) {
            var name = (string)s[0];
            var target = (string)s[1];
            if (ShellMode && target == "!quit") continue;   // it's the shell; nothing to come back to
            string[] aliases;
            AliasMap.TryGetValue(target, out aliases);
            if (seen.Add(name)) list.Add(new Entry { Name = name, Target = target, Glyph = (char)s[2], Sub = "System  \u00b7  " + s[3], Kind = "System", Aliases = aliases });
            else if (aliases != null) list.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Aliases = aliases;   // e.g. a Start Menu "File Explorer" shortcut
        }
        All = list;   // single reference swap, so a rebuild on a worker thread is safe for readers
    }

    static readonly Dictionary<string, string[]> AliasMap = new Dictionary<string, string[]> {
        { "!restart", new[] { "reboot" } },
        { "!shutdown", new[] { "power off", "poweroff", "shutdown", "turn off" } },
        { "!logoff", new[] { "sign out", "logout", "log out" } },
        { "!sleep", new[] { "suspend", "standby" } },
        { "!lock", new[] { "lock screen" } },
        { "!terminal", new[] { "powershell", "pwsh", "console", "shell", "windows terminal" } },
        { "control.exe", new[] { "settings", "preferences" } },
        { "taskmgr.exe", new[] { "processes" } },
        { "!wifi", new[] { "wireless", "network", "internet" } },
        { "explorer.exe", new[] { "files", "folders", "this pc" } },
        { "sndvol.exe", new[] { "volume" } },
        { "mmsys.cpl", new[] { "audio", "speakers", "microphone" } },
        { "DevicePairingWizard.exe", new[] { "bluetooth" } },
        { "powercfg.cpl", new[] { "battery" } },
        { "timedate.cpl", new[] { "clock" } },
        { "!intl", new[] { "language", "input" } },
        { "!theme", new[] { "wallpaper", "colours", "colors", "appearance", "personalize" } },
    };

    static string Compact(string s) { return new string(s.Where(char.IsLetterOrDigit).ToArray()); }

    // Higher is better; -1 = no match. Aliases score slightly below an equal match on the name.
    public static int Rank(Entry e, string q) {
        int s = RankText(e.Name, q);
        if (e.Aliases != null)
            foreach (var a in e.Aliases) { int sa = RankText(a, q); if (sa >= 0) s = Math.Max(s, sa - 10); }
        if (s < 0) return -1;
        int h;
        if (History.TryGetValue(e.Name, out h)) s += Math.Min(h, 30) * 15;
        return s;
    }

    static int RankText(string name, string q) {
        var n = name.ToLowerInvariant();
        int s;
        if (n == q) s = 1000;
        else if (n.StartsWith(q)) s = 800 - n.Length;
        else if (Compact(n).StartsWith(Compact(q))) s = 750 - n.Length;   // "wifi" -> "Wi-Fi"
        else {
            var words = n.Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            var initials = new string(words.Select(w => w[0]).ToArray());
            if (words.Any(w => w.StartsWith(q))) s = 600 - n.Length;
            else if (q.Length > 1 && initials.StartsWith(q)) s = 550;
            else if (n.Contains(q)) s = 400 - n.IndexOf(q);
            else {
                // subsequence match, penalised by gaps
                int i = 0, gaps = 0, last = -1;
                foreach (char c in q) {
                    int j = n.IndexOf(c, i);
                    if (j < 0) return -1;
                    if (last >= 0) gaps += j - last - 1;
                    last = j; i = j + 1;
                }
                s = 200 - gaps * 5;
            }
        }
        return s;
    }

    public static List<Entry> Search(string text, int max) {
        var raw = text.Trim();
        if (raw.StartsWith(">")) {
            var cmd = raw.Substring(1).Trim();
            return new List<Entry> { new Entry { Name = cmd.Length > 0 ? cmd : "Type a PowerShell command", Target = cmd,
                Kind = "Shell", Glyph = '\uE756', Sub = "Run in a PowerShell window" } };
        }
        var q = raw.ToLowerInvariant();
        var all = All;   // snapshot: a background rebuild may swap the list
        if (q.Length == 0)
            return all.Where(e => History.ContainsKey(e.Name))
                      .OrderByDescending(e => History[e.Name]).Take(max).ToList();
        foreach (var e in all) {
            e.Score = Rank(e, q);
            // power actions run instantly, so keep them off the top spot until the query is unambiguous
            if (e.Score >= 0 && q.Length < 4 && (e.Target == "!shutdown" || e.Target == "!restart" || e.Target == "!logoff"))
                e.Score -= 500;
        }
        var res = all.Where(e => e.Score >= 0).OrderByDescending(e => e.Score).ThenBy(e => e.Name.Length).Take(max).ToList();

        // Win+R behaviour: paths/URLs go first, anything unmatched becomes a run row
        bool looksLikePath = raw.Contains(":\\") || raw.Contains("://") || raw.StartsWith("\\\\") || raw.StartsWith("%");
        if (looksLikePath || res.Count == 0) {
            var run = new Entry { Name = raw, Target = raw, Kind = "Run", Glyph = looksLikePath ? '\uE8A7' : '\uE768',
                                  Sub = looksLikePath ? "Open" : "Run command" };
            if (looksLikePath) res.Insert(0, run); else res.Add(run);
            if (res.Count > max) res.RemoveAt(res.Count - 1);
        }
        return res;
    }
}

static class Icons {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint attr;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("comctl32.dll")] static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    // 32px icon for a file. SHGFI_SYSICONINDEX | SHGFI_LARGEICON: pulling the icon from the system
    // image list by index skips the overlay layer, so shortcuts come out without the arrow.
    public static Bitmap ForPath(string path) {
        try {
            var fi = new SHFILEINFO();
            var himl = SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf(fi), 0x4000);
            var hIcon = himl == IntPtr.Zero ? IntPtr.Zero : ImageList_GetIcon(himl, fi.iIcon, 0);
            if (hIcon == IntPtr.Zero) return null;
            Bitmap bmp;
            using (var ic = Icon.FromHandle(hIcon)) bmp = ic.ToBitmap();
            DestroyIcon(hIcon);
            return bmp;
        } catch { return null; }
    }

    // Copy of an icon handle we don't own (WM_GETICON / class icon): don't destroy it.
    public static Bitmap FromBorrowed(IntPtr hIcon) {
        try { using (var ic = Icon.FromHandle(hIcon)) return ic.ToBitmap(); } catch { return null; }
    }
}

static class Theme {
    public static readonly Color Bg = Color.FromArgb(28, 28, 32);
    public static readonly Color Border = Color.FromArgb(58, 58, 66);
    public static readonly Color Footer = Color.FromArgb(22, 22, 25);
    public static readonly Color Text = Color.FromArgb(240, 240, 244);
    public static readonly Color SubText = Color.FromArgb(146, 146, 156);
    public static readonly Color Faint = Color.FromArgb(105, 105, 115);
    public static Color Accent = ReadAccent();
    public static readonly Color SelSub = Color.FromArgb(225, 232, 245);

    // Desktop theme: themes\<name>\theme.ini next to the exe, named by HKCU\Software\minilaunch\Theme.
    // The defaults are the built-in cyberpunk look, so a missing or partial theme.ini still renders.
    // Read these at the point of use: Load runs first thing in Main, after any static field initialisers.
    public static string Name = "cyberpunk", Dir = "";
    public static Color Primary = Color.FromArgb(0, 232, 255), Secondary = Color.FromArgb(255, 64, 172);
    public static Color Ink = Color.FromArgb(14, 10, 26), Ink2 = Color.FromArgb(30, 12, 50);
    public static Color HudText = Color.FromArgb(238, 248, 255), HudDim = Color.FromArgb(150, 196, 222), HudFaint = Color.FromArgb(112, 132, 160);
    public static string Header = "NODE {MODEL}   ·   MINILAUNCH   ·   {USER}";
    public static string LabelMem = "MEM", LabelCpu = "CPU", LabelPwr = "PWR", LabelNet = "NET", LabelVpn = "VPN";

    public static void Load(string name) {
        if (string.IsNullOrEmpty(name)) {
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\minilaunch"))
                    name = k == null ? null : k.GetValue("Theme") as string;
            } catch { }
        }
        Name = string.IsNullOrEmpty(name) ? "cyberpunk" : name;
        Dir = Path.Combine(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "themes"), Name);
        var ini = Path.Combine(Dir, "theme.ini");
        if (!File.Exists(ini)) { Index.Log("theme: " + ini + " not found, using built-in colours"); return; }
        foreach (var raw in File.ReadAllLines(ini)) {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || eq < 1) continue;
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var val = line.Substring(eq + 1).Trim();
            try {
                switch (key) {
                    case "primary": Primary = ColorTranslator.FromHtml(val); break;
                    case "secondary": Secondary = ColorTranslator.FromHtml(val); break;
                    case "ink": Ink = ColorTranslator.FromHtml(val); break;
                    case "ink2": Ink2 = ColorTranslator.FromHtml(val); break;
                    case "text": HudText = ColorTranslator.FromHtml(val); break;
                    case "dim": HudDim = ColorTranslator.FromHtml(val); break;
                    case "faint": HudFaint = ColorTranslator.FromHtml(val); break;
                    case "accent": Accent = ColorTranslator.FromHtml(val); break;
                    case "header": Header = val; break;
                    case "label.mem": LabelMem = val; break;
                    case "label.cpu": LabelCpu = val; break;
                    case "label.pwr": LabelPwr = val; break;
                    case "label.net": LabelNet = val; break;
                    case "label.vpn": LabelVpn = val; break;
                }   // other keys (name, term.*) belong to set-theme.ps1
            } catch { Index.Log("theme: bad value in " + ini + ": " + line); }
        }
    }

    public static bool Preview;   // --render: sample user/host/model instead of this machine's

    // Header tokens: {user} {host} {model}, upper-cased when written in capitals.
    public static string Expand(string t) {
        string model = "PC";
        try {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")) {
                var v = k == null ? null : k.GetValue("SystemProductName") as string;
                if (!string.IsNullOrEmpty(v)) model = v.Trim();
            }
        } catch { }
        string user = Environment.UserName, host = Environment.MachineName.ToLowerInvariant();
        if (Preview) { user = "user"; host = "workstation"; model = "PC"; }
        return t.Replace("{USER}", user.ToUpperInvariant()).Replace("{user}", user)
                .Replace("{HOST}", host.ToUpperInvariant()).Replace("{host}", host)
                .Replace("{MODEL}", model.ToUpperInvariant()).Replace("{model}", model);
    }

    public static Color Mix(Color a, Color b, float t) {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static Color ReadAccent() {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM")) {
                var v = k == null ? null : k.GetValue("AccentColor");
                if (v is int) {
                    int abgr = (int)v;
                    var c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                    // keep white text readable on the selection
                    if (c.GetBrightness() < 0.75f) return c;
                }
            }
        } catch { }
        return Color.FromArgb(0, 99, 177);
    }

    public static GraphicsPath Rounded(RectangleF r, float rad) {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

class SearchBar : Panel {
    public readonly TextBox Box = new TextBox();
    readonly Font glyphFont;
    readonly float s;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    public SearchBar(float scale) {
        s = scale;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        Dock = DockStyle.Top;
        Height = (int)(64 * s);
        glyphFont = new Font("Segoe MDL2 Assets", 15f);
        Box.BorderStyle = BorderStyle.None;
        Box.Font = new Font("Segoe UI Semilight", 18f);
        Box.BackColor = Theme.Bg;
        Box.ForeColor = Theme.Text;
        Controls.Add(Box);
        Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, (IntPtr)1, "Search apps, settings and commands"); };  // EM_SETCUEBANNER
    }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        Box.Left = (int)(56 * s);
        Box.Width = Width - Box.Left - (int)(20 * s);
        Box.Top = (Height - Box.Height) / 2 + 1;
    }

    protected override void OnPaint(PaintEventArgs e) {
        TextRenderer.DrawText(e.Graphics, "\uE721", glyphFont, new Rectangle(0, 0, (int)(56 * s), Height), Theme.Accent,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

class ResultsView : Control {
    public List<Entry> Items = new List<Entry>();
    public int Selected;
    public int RowH, Pad;
    public string EmptyText = "";
    public event Action<int> Activated;
    readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
    readonly Font nameFont = new Font("Segoe UI", 12f);
    readonly Font subFont = new Font("Segoe UI", 8.75f);
    readonly Font glyphFont = new Font("Segoe MDL2 Assets", 15f);
    readonly Font hintFont = new Font("Segoe UI", 8.5f);
    readonly float s;
    Point lastMouse;

    public ResultsView(float scale) {
        s = scale;
        RowH = (int)(52 * s);
        Pad = (int)(6 * s);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);   // keep keyboard focus in the search box
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
    }

    public int ContentHeight { get { return Pad * 2 + Math.Max(1, Items.Count) * RowH; } }

    public void ClearIcons() {
        foreach (var b in icons.Values) if (b != null) b.Dispose();
        icons.Clear();
    }

    Bitmap IconFor(Entry r) {
        Bitmap bmp;
        if (icons.TryGetValue(r.Target, out bmp)) return bmp;
        bmp = Icons.ForPath(r.Target);
        icons[r.Target] = bmp;
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Items.Count == 0) {
            TextRenderer.DrawText(g, EmptyText, nameFont, new Rectangle(0, Pad, Width, RowH), Theme.Faint,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int iconBox = (int)(32 * s), left = (int)(14 * s), textX = left + iconBox + (int)(14 * s);
        for (int i = 0; i < Items.Count; i++) {
            var r = Items[i];
            var row = new Rectangle(Pad, Pad + i * RowH, Width - Pad * 2, RowH);
            bool sel = i == Selected;
            if (sel) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(new RectangleF(row.X, row.Y + 1, row.Width, row.Height - 2), 6 * s))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                g.SmoothingMode = SmoothingMode.None;
            }
            var iconRect = new Rectangle(row.X + left - Pad, row.Y + (RowH - iconBox) / 2, iconBox, iconBox);
            if (r.Glyph != '\0') {
                using (var path = Theme.Rounded(iconRect, 7 * s)) {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(sel ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(44, 44, 50))) g.FillPath(b, path);
                    g.SmoothingMode = SmoothingMode.None;
                }
                TextRenderer.DrawText(g, r.Glyph.ToString(), glyphFont, iconRect, sel ? Color.White : Theme.Text,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            } else {
                var bmp = IconFor(r);
                if (bmp != null) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, iconRect);
                }
            }
            int hintW = sel ? (int)(70 * s) : 0;
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, r.Name, nameFont, new Rectangle(row.X + textX - Pad, row.Y + (int)(7 * s), row.Width - textX - hintW, (int)(24 * s)),
                                  sel ? Color.White : Theme.Text, flags);
            TextRenderer.DrawText(g, r.Sub ?? "", subFont, new Rectangle(row.X + textX - Pad, row.Y + (int)(30 * s), row.Width - textX - hintW, (int)(18 * s)),
                                  sel ? Theme.SelSub : Theme.SubText, flags);
            if (sel)
                TextRenderer.DrawText(g, "Enter \u21b5", hintFont, new Rectangle(row.Right - hintW - (int)(10 * s), row.Y, hintW, RowH), Theme.SelSub,
                                      TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    int RowAt(Point p) {
        int i = (p.Y - Pad) / RowH;
        return p.Y >= Pad && i >= 0 && i < Items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if (e.Location == lastMouse) return;   // ignore the synthetic move when the window appears under the cursor
        lastMouse = e.Location;
        int i = RowAt(e.Location);
        if (i >= 0 && i != Selected) { Selected = i; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        int i = RowAt(e.Location);
        if (i >= 0 && Activated != null) Activated(i);
    }
}

class FooterBar : Control {
    public string Status = "";
    readonly Font font = new Font("Segoe UI", 8.5f);
    readonly float s;
    public FooterBar(float scale) {
        s = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Footer;
        Dock = DockStyle.Bottom;
        Height = (int)(30 * s);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor);
        using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(e.Graphics, "\u2191\u2193  select      \u21b5  open      Ctrl+Shift+\u21b5  as admin      >  PowerShell      Esc  close",
                              font, new Rectangle((int)(14 * s), 0, Width, Height), Theme.Faint, flags);
        TextRenderer.DrawText(e.Graphics, Status, font, new Rectangle(0, 0, Width - (int)(14 * s), Height), Theme.Faint, flags | TextFormatFlags.Right);
    }
}

class LauncherForm : Form {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    const int HK_LAUNCH = 1, HK_FILES = 2, HK_LWIN = 4, HK_RWIN = 5;
    const uint MOD_ALT = 1, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const int MaxResults = 7;

    readonly float s;
    readonly SearchBar search;
    readonly ResultsView results;
    readonly FooterBar footer;
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
    // one press of the Windows key, followed from its hotkey to key-up (see OnWinDown)
    readonly System.Windows.Forms.Timer winPoll = new System.Windows.Forms.Timer { Interval = 10 };
    readonly bool[] heldAtDown = new bool[256];
    bool winCombo, winOpens;
    IntPtr winPrev;
    DateTime lastIndex = DateTime.MinValue;
    bool shown;

    public LauncherForm() {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;   // screen DPI; don't create our handle yet
        Text = "minilaunch";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Border;          // 1px frame shows through the padding
        Padding = new Padding(1);
        Width = (int)(680 * s);

        search = new SearchBar(s);
        results = new ResultsView(s);
        footer = new FooterBar(s);
        Controls.Add(results);
        Controls.Add(footer);
        Controls.Add(search);

        search.Box.TextChanged += delegate { Refresh_(); };
        results.Activated += delegate(int i) { Launch(i, false); };
        Deactivate += delegate { HideBox(); };
        fade.Tick += delegate {
            Opacity = Math.Min(1.0, Opacity + 0.2);
            if (Opacity >= 1.0) fade.Stop();
        };
        winPoll.Tick += delegate { PollWin(); };
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, HK_LAUNCH, MOD_ALT | MOD_NOREPEAT, (uint)Keys.Space);
        RegisterHotKey(Handle, HK_FILES, MOD_WIN | MOD_NOREPEAT, (uint)Keys.E);
        // Win+LWin fires as the Windows key goes down, whatever window has focus
        if (!RegisterHotKey(Handle, HK_LWIN, MOD_WIN | MOD_NOREPEAT, 0x5B) | !RegisterHotKey(Handle, HK_RWIN, MOD_WIN | MOD_NOREPEAT, 0x5C))
            Index.Log("winkey: could not register the Windows key hotkeys");
        BeginInvoke(new Action(Trim));
    }

    // Toggle on a bare tap of either Windows key; combos (Win+E, Win+L, ...) are left alone.
    // A low-level keyboard hook can't do this reliably: UIPI hides an elevated window's keystrokes from it,
    // and Windows silently drops a hook that misses its timeout. The hotkey comes from win32k instead, and
    // receiving it grants foreground rights - but only until the key-up goes to the focused app. So take
    // focus now, invisibly, and follow the key with GetAsyncKeyState: the box fades in if the key comes up
    // with no other key pressed in between, otherwise focus goes straight back.
    void OnWinDown() {
        if (winPoll.Enabled) return;
        winCombo = false;
        winPrev = GetForegroundWindow();
        for (int vk = 1; vk < 256; vk++) heldAtDown[vk] = GetAsyncKeyState(vk) < 0;
        winOpens = !shown;
        if (winOpens) ShowBox(false);
        winPoll.Start();
    }

    void PollWin() {
        for (int vk = 7; vk < 256; vk++) {   // 1-6 are mouse buttons
            if (vk == 0x5B || vk == 0x5C) continue;
            if (GetAsyncKeyState(vk) >= 0) heldAtDown[vk] = false;
            else if (!heldAtDown[vk] && !winCombo) {
                winCombo = true;
                if (winOpens && shown) { HideBox(); SetForegroundWindow(winPrev); }
            }
        }
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) return;
        winPoll.Stop();
        if (winCombo) return;
        if (!winOpens) HideBox();
        else if (shown) fade.Start();
        else Index.Log("winkey: lost focus before key-up (from " + ProcName(winPrev) + ")");
    }

    static string ProcName(IntPtr hwnd) {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    static void Trim() {
        GC.Collect();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    // Start hidden: the form only appears via ShowBox (Application.Run's initial Show is swallowed)
    protected override void SetVisibleCore(bool value) {
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(value && shown);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312) {  // WM_HOTKEY
            int id = m.WParam.ToInt32();
            if (id == HK_LAUNCH) Toggle();
            else if (id == HK_LWIN || id == HK_RWIN) OnWinDown();
            else if (id == HK_FILES) Start("explorer.exe", null);
            return;
        }
        base.WndProc(ref m);
    }

    public void Toggle() { if (shown) HideBox(); else ShowBox(); }

    // Must be instant: anything typed before the box has focus lands in the previous window.
    // reveal = false takes focus but stays transparent until the caller starts the fade.
    void ShowBox(bool reveal = true) {
        search.Box.Text = "";
        Refresh_();
        var scr = Screen.FromPoint(Cursor.Position).WorkingArea;
        Left = scr.Left + (scr.Width - Width) / 2;
        Top = scr.Top + scr.Height / 5;
        shown = true;
        Opacity = 0;
        Show();
        ForceForeground();
        search.Box.Focus();
        if (reveal) fade.Start();
    }

    // The hook path doesn't grant foreground rights the way a hotkey does, so borrow the
    // foreground thread's input state for the switch.
    void ForceForeground() {
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        if (fg != IntPtr.Zero && fgThread != me) {
            AttachThreadInput(me, fgThread, true);
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
            AttachThreadInput(me, fgThread, false);
        } else {
            SetForegroundWindow(Handle);
        }
        Activate();
    }

    void HideBox() {
        if (!shown) return;
        shown = false;
        fade.Stop();
        Hide();
        results.ClearIcons();
        // refresh the app index off the UI thread so the next ShowBox never waits on the disk
        if ((DateTime.Now - lastIndex).TotalSeconds > 60) {
            lastIndex = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate { try { Index.Rebuild(); } catch (Exception ex) { Index.Log("index: " + ex.Message); } });
        }
    }

    void Refresh_() {
        results.Items = Index.Search(search.Box.Text, MaxResults);
        results.Selected = 0;
        results.EmptyText = Index.History.Count == 0 ? "Start typing to search" : "";
        footer.Status = search.Box.Text.Trim().Length == 0 ? (results.Items.Count > 0 ? "Frequent" : "")
                     : Enumerable.Count(results.Items, r => r.Kind == "App" || r.Kind == "System") + " results";
        var h = Padding.Vertical + search.Height + results.ContentHeight + footer.Height;
        if (Height != h) Height = h;
        results.Invalidate();
        footer.Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        int n = results.Items.Count;
        if (e.KeyCode == Keys.Escape) { HideBox(); e.Handled = true; }
        else if (e.KeyCode == Keys.Down || (e.KeyCode == Keys.Tab && !e.Shift)) {
            if (n > 0) { results.Selected = (results.Selected + 1) % n; results.Invalidate(); }
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Up || (e.KeyCode == Keys.Tab && e.Shift)) {
            if (n > 0) { results.Selected = (results.Selected - 1 + n) % n; results.Invalidate(); }
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Back && e.Control) {
            // TextBox inserts a box glyph for Ctrl+Backspace; delete the previous word instead
            var t = search.Box.Text.Substring(0, search.Box.SelectionStart).TrimEnd();
            int cut = t.LastIndexOf(' ') + 1;
            search.Box.Text = t.Substring(0, cut) + search.Box.Text.Substring(search.Box.SelectionStart);
            search.Box.SelectionStart = cut;
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter) {
            e.Handled = e.SuppressKeyPress = true;
            if (n > 0) Launch(results.Selected, e.Control && e.Shift);
        }
        base.OnKeyDown(e);
    }

    void Launch(int i, bool admin) {
        if (i < 0 || i >= results.Items.Count) return;
        Run(results.Items[i], admin);
    }

    public void Run(Entry r, bool admin) {
        if (r.Target.Length == 0) return;
        Index.Bump(r);
        HideBox();
        try {
            if (r.Kind == "Shell") { StartTerminal(Index.PowerShell, "-NoLogo -NoExit -Command " + r.Target, admin); return; }
            if (r.Kind == "Run") {
                try { Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(r.Target)) { UseShellExecute = true, Verb = admin ? "runas" : "" }); }
                catch { StartTerminal(Index.PowerShell, "-NoLogo -NoExit -Command " + r.Target, admin); }
                return;
            }
            switch (r.Target) {
                case "!lock": LockWorkStation(); return;
                case "!logoff": Start("shutdown.exe", "/l"); return;
                case "!restart": Start("shutdown.exe", "/r /t 0"); return;
                case "!shutdown": Start("shutdown.exe", "/s /t 0"); return;
                case "!sleep": SetSuspendState(false, false, false); return;
                case "!quit": Application.Exit(); return;
                case "!wifi": StartTerminal(Index.PowerShell, "-NoLogo -NoProfile -File \"" + Path.Combine(Index.AppDir, "wifi.ps1") + "\"", false); return;
                case "!terminal": StartTerminal(Index.PowerShell, "-NoLogo", admin); return;
                case "!intl": Start("control.exe", "intl.cpl"); return;
                case "!theme":
                    StartTerminal(Index.PowerShell, "-NoLogo -NoProfile -File \"" + Path.Combine(Index.AppDir, "set-theme.ps1") + "\"", false);
                    return;
            }
            var psi = new ProcessStartInfo(r.Target) { UseShellExecute = true };
            if (admin) psi.Verb = "runas";
            Process.Start(psi);
        } catch (Exception ex) { MessageBox.Show(ex.Message, "minilaunch"); }
    }

    static void Start(string file, string args, bool admin = false) {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true, Verb = admin ? "runas" : "" }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "minilaunch"); }
    }

    static void StartTerminal(string exe, string args, bool admin) {
        try {
            var psi = Index.InTerminal(exe, args);
            if (admin) psi.Verb = "runas";
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Process.Start(psi);
        } catch (Exception ex) { MessageBox.Show(ex.Message, "minilaunch"); }
    }
}

// Procedural cyberpunk wallpaper (or a user-supplied wallpaper.jpg/png next to the exe).
static class Wallpaper {
    static readonly Color Cyan = Color.FromArgb(0, 240, 255), Pink = Color.FromArgb(255, 46, 160);

    public static Bitmap Make(int W, int H) {
        // the theme's own image first, then a wallpaper.* dropped next to the exe, then the procedural one
        foreach (var dir in new[] { Theme.Dir, Path.GetDirectoryName(Application.ExecutablePath) })
            foreach (var ext in new[] { "jpg", "jpeg", "png", "bmp" }) {
                if (dir.Length == 0) continue;
                var p = Path.Combine(dir, "wallpaper." + ext);
                if (!File.Exists(p)) continue;
                try { return Cover(p, W, H); } catch (Exception ex) { Index.Log("wallpaper: " + ex.Message); }
            }
        return Render(W, H);
    }

    static Bitmap Cover(string path, int W, int H) {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var src = Image.FromFile(path))
        using (var g = Graphics.FromImage(bmp)) {
            float k = Math.Max((float)W / src.Width, (float)H / src.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, (W - src.Width * k) / 2, (H - src.Height * k) / 2, src.Width * k, src.Height * k);
        }
        return bmp;
    }

    public static Bitmap Render(int W, int H) {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        var rnd = new Random(2077);
        float s = H / 800f, hz = H * 0.64f, cx = W * 0.5f;
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // sky
            g.Clear(Color.Black);
            using (var b = new LinearGradientBrush(new RectangleF(-1, -1, W + 2, hz + 2), Color.Black, Color.Black, 90f)) {
                var blend = new ColorBlend();
                blend.Colors = new[] { Color.FromArgb(5, 3, 18), Color.FromArgb(24, 8, 52), Color.FromArgb(84, 14, 90), Color.FromArgb(170, 32, 112) };
                blend.Positions = new[] { 0f, 0.45f, 0.86f, 1f };
                b.InterpolationColors = blend;
                g.FillRectangle(b, -1, -1, W + 2, hz + 2);   // overscan: AA would leave a light seam at the edges
            }
            for (int i = 0; i < 170; i++) {
                float x = (float)rnd.NextDouble() * W, y = (float)(rnd.NextDouble() * rnd.NextDouble()) * hz * 0.8f;
                float r = (float)(rnd.NextDouble() * 1.4 + 0.5) * s;
                using (var b = new SolidBrush(Color.FromArgb(rnd.Next(60, 220), rnd.Next(2) == 0 ? Color.White : Color.FromArgb(150, 230, 255))))
                    g.FillEllipse(b, x, y, r, r);
            }

            // striped sun with glow
            float sr = H * 0.2f, scy = hz - H * 0.14f;
            for (int i = 14; i >= 1; i--) {
                float rr = sr * (1 + i * 0.09f);
                using (var b = new SolidBrush(Color.FromArgb(7, 255, 60, 160))) g.FillEllipse(b, cx - rr, scy - rr, rr * 2, rr * 2);
            }
            using (var path = new GraphicsPath()) {
                path.AddEllipse(cx - sr, scy - sr, sr * 2, sr * 2);
                using (var reg = new Region(path)) {
                    float y = scy + sr * 0.05f, gap = 2 * s, band = 9 * s;
                    while (y < scy + sr) {
                        reg.Exclude(new RectangleF(cx - sr, y, sr * 2, gap));
                        y += gap + band; gap += 1.7f * s; band = Math.Max(2 * s, band - 1.1f * s);
                    }
                    using (var b = new LinearGradientBrush(new RectangleF(cx - sr, scy - sr, sr * 2, sr * 2 + 1),
                                                           Color.FromArgb(255, 226, 90), Color.FromArgb(255, 40, 150), 90f))
                        g.FillRegion(b, reg);
                }
            }

            Skyline(g, rnd, W, H, hz, s, false);
            // haze where the far city meets the horizon
            using (var b = new LinearGradientBrush(new RectangleF(0, hz - H * 0.13f, W, H * 0.13f + 1),
                                                   Color.FromArgb(0, 255, 50, 150), Color.FromArgb(120, 255, 50, 150), 90f))
                g.FillRectangle(b, 0, hz - H * 0.13f, W, H * 0.13f);
            Skyline(g, rnd, W, H, hz, s, true);

            // ground, sun reflection, perspective grid
            using (var b = new LinearGradientBrush(new RectangleF(0, hz, W, H - hz + 1), Color.FromArgb(28, 5, 42), Color.FromArgb(4, 1, 10), 90f))
                g.FillRectangle(b, 0, hz, W, H - hz + 1);
            using (var b = new LinearGradientBrush(new RectangleF(0, hz, W, H * 0.3f), Color.FromArgb(70, 255, 70, 170), Color.FromArgb(0, 255, 60, 160), 90f))
                g.FillEllipse(b, cx - sr * 0.55f, hz - H * 0.01f, sr * 1.1f, H * 0.3f);
            var grid = Color.FromArgb(255, 40, 200);
            using (var vb = new LinearGradientBrush(new RectangleF(0, hz, W, H - hz + 1), Color.FromArgb(10, grid), Color.FromArgb(210, grid), 90f))
            using (var vglow = new LinearGradientBrush(new RectangleF(0, hz, W, H - hz + 1), Color.FromArgb(0, grid), Color.FromArgb(50, grid), 90f))
            using (var vp = new Pen(vb, 1.3f * s))
            using (var vg = new Pen(vglow, 5 * s))
                for (int i = -26; i <= 26; i++) {
                    float xt = cx + i * W * 0.004f, xb = cx + i * W * 0.075f;
                    g.DrawLine(vg, xt, hz, xb, H);
                    g.DrawLine(vp, xt, hz, xb, H);
                }
            for (int k = 1; k <= 14; k++) {
                float t = k / 14f, y = hz + (H - hz) * t * t;
                int a = (int)(30 + 190 * t);
                using (var p = new Pen(Color.FromArgb(a / 4, grid), 5 * s)) g.DrawLine(p, 0, y, W, y);
                using (var p = new Pen(Color.FromArgb(a, grid), 1.3f * s)) g.DrawLine(p, 0, y, W, y);
            }
            Neon(g, Pink, 0, hz, W, hz, 2 * s);

            // rain
            for (int i = 0; i < 450; i++) {
                float x = (float)rnd.NextDouble() * W, y = (float)rnd.NextDouble() * H, len = (8 + (float)rnd.NextDouble() * 24) * s;
                using (var p = new Pen(Color.FromArgb(rnd.Next(16, 46), 150, 220, 255), 1f)) g.DrawLine(p, x, y, x - len * 0.18f, y + len);
            }

            // scanlines + vignette
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
                for (int y = 0; y < H; y += 3) g.FillRectangle(b, 0, y, W, 1);
            using (var path = new GraphicsPath()) {
                path.AddEllipse(-W * 0.25f, -H * 0.3f, W * 1.5f, H * 1.6f);
                using (var b = new PathGradientBrush(path)) {
                    b.CenterColor = Color.FromArgb(0, 0, 0, 0);
                    b.SurroundColors = new[] { Color.FromArgb(190, 0, 0, 0) };
                    b.FocusScales = new PointF(0.55f, 0.5f);
                    g.FillRectangle(b, 0, 0, W, H);
                }
            }
        }
        return bmp;
    }

    static void Skyline(Graphics g, Random rnd, int W, int H, float hz, float s, bool near) {
        var body = near ? Color.FromArgb(9, 4, 18) : Color.FromArgb(40, 12, 68);
        Color[] lights = { Cyan, Pink, Color.FromArgb(255, 190, 70), Color.FromArgb(170, 200, 255) };
        float x = -20 * s;
        while (x < W) {
            float w = (near ? rnd.Next(50, 130) : rnd.Next(26, 80)) * s;
            float h = (float)(near ? 0.12 + rnd.NextDouble() * 0.32 : 0.08 + rnd.NextDouble() * 0.27) * H;
            // keep the middle low so the sun shows through
            float d = Math.Min(1f, Math.Abs(x + w / 2 - W / 2f) / (W * (near ? 0.3f : 0.2f)));
            h *= near ? 0.15f + 0.85f * d : 0.3f + 0.7f * d;
            using (var b = new SolidBrush(body)) {
                g.FillRectangle(b, x, hz - h, w, h + 1);
                if (rnd.Next(3) == 0) {
                    float aw = w * 0.4f, ah = h * 0.12f;
                    g.FillRectangle(b, x + (w - aw) / 2, hz - h - ah, aw, ah + 1);
                    if (rnd.Next(2) == 0)
                        using (var p = new Pen(body, 2 * s)) g.DrawLine(p, x + w / 2, hz - h - ah, x + w / 2, hz - h - ah - h * 0.15f);
                }
            }
            float step = (near ? 7 : 5) * s, size = (near ? 2.5f : 1.6f) * s;
            double lit = near ? 0.16 : 0.07;
            for (float wy = hz - h + step; wy < hz - step; wy += step)
                for (float wx = x + step * 0.8f; wx < x + w - step * 0.5f; wx += step)
                    if (rnd.NextDouble() < lit)
                        using (var b = new SolidBrush(Color.FromArgb(near ? 200 : 110, lights[rnd.Next(lights.Length)])))
                            g.FillRectangle(b, wx, wy, size, size * 1.4f);
            if (near && h > 0.18f * H && rnd.Next(3) == 0) {
                float sx = x + w * (0.2f + (float)rnd.NextDouble() * 0.6f), sy = hz - h * 0.85f;
                Neon(g, rnd.Next(2) == 0 ? Pink : Cyan, sx, sy, sx, sy + h * (0.2f + (float)rnd.NextDouble() * 0.3f), 4 * s);
            }
            x += w + (near ? rnd.Next(0, 18) : rnd.Next(-6, 6)) * s;
        }
    }

    public static void Neon(Graphics g, Color c, float x1, float y1, float x2, float y2, float width) {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 5; i >= 1; i--)
            using (var p = new Pen(Color.FromArgb(18, c), width + i * 5)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, x1, y1, x2, y2); }
        using (var p = new Pen(c, width)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, x1, y1, x2, y2); }
        using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), width * 0.35f)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawLine(p, x1, y1, x2, y2); }
        g.SmoothingMode = mode;
    }
}

class Stats {
    public double MemUsedGB, MemTotalGB;
    public int Cpu, Battery = -1;
    public bool Charging, Online;
    public string Vpn = "";
}

// Bottom-left status readout: a dark glass panel over the wallpaper so the text stays legible on any image.
static class Hud {
    static readonly System.Drawing.Text.TextRenderingHint Crisp = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

    static RectangleF Panel(Size sz, float s) {
        float w = 520 * s, h = 236 * s;
        return new RectangleF(36 * s, sz.Height - 36 * s - h, w, h);
    }

    public static Rectangle Area(Size sz, float s) {
        var p = Panel(sz, s);
        p.Inflate(30 * s, 30 * s);   // neon glow spills past the panel edge
        return Rectangle.Round(p);
    }

    public static void Draw(Graphics g, Size sz, float s, DateTime now, Stats st) {
        var p = Panel(sz, s);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(p, 10 * s)) {
            using (var b = new SolidBrush(Color.FromArgb(200, Theme.Ink))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(90, Theme.Primary), 1f)) g.DrawPath(pen, path);
        }
        Wallpaper.Neon(g, Theme.Secondary, p.X + 1.5f * s, p.Y + 18 * s, p.X + 1.5f * s, p.Bottom - 18 * s, 2.5f * s);

        float x = p.X + 26 * s, y = p.Y;
        using (var head = new Font("Bahnschrift SemiBold", 12 * s, GraphicsUnit.Pixel))
        using (var clock = new Font("Bahnschrift SemiLight", 84 * s, GraphicsUnit.Pixel))
        using (var date = new Font("Bahnschrift", 19 * s, GraphicsUnit.Pixel))
        using (var label = new Font("Bahnschrift SemiBold", 13 * s, GraphicsUnit.Pixel))
        using (var mono = new Font("Consolas", 14 * s, GraphicsUnit.Pixel))
        using (var hint = new Font("Bahnschrift", 12 * s, GraphicsUnit.Pixel)) {
            g.TextRenderingHint = Crisp;
            Text(g, Theme.Expand(Theme.Header), head, Theme.Secondary, x, y + 16 * s);

            // large type: grayscale AA keeps the curves smooth; ClearType fringes at this size
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Text(g, now.ToString("HH:mm"), clock, Theme.HudText, x - 6 * s, y + 26 * s);
            g.TextRenderingHint = Crisp;
            Text(g, now.ToString("dddd, d MMMM yyyy"), date, Theme.Primary, x, y + 124 * s);

            float my = y + 160 * s, gx = x;
            double mem = st.MemTotalGB > 0 ? st.MemUsedGB / st.MemTotalGB : 0;
            gx = Meter(g, label, mono, s, gx, my, Theme.LabelMem, mem, mem > 0.85, st.MemUsedGB.ToString("0.00") + " G");
            gx = Meter(g, label, mono, s, gx, my, Theme.LabelCpu, st.Cpu / 100.0, st.Cpu > 85, st.Cpu + "%");
            if (st.Battery >= 0)
                Meter(g, label, mono, s, gx, my, Theme.LabelPwr, st.Battery / 100.0, st.Battery < 20 && !st.Charging, st.Battery + "%" + (st.Charging ? "+" : ""));

            float ny = my + 24 * s;
            Text(g, Theme.LabelNet, label, Theme.HudDim, x, ny);
            Text(g, st.Online ? "ONLINE" : "OFFLINE", mono, st.Online ? Theme.Primary : Theme.Secondary, x + 40 * s, ny - 1 * s);
            if (st.Vpn.Length > 0) {
                Text(g, Theme.LabelVpn, label, Theme.HudDim, x + 132 * s, ny);
                Text(g, st.Vpn, mono, Theme.Primary, x + 178 * s, ny - 1 * s);
            }

            Text(g, "Tap Win to search   ·   right-click for menu", hint, Theme.HudFaint, x, y + 208 * s);
        }
    }

    static float Meter(Graphics g, Font labelFont, Font valueFont, float s, float x, float y, string label, double frac, bool warn, string value) {
        frac = Math.Max(0, Math.Min(1, frac));
        Text(g, label, labelFont, Theme.HudDim, x, y);
        float bx = x + 40 * s, by = y + 7 * s, bw = 64 * s, bh = 4 * s;
        using (var b = new SolidBrush(Color.FromArgb(70, 120, 150, 190))) g.FillRectangle(b, bx, by, bw, bh);
        var c = warn ? Theme.Secondary : Theme.Primary;
        using (var b = new SolidBrush(Color.FromArgb(60, c))) g.FillRectangle(b, bx, by - 2 * s, (float)(bw * frac), bh + 4 * s);
        using (var b = new SolidBrush(c)) g.FillRectangle(b, bx, by, (float)(bw * frac), bh);
        Text(g, value, valueFont, Theme.HudText, bx + bw + 8 * s, y - 1 * s);
        var vw = g.MeasureString(value, valueFont).Width;
        return bx + bw + 8 * s + vw + 16 * s;
    }

    static void Text(Graphics g, string t, Font f, Color c, float x, float y) {
        using (var b = new SolidBrush(c)) g.DrawString(t, f, b, x, y);
    }
}

class DarkMenuColors : ProfessionalColorTable {
    static Color Sel { get { return Theme.Mix(Theme.Ink, Theme.Primary, 0.3f); } }
    static Color Sep { get { return Theme.Mix(Theme.Ink, Theme.Secondary, 0.35f); } }
    public override Color ToolStripDropDownBackground { get { return Theme.Ink; } }
    public override Color ImageMarginGradientBegin { get { return Theme.Ink; } }
    public override Color ImageMarginGradientMiddle { get { return Theme.Ink; } }
    public override Color ImageMarginGradientEnd { get { return Theme.Ink; } }
    public override Color MenuBorder { get { return Theme.Secondary; } }
    public override Color MenuItemBorder { get { return Theme.Primary; } }
    public override Color MenuItemSelected { get { return Sel; } }
    public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
    public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
    public override Color SeparatorDark { get { return Sep; } }
    public override Color SeparatorLight { get { return Sep; } }
}

// Full-screen window pinned under everything else: wallpaper, HUD, double-click/right-click entry points.
class DesktopForm : Form {
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    class MemStatus { public uint Length = 64, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);

    readonly LauncherForm launcher;
    readonly Stats st = new Stats();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 2000 };
    Bitmap wall;
    float s;
    long prevIdle, prevTotal;
    int netCountdown;
    // network adapters whose address the HUD shows on its VPN row, e.g. "Tailscale" or "NordLynx"
    readonly string[] vpnNames = Index.Setting("VpnInterface", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToArray();

    public DesktopForm(LauncherForm l) {
        launcher = l;
        Text = "minilaunch desktop";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        ContextMenuStrip = BuildMenu();
        Fit();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { Fit(); Invalidate(); };
        tick.Tick += delegate { UpdateStats(); Invalidate(Hud.Area(ClientSize, s)); };
        UpdateStats();
        tick.Start();
    }

    void Fit() {
        ShellSettings.Apply();   // display changes reset the work area
        Bounds = Screen.PrimaryScreen.Bounds;
        s = Bounds.Height / 800f;
        var old = wall;
        wall = Wallpaper.Make(Bounds.Width, Bounds.Height);
        if (old != null) old.Dispose();
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000;   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: no Alt+Tab, never steals focus
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x46) {   // WM_WINDOWPOSCHANGING: always stay at the bottom of the z-order
            Marshal.WriteIntPtr(m.LParam, IntPtr.Size, (IntPtr)1);                  // hwndInsertAfter = HWND_BOTTOM
            int flagsAt = IntPtr.Size * 2 + 16;
            Marshal.WriteInt32(m.LParam, flagsAt, Marshal.ReadInt32(m.LParam, flagsAt) & ~0x4);  // clear SWP_NOZORDER
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) {
        var clip = e.ClipRectangle;
        e.Graphics.DrawImage(wall, clip, clip, GraphicsUnit.Pixel);
        if (clip.IntersectsWith(Hud.Area(ClientSize, s))) Hud.Draw(e.Graphics, ClientSize, s, DateTime.Now, st);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) launcher.Toggle();
    }

    void UpdateStats() {
        var m = new MemStatus();
        if (GlobalMemoryStatusEx(m)) { st.MemTotalGB = m.Total / 1073741824.0; st.MemUsedGB = (m.Total - m.Avail) / 1073741824.0; }
        long idle, kern, user;
        if (GetSystemTimes(out idle, out kern, out user)) {
            long total = kern + user;   // kernel time includes idle
            if (prevTotal > 0 && total > prevTotal) st.Cpu = (int)Math.Round(100.0 * (1 - (double)(idle - prevIdle) / (total - prevTotal)));
            prevIdle = idle; prevTotal = total;
        }
        var ps = SystemInformation.PowerStatus;
        st.Battery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 ? -1 : (int)Math.Round(ps.BatteryLifePercent * 100);
        st.Charging = ps.PowerLineStatus == PowerLineStatus.Online;
        if (netCountdown-- <= 0) {
            netCountdown = 5;
            st.Online = NetworkInterface.GetIsNetworkAvailable();
            st.Vpn = "";
            if (vpnNames.Length > 0) try {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (vpnNames.Contains(ni.Name, StringComparer.OrdinalIgnoreCase) && ni.OperationalStatus == OperationalStatus.Up)
                        foreach (var a in ni.GetIPProperties().UnicastAddresses)
                            if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) st.Vpn = a.Address.ToString();
            } catch { }
        }
    }

    ContextMenuStrip BuildMenu() {
        var menu = new ContextMenuStrip {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            ShowImageMargin = false,
            Font = new Font("Consolas", 10f),
            ForeColor = Theme.HudText,
        };
        var search = new ToolStripMenuItem("Search apps") { ShortcutKeyDisplayString = "Win" };
        search.Click += delegate { launcher.Toggle(); };
        menu.Items.Add(search);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var t in new[] { "!terminal", "explorer.exe", "control.exe", "!wifi", "taskmgr.exe", "-", "!lock", "!sleep", "!restart", "!shutdown" }) {
            if (t == "-") { menu.Items.Add(new ToolStripSeparator()); continue; }
            var entry = Index.All.FirstOrDefault(x => x.Target == t);   // index is built before the desktop
            if (entry == null) continue;
            var item = new ToolStripMenuItem(entry.Name);
            item.Click += delegate { launcher.Run(entry, false); };
            menu.Items.Add(item);
        }
        return menu;
    }
}

// System-wide settings explorer would normally own.
static class ShellSettings {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MINIMIZEDMETRICS { public int Size, Width, HorzGap, VertGap, Arrange; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiRect(uint action, uint param, ref RECT r, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SpiMin(uint action, uint param, ref MINIMIZEDMETRICS m, uint flags);
    [DllImport("user32.dll")] static extern bool SetTaskmanWindow(IntPtr h);
    const uint SPI_SETWORKAREA = 0x2F, SPI_GETMINIMIZEDMETRICS = 0x2B, SPI_SETMINIMIZEDMETRICS = 0x2C, SPIF_SENDCHANGE = 2;
    const int ARW_HIDE = 8;

    // Minimized windows: with ARW_HIDE Windows parks them at -32000 instead of drawing title-bar stubs in
    // the corner, but it only honours the flag once some window is registered as the taskman window
    // (explorer's taskbar). Being done by Windows itself, this also covers elevated windows.
    public static void HideMinimized(IntPtr taskman) {
        if (!SetTaskmanWindow(taskman)) Index.Log("SetTaskmanWindow failed");
        var m = new MINIMIZEDMETRICS { Size = Marshal.SizeOf(typeof(MINIMIZEDMETRICS)) };
        if (SpiMin(SPI_GETMINIMIZEDMETRICS, (uint)m.Size, ref m, 0) && (m.Arrange & ARW_HIDE) == 0) {
            m.Arrange |= ARW_HIDE;
            SpiMin(SPI_SETMINIMIZEDMETRICS, (uint)m.Size, ref m, 0);
        }
    }

    // No taskbar: maximized windows get the whole screen (a stale explorer reservation otherwise lingers).
    public static void Apply() {
        var b = Screen.PrimaryScreen.Bounds;
        var r = new RECT { L = b.Left, T = b.Top, R = b.Right, B = b.Bottom };
        SpiRect(SPI_SETWORKAREA, 0, ref r, SPIF_SENDCHANGE);
    }
}

class WinItem {
    public IntPtr Hwnd;
    public string Title = "", Proc = "";
    public Bitmap Icon;
}

// Right-edge dock for minimized windows. Per-pixel-alpha layered window: fully transparent pixels
// pass clicks through, so the card area costs nothing when it isn't drawn.
class Dock : Form {
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")] static extern IntPtr GetClassLongPtr64(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "GetClassLong")] static extern uint GetClassLong32(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int Length, Flags, ShowCmd; public PT MinPos, MaxPos; public RECT Normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder sb, ref int size);
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref PT pos, ref SZ size, IntPtr src, ref PT srcPos, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    static readonly string[] BtnText = { "Restore", "Maximize", "Close" };
    static readonly string[] BtnGlyph = { "", "", "" };

    readonly float s;
    readonly int tile, pad, step, pillW, margin, cardW, cardH, gap;
    readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
    readonly WinEventProc winEvent;   // kept in a field so the GC doesn't collect the delegate
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1000 };
    readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 120 };
    readonly System.Windows.Forms.Timer leave = new System.Windows.Forms.Timer { Interval = 300 };
    readonly Font titleFont, subFont, btnFont, glyphFont;
    public List<WinItem> Wins = new List<WinItem>();
    public TrayHost Tray;                                   // tray icons follow the windows, below a divider
    List<TrayIcon> trayShown = new List<TrayIcon>();
    int Count { get { return Wins.Count + trayShown.Count; } }
    bool IsTray(int i) { return i >= Wins.Count; }
    int SepH { get { return Wins.Count > 0 && trayShown.Count > 0 ? (int)(12 * s) : 0; } }
    public int Hover = -1, HoverBtn = -1;
    public bool Expanded;
    public float Slide = 1;   // 0 = pill fully out, 1 = tucked away
    bool mouseInside;
    IntPtr pendingClose;
    int pendingCloseAt;

    public Dock(bool live) {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
        tile = (int)(44 * s); pad = (int)(8 * s); step = tile + (int)(4 * s);
        pillW = tile + pad * 2; margin = (int)(10 * s); cardW = (int)(290 * s); cardH = (int)(96 * s); gap = (int)(12 * s);
        titleFont = new Font("Segoe UI", 13 * s, GraphicsUnit.Pixel);
        subFont = new Font("Consolas", 11 * s, GraphicsUnit.Pixel);
        btnFont = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel);
        glyphFont = new Font("Segoe MDL2 Assets", 10 * s, GraphicsUnit.Pixel);

        Text = "minilaunch dock";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        var scr = Screen.PrimaryScreen.Bounds;
        Bounds = new Rectangle(scr.Right - (cardW + gap + pillW + margin + (int)(24 * s)), scr.Top,
                               cardW + gap + pillW + margin + (int)(24 * s), scr.Height);
        if (!live) return;

        anim.Tick += delegate {
            float target = Expanded ? 0 : 1;
            Slide += Math.Sign(target - Slide) * 0.25f;
            if (Math.Abs(Slide - target) < 0.01f) { Slide = target; anim.Stop(); }
            Render();
        };
        poll.Tick += delegate { RefreshList(); };          // catches closes/title changes while anything is docked
        settle.Tick += delegate { settle.Stop(); RefreshList(); };
        leave.Tick += delegate { leave.Stop(); Hover = HoverBtn = -1; UpdateExpanded(); };
        winEvent = OnWinEvent;
        SetWinEventHook(0x16, 0x17, IntPtr.Zero, winEvent, 0, 0, 0);   // EVENT_SYSTEM_MINIMIZESTART..END
        SetWinEventHook(0x03, 0x03, IntPtr.Zero, winEvent, 0, 0, 0);   // EVENT_SYSTEM_FOREGROUND
        CreateHandle();
        ShellSettings.HideMinimized(Handle);
        RefreshList();
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8;   // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) {
        if (idObject != 0 || idChild != 0) return;
        if (ev == 0x03) UpdateExpanded();
        else { settle.Stop(); settle.Start(); }   // let the minimize finish before reading window state
    }

    // ---- window list ----

    static string TitleOf(IntPtr h) {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    bool IsDockable(IntPtr h) {
        if (!IsWindowVisible(h) || !IsIconic(h)) return false;
        if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        if ((GetWindowLong(h, -20) & 0x80) != 0) return false;   // tool windows
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid != ownPid && TitleOf(h).Length > 0;
    }

    // Windows minimized before ShellSettings.HideMinimized ran are still on screen as title-bar stubs
    // (Windows even clamps an off-screen minimized position back to the corner). So park such a stub in
    // the corner and sink it below the full-screen desktop window, which hides it.
    static void HideStub(IntPtr h) {
        RECT r;
        if (GetWindowRect(h, out r) && r.L <= -32000) return;   // already parked off-screen by ARW_HIDE
        var wp = new WINDOWPLACEMENT { Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
        if (GetWindowPlacement(h, ref wp) && (wp.MinPos.X != -1 || wp.MinPos.Y != -1)) {
            wp.Flags = 1;   // WPF_SETMINPOSITION
            wp.MinPos = new PT { X = -1, Y = -1 };
            SetWindowPlacement(h, ref wp);
        }
        // HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE. Fails silently for elevated windows (UIPI).
        SetWindowPos(h, (IntPtr)1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    // ShowWindow is silently dropped for elevated windows (UIPI), leaving a minimized one where it was.
    // A posted WM_SYSCOMMAND does get through, and the window restores itself.
    internal static void Unminimize(IntPtr h, bool maximize) {
        ShowWindow(h, maximize ? 3 /* SW_MAXIMIZE */ : 9 /* SW_RESTORE */);
        if (IsIconic(h))
            PostMessage(h, 0x112 /* WM_SYSCOMMAND */, (IntPtr)(maximize ? 0xF030 /* SC_MAXIMIZE */ : 0xF120 /* SC_RESTORE */), IntPtr.Zero);
    }

    static string ProcPath(uint pid) {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try {
            var sb = new System.Text.StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "";
        } finally { CloseHandle(h); }
    }

    WinItem Describe(IntPtr h) {
        var w = new WinItem { Hwnd = h, Title = TitleOf(h) };
        uint pid; GetWindowThreadProcessId(h, out pid);
        var path = ProcPath(pid);
        w.Proc = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : "";
        IntPtr hi;
        if (SendMessageTimeout(h, 0x7F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 2 /* ABORTIFHUNG */, 150, out hi) != IntPtr.Zero && hi != IntPtr.Zero)
            w.Icon = Icons.FromBorrowed(hi);
        if (w.Icon == null && path.Length > 0) w.Icon = Icons.ForPath(path);
        if (w.Icon == null) {
            var ci = IntPtr.Size == 8 ? GetClassLongPtr64(h, -14) : (IntPtr)GetClassLong32(h, -14);   // GCLP_HICON
            if (ci != IntPtr.Zero) w.Icon = Icons.FromBorrowed(ci);
        }
        return w;
    }

    internal void RefreshList() {
        // a Close that didn't finish (e.g. a "save changes?" prompt): bring the window back so the prompt is visible
        if (pendingClose != IntPtr.Zero && Environment.TickCount - pendingCloseAt > 900) {
            var pc = pendingClose; pendingClose = IntPtr.Zero;
            if (IsWindow(pc) && IsIconic(pc)) { Unminimize(pc, false); SetForegroundWindow(pc); }
        }
        var found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) { if (IsDockable(h)) found.Add(h); return true; }, IntPtr.Zero);
        var next = new List<WinItem>();
        foreach (var w in Wins) {
            if (found.Contains(w.Hwnd)) { w.Title = TitleOf(w.Hwnd); next.Add(w); }
            else if (w.Icon != null) w.Icon.Dispose();
        }
        foreach (var h in found) if (!next.Any(w => w.Hwnd == h)) next.Add(Describe(h));
        foreach (var w in next) HideStub(w.Hwnd);
        Wins = next;
        trayShown = Tray == null ? new List<TrayIcon>() : Tray.Shown;
        if (Hover >= Count) Hover = HoverBtn = -1;

        if (Count == 0) {
            poll.Stop();
            if (Visible) Hide();
            Expanded = false; Slide = 1; mouseInside = false;
            return;
        }
        poll.Start();
        if (!Visible) { Slide = 1; Expanded = false; Show(); }
        UpdateExpanded();
    }

    // ---- expand / collapse ----

    bool ForegroundCovers() {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsIconic(fg)) return false;
        uint pid; GetWindowThreadProcessId(fg, out pid);
        if (pid == ownPid) return false;
        RECT r;
        if (!GetWindowRect(fg, out r)) return false;
        var pill = PillRect(0);
        pill.Offset(Left, Top);
        return new Rectangle(r.L, r.T, r.R - r.L, r.B - r.T).IntersectsWith(pill);
    }

    void UpdateExpanded() {
        if (Count == 0) return;
        bool want = mouseInside || !ForegroundCovers();
        if (want != Expanded) { Expanded = want; anim.Start(); }
        else Render();
    }

    // ---- layout ----

    int PillH { get { int n = Math.Max(1, Count); return pad * 2 + n * tile + (n - 1) * (step - tile) + SepH; } }

    Rectangle PillRect(float slide) {
        int x = Width - margin - pillW + (int)(slide * (pillW + margin + 8 * s));
        return new Rectangle(x, (Height - PillH) / 2, pillW, PillH);
    }

    Rectangle TileRect(int i, Rectangle pill) { return new Rectangle(pill.X + pad, pill.Y + pad + i * step + (IsTray(i) ? SepH : 0), tile, tile); }

    Rectangle CardRect(int i, Rectangle pill) {
        var t = TileRect(i, pill);
        int y = Math.Max((int)(8 * s), Math.Min(Height - cardH - (int)(8 * s), t.Y + t.Height / 2 - cardH / 2));
        return new Rectangle(pill.X - gap - cardW, y, cardW, cardH);
    }

    Rectangle[] Buttons(Rectangle card) {
        int m = (int)(12 * s), sp = (int)(8 * s), bh = (int)(30 * s);
        int bw = (card.Width - m * 2 - sp * 2) / 3, y = card.Bottom - m - bh;
        return new[] {
            new Rectangle(card.X + m, y, bw, bh),
            new Rectangle(card.X + m + bw + sp, y, bw, bh),
            new Rectangle(card.X + m + (bw + sp) * 2, y, bw, bh)
        };
    }

    // the strip between card and tile, so the mouse can travel across without "leaving"
    Rectangle Bridge(Rectangle card, Rectangle tileR) {
        int top = Math.Min(card.Y, tileR.Y), bottom = Math.Max(card.Bottom, tileR.Bottom);
        return new Rectangle(card.Right - 2, top, tileR.X - card.Right + 4, bottom - top);
    }

    // ---- drawing ----

    public Bitmap Compose() {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var pill = PillRect(Slide);
            if (Slide > 0.5f) DrawEdge(g, PillRect(0));
            if (Slide < 0.99f) DrawPill(g, pill);
            if (Expanded && Slide < 0.01f && Hover >= 0 && Hover < Count) DrawCard(g, pill, Hover);
        }
        return bmp;
    }

    void DrawEdge(Graphics g, Rectangle pill) {
        // a nearly invisible hit zone along the edge makes the tucked dock easy to reach with the mouse
        using (var b = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillRectangle(b, Width - 12 * s, pill.Y, 12 * s, pill.Height);
        float x = Width - 3 * s;
        Wallpaper.Neon(g, Theme.Primary, x, pill.Y + 14 * s, x, pill.Bottom - 14 * s, 2.5f * s);
    }

    void DrawPill(Graphics g, Rectangle pill) {
        float r = 16 * s;
        for (int i = 4; i >= 1; i--) {
            var gr = RectangleF.Inflate(pill, i * 3 * s, i * 3 * s);
            using (var p = Theme.Rounded(gr, r + i * 3 * s)) using (var b = new SolidBrush(Color.FromArgb(10 + (4 - i) * 4, Theme.Secondary))) g.FillPath(b, p);
        }
        using (var p = Theme.Rounded(pill, r)) {
            using (var b = new LinearGradientBrush(pill, Color.FromArgb(240, Theme.Ink2), Color.FromArgb(240, Theme.Ink), 90f)) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(170, Theme.Secondary), 1.2f * s)) g.DrawPath(pen, p);
        }
        if (SepH > 0) {
            float y = TileRect(Wins.Count, pill).Y - SepH / 2f - (step - tile) / 2f;
            using (var pen = new Pen(Color.FromArgb(110, Theme.Secondary), 1f * s)) g.DrawLine(pen, pill.X + pad + 6 * s, y, pill.Right - pad - 6 * s, y);
        }
        for (int i = 0; i < Count; i++) {
            var t = TileRect(i, pill);
            if (i == Hover) {
                using (var p = Theme.Rounded(t, 10 * s)) {
                    using (var b = new SolidBrush(Color.FromArgb(60, Theme.Primary))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(220, Theme.Primary), 1.2f * s)) g.DrawPath(pen, p);
                }
                Wallpaper.Neon(g, Theme.Primary, pill.X + 2.5f * s, t.Y + 10 * s, pill.X + 2.5f * s, t.Bottom - 10 * s, 2 * s);
            }
            if (IsTray(i)) {   // tray icons are small-icon artwork: draw them at tray size, not stretched
                float tsz = 20 * s;
                g.DrawImage(trayShown[i - Wins.Count].Icon, t.X + (tile - tsz) / 2, t.Y + (tile - tsz) / 2, tsz, tsz);
                continue;
            }
            float isz = 30 * s;
            var icon = Wins[i].Icon;
            if (icon != null) g.DrawImage(icon, t.X + (tile - isz) / 2, t.Y + (tile - isz) / 2, isz, isz);
            else {
                var letter = Wins[i].Title.Substring(0, 1).ToUpperInvariant();
                using (var b = new SolidBrush(Theme.Primary)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(letter, titleFont, b, t, sf);
            }
        }
    }

    void DrawCard(Graphics g, Rectangle pill, int i) {
        if (IsTray(i)) { DrawTrayCard(g, pill, i); return; }
        var w = Wins[i];
        var c = CardRect(i, pill);
        using (var b = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillRectangle(b, Bridge(c, TileRect(i, pill)));
        float r = 12 * s;
        for (int k = 5; k >= 1; k--) {
            var gr = RectangleF.Inflate(c, k * 3 * s, k * 3 * s);
            using (var p = Theme.Rounded(gr, r + k * 3 * s)) using (var b = new SolidBrush(Color.FromArgb(8 + (5 - k) * 3, Theme.Secondary))) g.FillPath(b, p);
        }
        using (var p = Theme.Rounded(c, r)) {
            using (var b = new LinearGradientBrush(c, Color.FromArgb(245, Theme.Ink2), Color.FromArgb(245, Theme.Ink), 90f)) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(200, Theme.Secondary), 1.2f * s)) g.DrawPath(pen, p);
        }
        float m = 12 * s, isz = 22 * s;
        if (w.Icon != null) g.DrawImage(w.Icon, c.X + m, c.Y + m, isz, isz);
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var tb = new SolidBrush(Theme.HudText))
        using (var pb = new SolidBrush(Theme.Secondary)) {
            float tx = c.X + m + isz + 10 * s, tw = c.Right - m - tx;
            g.DrawString(w.Title, titleFont, tb, new RectangleF(tx, c.Y + m - 2 * s, tw, 18 * s), sf);
            g.DrawString((w.Proc.Length > 0 ? w.Proc : "window").ToUpperInvariant() + "  //  MINIMIZED", subFont, pb, new RectangleF(tx, c.Y + m + 17 * s, tw, 16 * s), sf);
        }
        var btns = Buttons(c);
        for (int k = 0; k < 3; k++) {
            var br = btns[k];
            var col = k == 2 ? Theme.Secondary : Theme.Primary;
            bool hot = k == HoverBtn;
            using (var p = Theme.Rounded(br, 7 * s)) {
                using (var b = new SolidBrush(hot ? col : Color.FromArgb(34, col))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(hot ? 255 : 120, col), 1f * s)) g.DrawPath(pen, p);
            }
            var fg = hot ? Theme.Ink : col;
            var label = BtnText[k];
            var gsz = g.MeasureString(BtnGlyph[k], glyphFont);
            var lsz = g.MeasureString(label, btnFont);
            float total = gsz.Width + 2 * s + lsz.Width, x0 = br.X + (br.Width - total) / 2;
            using (var b = new SolidBrush(fg)) {
                g.DrawString(BtnGlyph[k], glyphFont, b, x0, br.Y + (br.Height - gsz.Height) / 2 + 1 * s);
                g.DrawString(label, btnFont, b, x0 + gsz.Width + 2 * s, br.Y + (br.Height - lsz.Height) / 2);
            }
        }
    }

    // tray icons get a compact card: the app's tooltip, and what the clicks do
    void DrawTrayCard(Graphics g, Rectangle pill, int i) {
        var ti = trayShown[i - Wins.Count];
        var full = CardRect(i, pill);
        int h = (int)(62 * s);
        var c = new Rectangle(full.X, Math.Max((int)(8 * s), Math.Min(Height - h - (int)(8 * s), full.Y + (full.Height - h) / 2)), full.Width, h);
        using (var b = new SolidBrush(Color.FromArgb(2, 0, 0, 0))) g.FillRectangle(b, Bridge(c, TileRect(i, pill)));
        float r = 12 * s;
        using (var p = Theme.Rounded(c, r)) {
            using (var b = new LinearGradientBrush(c, Color.FromArgb(245, Theme.Ink2), Color.FromArgb(245, Theme.Ink), 90f)) g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb(200, Theme.Primary), 1.2f * s)) g.DrawPath(pen, p);
        }
        float m = 12 * s, isz = 20 * s;
        g.DrawImage(ti.Icon, c.X + m, c.Y + m, isz, isz);
        var tip = ti.Tip.Split('\n')[0].Trim();
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var tb = new SolidBrush(Theme.HudText))
        using (var pb = new SolidBrush(Theme.Primary)) {
            float tx = c.X + m + isz + 10 * s, tw = c.Right - m - tx;
            g.DrawString(tip.Length > 0 ? tip : ti.Proc, titleFont, tb, new RectangleF(tx, c.Y + m - 2 * s, tw, 18 * s), sf);
            g.DrawString("CLICK OPEN  //  RIGHT-CLICK MENU", subFont, pb, new RectangleF(tx, c.Y + m + 19 * s, tw, 16 * s), sf);
        }
    }

    void Render() {
        if (!IsHandleCreated || !Visible) return;
        using (var bmp = Compose()) {
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
            try {
                var size = new SZ { W = bmp.Width, H = bmp.Height };
                var src = new PT();
                var pos = new PT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_OVER, AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            } finally {
                SelectObject(memDc, old);
                DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    // ---- mouse ----

    void HitTest(Point p, out int tileIdx, out int btn) {
        tileIdx = -1; btn = -1;
        if (!Expanded) return;
        var pill = PillRect(Slide);
        for (int i = 0; i < Count; i++) if (TileRect(i, pill).Contains(p)) { tileIdx = i; return; }
        if (Hover >= 0 && Hover < Count) {
            var c = CardRect(Hover, pill);
            if (c.Contains(p)) {
                tileIdx = Hover;
                if (!IsTray(Hover)) {
                    var bs = Buttons(c);
                    for (int k = 0; k < 3; k++) if (bs[k].Contains(p)) btn = k;
                }
                return;
            }
            if (Bridge(c, TileRect(Hover, pill)).Contains(p) || pill.Contains(p)) tileIdx = Hover;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        leave.Stop();
        if (!mouseInside) { mouseInside = true; UpdateExpanded(); }
        int t, b;
        HitTest(e.Location, out t, out b);
        if (t != Hover || b != HoverBtn) { Hover = t; HoverBtn = b; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        mouseInside = false;
        leave.Start();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) {
        base.OnMouseDoubleClick(e);
        int t, b;
        HitTest(e.Location, out t, out b);
        if (e.Button == MouseButtons.Left && t >= 0 && t < Count && IsTray(t)) TrayHost.DoubleClick(trayShown[t - Wins.Count], Cursor.Position);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        int t, b;
        HitTest(e.Location, out t, out b);
        if (t < 0 || t >= Count) return;
        if (IsTray(t)) {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                TrayHost.Click(trayShown[t - Wins.Count], e.Button == MouseButtons.Right, Cursor.Position);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var w = Wins[t];
        Hover = HoverBtn = -1;
        switch (b < 0 ? 0 : b) {
            case 0: Unminimize(w.Hwnd, false); SetForegroundWindow(w.Hwnd); break;
            case 1: Unminimize(w.Hwnd, true); SetForegroundWindow(w.Hwnd); break;
            case 2:
                PostMessage(w.Hwnd, 0x112 /* WM_SYSCOMMAND */, (IntPtr)0xF060 /* SC_CLOSE */, IntPtr.Zero);
                pendingClose = w.Hwnd; pendingCloseAt = Environment.TickCount;   // re-checked by the 1s poll
                break;
        }
        settle.Stop(); settle.Start();
    }
}

class TrayIcon {
    public IntPtr Hwnd;        // the app's window that receives the callback
    public uint Id, CallbackMsg, Version;
    public Guid Guid;
    public string Tip = "", Proc = "";
    public Bitmap Icon;
    public bool Hidden;
}

// A minimal notification area. Apps reach the tray through the window of class Shell_TrayWnd, which
// explorer's taskbar normally owns; with none, Shell_NotifyIcon fails and some apps (Boot Camp, which
// also runs the MacBook's brightness keys) exit outright. This window accepts their icons and the dock
// shows them. Only created in shell mode: with explorer running, its own taskbar does this.
class TrayHost : NativeWindow {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public IntPtr proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string menu, name; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public readonly List<TrayIcon> Icons = new List<TrayIcon>();
    public event Action Changed = delegate { };

    public TrayHost() {
        // Created by hand and then subclassed: WinForms' own CreateHandle only finds global window classes.
        var wc = new WNDCLASS { proc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), inst = GetModuleHandle(null), name = "Shell_TrayWnd" };
        if (RegisterClass(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClass Shell_TrayWnd");
        var h = CreateWindowEx(0x80 /* TOOLWINDOW */, "Shell_TrayWnd", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx Shell_TrayWnd");
        AssignHandle(h);
        // apps that were already running (a minilaunch restart) re-add their icons when they see this
        PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
    }

    public List<TrayIcon> Shown {
        get {
            Icons.RemoveAll(i => !IsWindow(i.Hwnd));   // the app quit without removing its icon
            return Icons.Where(i => !i.Hidden && i.Icon != null).ToList();
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x4A /* WM_COPYDATA */) {
            var dwData = Marshal.ReadIntPtr(m.LParam);
            var lpData = Marshal.ReadIntPtr(m.LParam, IntPtr.Size * 2);
            // dwData 1 = Shell_NotifyIcon; 0 = SHAppBarMessage, 3 = icon-position queries: no taskbar to report
            if ((long)dwData == 1 && lpData != IntPtr.Zero) { m.Result = (IntPtr)(Notify(lpData) ? 1 : 0); return; }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    // TRAYNOTIFYDATA: signature, message, then NOTIFYICONDATAW with handles as 32-bit values (so 32- and
    // 64-bit apps share one layout): cbSize 8, hWnd 12, uID 16, uFlags 20, uCallbackMessage 24, hIcon 28,
    // szTip 32 (128 chars), dwState 288, dwStateMask 292, szInfo 296, uVersion 808, ..., guidItem 944.
    bool Notify(IntPtr p) {
        if (Marshal.ReadInt32(p) != 0x34753423) return false;
        int msg = Marshal.ReadInt32(p, 4), cb = Marshal.ReadInt32(p, 8);
        var hwnd = (IntPtr)Marshal.ReadInt32(p, 12);
        uint id = (uint)Marshal.ReadInt32(p, 16), flags = (uint)Marshal.ReadInt32(p, 20);
        var guid = Guid.Empty;
        if ((flags & 0x20) != 0 && cb >= 952) { var g = new byte[16]; Marshal.Copy(p + 944, g, 0, 16); guid = new Guid(g); }
        var icon = Icons.FirstOrDefault(i => guid != Guid.Empty ? i.Guid == guid : i.Hwnd == hwnd && i.Id == id);

        switch (msg) {
            case 0:   // NIM_ADD
                if (icon != null) return false;
                icon = new TrayIcon { Hwnd = hwnd, Id = id, Guid = guid, Proc = ProcName(hwnd) };
                Icons.Add(icon);
                Apply(icon, p, flags);
                break;
            case 1:   // NIM_MODIFY
                if (icon == null) return false;
                Apply(icon, p, flags);
                break;
            case 2:   // NIM_DELETE
                if (icon == null) return false;
                if (icon.Icon != null) icon.Icon.Dispose();
                Icons.Remove(icon);
                break;
            case 4:   // NIM_SETVERSION
                if (icon == null) return false;
                icon.Version = (uint)Marshal.ReadInt32(p, 808);
                return true;
            default: return true;   // NIM_SETFOCUS
        }
        Changed();
        return true;
    }

    void Apply(TrayIcon icon, IntPtr p, uint flags) {
        if ((flags & 0x1) != 0) icon.CallbackMsg = (uint)Marshal.ReadInt32(p, 24);                    // NIF_MESSAGE
        if ((flags & 0x2) != 0) {                                                                       // NIF_ICON
            // copy it now: the app may destroy its icon handle right after the call
            var h = (IntPtr)Marshal.ReadInt32(p, 28);
            if (icon.Icon != null) icon.Icon.Dispose();
            icon.Icon = h == IntPtr.Zero ? null : global::Icons.FromBorrowed(h);
        }
        if ((flags & 0x4) != 0) icon.Tip = (Marshal.PtrToStringUni(p + 32, 128) ?? "").Split('\0')[0];  // NIF_TIP
        if ((flags & 0x8) != 0) {                                                                       // NIF_STATE
            uint state = (uint)Marshal.ReadInt32(p, 288), mask = (uint)Marshal.ReadInt32(p, 292);
            if ((mask & 1) != 0) icon.Hidden = (state & 1) != 0;                                        // NIS_HIDDEN
        }
    }

    static string ProcName(IntPtr hwnd) {
        uint pid; GetWindowThreadProcessId(hwnd, out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }

    // Tells the app its icon was clicked, in the message format it asked for (NIM_SETVERSION).
    public static void Click(TrayIcon icon, bool right, Point screen) {
        uint down = right ? 0x204u : 0x201u, up = right ? 0x205u : 0x202u;   // WM_[RL]BUTTONDOWN / UP
        uint after = right ? 0x7Bu : 0x400u;                                  // WM_CONTEXTMENU / NIN_SELECT
        Send(icon, screen, icon.Version >= 3 ? new[] { down, up, after } : new[] { down, up });
    }

    // Older tray apps open their window on a double-click rather than a click.
    public static void DoubleClick(TrayIcon icon, Point screen) { Send(icon, screen, new[] { 0x203u /* WM_LBUTTONDBLCLK */, 0x202u }); }

    static void Send(TrayIcon icon, Point screen, uint[] events) {
        uint pid; GetWindowThreadProcessId(icon.Hwnd, out pid);
        // the click on the dock gave us foreground rights; pass them on so the app's menu or window can come forward
        keybd_event(0, 0, 0, UIntPtr.Zero); keybd_event(0, 0, 2, UIntPtr.Zero);
        AllowSetForegroundWindow(pid);
        // v4: lParam = LOWORD event, HIWORD icon id; wParam = anchor point. Older: wParam = id, lParam = event.
        var anchor = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
        foreach (var ev in events)
            if (icon.Version >= 4) PostMessage(icon.Hwnd, icon.CallbackMsg, anchor, (IntPtr)(((int)icon.Id << 16) | (int)ev));
            else PostMessage(icon.Hwnd, icon.CallbackMsg, (IntPtr)icon.Id, (IntPtr)ev);
    }
}

// Three-finger horizontal swipe on a Windows Precision Touchpad cycles through open windows.
// Explorer normally owns touchpad gestures; without it we read the touchpad's HID reports via raw input.
// Needs a PTP driver (on MacBooks: imbushuo/mac-precision-touchpad); with Apple's Boot Camp driver
// the trackpad only reports mouse events and this simply never fires.
class Gestures : NativeWindow {
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_VALUE_CAPS {
        public ushort UsagePage; public byte ReportID, IsAlias; public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount, R1, R2, R3, R4, R5;
        public uint UnitsExp, Units; public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, IntPtr report, uint len);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint n, IntPtr pp, IntPtr report, uint len);
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    class Pad { public IntPtr Pp; public List<ushort> Fingers = new List<ushort>(); public int XMax = 1, YMax = 1; }
    readonly Dictionary<IntPtr, Pad> pads = new Dictionary<IntPtr, Pad>();
    readonly Action<int> onSwipe;   // +1 next, -1 previous
    public Action<int, double, double> Trace;   // fingers down, mean x, mean y per report (--test-gestures)

    // One gesture = first finger down until every finger is up. During a quick swipe one fingertip often
    // blinks out for a report or two (3 -> 2 -> 3), so movement is accumulated per contact ID rather than
    // from the centroid, and the gesture counts as three-finger once three were seen together.
    Dictionary<uint, PointF> prev = new Dictionary<uint, PointF>();
    double accX, accY;
    int threeReports;
    bool tooMany, fired;

    public Gestures(Action<int> swipe) {
        onSwipe = swipe;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });   // HWND_MESSAGE: invisible, input only
        var d = new[] { new RAWINPUTDEVICE { UsagePage = 0x0D, Usage = 0x05, Flags = 0x100 /* RIDEV_INPUTSINK */, Target = Handle } };
        if (!RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            Index.Log("gestures: RegisterRawInputDevices failed " + Marshal.GetLastWin32Error());
    }

    Pad PadFor(IntPtr dev) {
        Pad p;
        if (pads.TryGetValue(dev, out p)) return p;
        p = new Pad();
        uint size = 0;
        GetRawInputDeviceInfo(dev, 0x20000005 /* RIDI_PREPARSEDDATA */, IntPtr.Zero, ref size);
        if (size > 0) {
            p.Pp = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(dev, 0x20000005, p.Pp, ref size) == uint.MaxValue) { Marshal.FreeHGlobal(p.Pp); p.Pp = IntPtr.Zero; }
        }
        if (p.Pp != IntPtr.Zero) {
            // every finger is a logical collection with its own X/Y; find them through the X value caps
            ushort n = 64;
            var caps = new HIDP_VALUE_CAPS[n];
            if (HidP_GetValueCaps(0, caps, ref n, p.Pp) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++) {
                    if (caps[i].UsagePage != 1 || caps[i].IsRange != 0) continue;
                    if (caps[i].UsageMin == 0x30) { p.Fingers.Add(caps[i].LinkCollection); p.XMax = Math.Max(p.XMax, caps[i].LogicalMax); }
                    if (caps[i].UsageMin == 0x31) p.YMax = Math.Max(p.YMax, caps[i].LogicalMax);
                }
        }
        pads[dev] = p;
        return p;
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x00FF) {   // WM_INPUT
            try { OnInput(m.LParam); } catch (Exception ex) { Index.Log("gestures: " + ex.Message); }
        }
        base.WndProc(ref m);
    }

    void OnInput(IntPtr hRaw) {
        uint hdr = (uint)(8 + IntPtr.Size * 2), size = 0;
        GetRawInputData(hRaw, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, hdr);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try {
            if (GetRawInputData(hRaw, 0x10000003, buf, ref size, hdr) != size || Marshal.ReadInt32(buf) != 2 /* RIM_TYPEHID */) return;
            var pad = PadFor(Marshal.ReadIntPtr(buf, 8));
            if (pad.Pp == IntPtr.Zero || pad.Fingers.Count == 0) return;
            uint repLen = (uint)Marshal.ReadInt32(buf, (int)hdr), count = (uint)Marshal.ReadInt32(buf, (int)hdr + 4);
            for (uint r = 0; r < count; r++) Report(pad, buf + (int)hdr + 8 + (int)(r * repLen), repLen);
        } finally { Marshal.FreeHGlobal(buf); }
    }

    void Report(Pad pad, IntPtr rep, uint len) {
        var now = new Dictionary<uint, PointF>();
        var usages = new ushort[16];
        foreach (var link in pad.Fingers) {
            uint n = (uint)usages.Length;
            if (HidP_GetUsages(0, 0x0D, link, usages, ref n, pad.Pp, rep, len) != HIDP_STATUS_SUCCESS) continue;
            bool tip = false;
            for (int i = 0; i < n; i++) if (usages[i] == 0x42) tip = true;   // Tip Switch
            if (!tip) continue;
            uint x, y, id;
            if (HidP_GetUsageValue(0, 1, link, 0x30, out x, pad.Pp, rep, len) != HIDP_STATUS_SUCCESS) continue;
            if (HidP_GetUsageValue(0, 1, link, 0x31, out y, pad.Pp, rep, len) != HIDP_STATUS_SUCCESS) continue;
            if (HidP_GetUsageValue(0, 0x0D, link, 0x51, out id, pad.Pp, rep, len) != HIDP_STATUS_SUCCESS) id = link;   // Contact ID
            now[id] = new PointF((float)x / pad.XMax, (float)y / pad.YMax);
        }
        if (Trace != null) Trace(now.Count, now.Count > 0 ? now.Values.Average(p => p.X) : 0, now.Count > 0 ? now.Values.Average(p => p.Y) : 0);

        if (now.Count == 0) {   // all fingers up: gesture over
            prev.Clear(); accX = accY = 0; threeReports = 0; tooMany = fired = false;
            return;
        }
        if (now.Count == 3) threeReports++;
        if (now.Count > 3) tooMany = true;   // four-finger gestures aren't ours
        double dx = 0, dy = 0; int matched = 0;
        foreach (var kv in now) {
            PointF p;
            if (prev.TryGetValue(kv.Key, out p)) { dx += kv.Value.X - p.X; dy += kv.Value.Y - p.Y; matched++; }
        }
        if (matched > 0) { accX += dx / matched; accY += dy / matched; }
        prev = now;

        if (!fired && !tooMany && threeReports >= 2 && Math.Abs(accX) > 0.15 && Math.Abs(accX) > Math.Abs(accY) * 1.5) {
            fired = true;   // one switch per swipe
            onSwipe(accX < 0 ? 1 : -1);
        }
    }
}

// Cycles focus through top-level windows (most recently used first) and flashes a card naming the target.
class Switcher : Form {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder sb, ref int size);

    readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
    readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer { Interval = 900 };
    readonly Font titleFont, subFont;
    readonly float s;
    List<IntPtr> order = new List<IntPtr>();
    int pos, lastSwipe;
    string title = "", sub = "";
    Bitmap icon;

    public Switcher() {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
        titleFont = new Font("Bahnschrift", 17 * s, GraphicsUnit.Pixel);
        subFont = new Font("Bahnschrift", 12 * s, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Ink;
        DoubleBuffered = true;
        Size = new Size((int)(420 * s), (int)(84 * s));
        hide.Tick += delegate { hide.Stop(); Hide(); };
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8 | 0x20;   // TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT (clicks pass through)
            cp.ClassStyle |= 0x20000;                         // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    bool Switchable(IntPtr h) {
        if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        int ex = GetWindowLong(h, -20);
        if ((ex & 0x80) != 0 || (ex & 0x08000000) != 0) return false;   // tool / no-activate windows
        int cloaked;
        if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return false;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == ownPid) return false;
        var sb = new System.Text.StringBuilder(2);
        return GetWindowText(h, sb, 2) > 0;
    }

    public void Step(int dir) {
        // a fresh gesture re-reads the z-order (most recent first); quick repeats keep walking the same list
        if (Environment.TickCount - lastSwipe > 1500 || order.Count == 0) {
            order = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l) { if (Switchable(h)) order.Add(h); return true; }, IntPtr.Zero);
            var fg = GetForegroundWindow();
            pos = Math.Max(0, order.IndexOf(fg));
        }
        lastSwipe = Environment.TickCount;
        order.RemoveAll(h => !IsWindow(h));
        if (order.Count < 2) { Flash("No other windows", "", null); return; }
        pos = ((pos + dir) % order.Count + order.Count) % order.Count;
        var target = order[pos];
        Activate(target);
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(target, sb, sb.Capacity);
        Flash(sb.ToString(), (pos + 1) + " / " + order.Count + "   ·   " + (dir > 0 ? "next ▸" : "◂ previous"), IconOf(target));
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    internal static void Activate(IntPtr h) {
        if (IsIconic(h)) global::Dock.Unminimize(h, false);   // plain "Dock" is Control.Dock in here
        // Foreground-lock rules only let the process that received the last input event take focus.
        // A synthetic event with virtual key 0 makes that us without any app seeing a real keystroke.
        keybd_event(0, 0, 0, UIntPtr.Zero);
        keybd_event(0, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        var fg = GetForegroundWindow();
        uint fgThread = 0, me = GetCurrentThreadId(), pid;
        if (fg != IntPtr.Zero) fgThread = GetWindowThreadProcessId(fg, out pid);
        // raw touchpad input doesn't grant foreground rights, so borrow the foreground thread's input state
        if (fgThread != 0 && fgThread != me) AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        bool ok = SetForegroundWindow(h);
        if (fgThread != 0 && fgThread != me) AttachThreadInput(me, fgThread, false);
        if (!ok || GetForegroundWindow() != h) SwitchToThisWindow(h, true);
    }

    Bitmap IconOf(IntPtr h) {
        IntPtr hi;
        if (SendMessageTimeout(h, 0x7F /* WM_GETICON */, (IntPtr)1, IntPtr.Zero, 2, 100, out hi) != IntPtr.Zero && hi != IntPtr.Zero) {
            var b = Icons.FromBorrowed(hi);
            if (b != null) return b;
        }
        uint pid; GetWindowThreadProcessId(h, out pid);
        var ph = OpenProcess(0x1000, false, pid);
        if (ph == IntPtr.Zero) return null;
        try {
            var sb = new System.Text.StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(ph, 0, sb, ref n) ? Icons.ForPath(sb.ToString()) : null;
        } finally { CloseHandle(ph); }
    }

    void Flash(string t, string st, Bitmap ic) {
        title = t; sub = st;
        if (icon != null) icon.Dispose();
        icon = ic;
        var scr = Screen.PrimaryScreen.Bounds;
        Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Bottom - Height - (int)(70 * s));
        if (!Visible) Show();
        Invalidate();
        hide.Stop(); hide.Start();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.Clear(Theme.Ink);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(120, Theme.Primary), 1f)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        Wallpaper.Neon(g, Theme.Secondary, 2 * s, 14 * s, 2 * s, Height - 14 * s, 2.5f * s);
        float x = 22 * s;
        if (icon != null) {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, x, (Height - 36 * s) / 2, 36 * s, 36 * s);
            x += 50 * s;
        }
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, title, titleFont, new Rectangle((int)x, (int)(17 * s), Width - (int)x - (int)(16 * s), (int)(26 * s)), Theme.HudText, flags);
        TextRenderer.DrawText(g, sub, subFont, new Rectangle((int)x, (int)(47 * s), Width - (int)x - (int)(16 * s), (int)(20 * s)), Theme.Primary, flags);
    }
}

// Explorer gives focus back to the last window after Win+L / unlock; without it the session comes back
// with nothing focused. Track the last real app window and re-activate it when the session unlocks.
class SessionFocus : NativeWindow {
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    const int WM_WTSSESSION_CHANGE = 0x2B1, WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;

    readonly WinEventProc onForeground;   // kept in a field so the GC doesn't collect the delegate
    readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
    readonly System.Windows.Forms.Timer restore = new System.Windows.Forms.Timer { Interval = 400 };
    IntPtr last, saved;
    bool locked;

    public SessionFocus() {
        CreateHandle(new CreateParams { Caption = "minilaunch session", ExStyle = 0x80 /* TOOLWINDOW */ });   // hidden top-level: receives session broadcasts
        if (!WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */))
            Index.Log("sessionfocus: WTSRegisterSessionNotification failed " + Marshal.GetLastWin32Error());
        onForeground = (hook, ev, hwnd, obj, child, thread, time) => { if (!locked && obj == 0) Remember(hwnd); };
        SetWinEventHook(0x03, 0x03, IntPtr.Zero, onForeground, 0, 0, 0);   // EVENT_SYSTEM_FOREGROUND
        Remember(GetForegroundWindow());
        // the secure desktop is still tearing down when the unlock arrives; give it a moment
        restore.Tick += delegate { restore.Stop(); Restore(); };
        // the foreground hook misses some switches (e.g. a new console window), so also sample once a second
        var poll = new System.Windows.Forms.Timer { Interval = 1000 };
        poll.Tick += delegate { if (!locked) Remember(GetForegroundWindow()); };
        poll.Start();
    }

    void Remember(IntPtr h) {
        if (h == IntPtr.Zero || !IsWindowVisible(h)) return;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == ownPid) return;   // our desktop, dock, launcher and switcher card don't count
        last = h;
    }

    void Restore() {
        if (saved == IntPtr.Zero || !IsWindow(saved)) return;
        if (GetForegroundWindow() == saved) return;
        Switcher.Activate(saved);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_WTSSESSION_CHANGE) {
            int code = m.WParam.ToInt32();
            if (code == WTS_SESSION_LOCK) { Remember(GetForegroundWindow()); locked = true; saved = last; }
            else if (code == WTS_SESSION_UNLOCK) { locked = false; restore.Stop(); restore.Start(); }
        }
        base.WndProc(ref m);
    }
}

static class Program {
    static SessionFocus sessionFocus;
    static DesktopForm desktop;
    static Dock dock;
    static TrayHost tray;
    static Gestures gestures;
    static Switcher switcher;

    // Shell mode: start what explorer would have run from the Run keys (HKLM 64/32-bit, then HKCU),
    // skipping entries switched off in Task Manager's Startup tab.
    static void ShellDuties() {
        var hives = new[] {
            new { Root = Microsoft.Win32.RegistryHive.LocalMachine, View = Microsoft.Win32.RegistryView.Registry64, Approved = "Run" },
            new { Root = Microsoft.Win32.RegistryHive.LocalMachine, View = Microsoft.Win32.RegistryView.Registry32, Approved = "Run32" },
            new { Root = Microsoft.Win32.RegistryHive.CurrentUser, View = Microsoft.Win32.RegistryView.Default, Approved = "Run" },
        };
        foreach (var h in hives) {
            try {
                using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(h.Root, h.View))
                using (var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                using (var ok = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + h.Approved)) {
                    if (run == null) continue;
                    foreach (var name in run.GetValueNames()) {
                        var state = ok == null ? null : ok.GetValue(name) as byte[];
                        if (state != null && state.Length > 0 && (state[0] & 1) == 1) continue;   // 02 = enabled, 03 = disabled
                        var cmd = run.GetValue(name) as string;
                        if (string.IsNullOrWhiteSpace(cmd)) continue;
                        try { StartCommandLine(Environment.ExpandEnvironmentVariables(cmd.Trim())); }
                        catch (Exception ex) { Index.Log("Run " + name + ": " + ex.Message); }
                    }
                }
            } catch (Exception ex) { Index.Log("Run key: " + ex.Message); }
        }
    }

    // Run-key values are raw command lines, often with an unquoted path containing spaces
    // ("C:\Program Files\Boot Camp\Bootcamp.exe"), so split on the first ".exe" when unquoted.
    static void StartCommandLine(string cmd) {
        string exe, args = "";
        if (cmd.StartsWith("\"")) {
            int q = cmd.IndexOf('"', 1);
            exe = q > 0 ? cmd.Substring(1, q - 1) : cmd.Trim('"');
            if (q > 0) args = cmd.Substring(q + 1).Trim();
        } else {
            int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = e > 0 ? cmd.Substring(0, e + 4) : cmd;
            if (e > 0) args = cmd.Substring(e + 4).Trim();
        }
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
    }

    [STAThread]
    static void Main(string[] args) {
        // --theme <name> overrides the configured theme for this run (previews: --render ... --theme zen)
        string themeArg = null;
        int ti = Array.IndexOf(args, "--theme");
        if (ti >= 0 && ti + 1 < args.Length) { themeArg = args[ti + 1]; args = args.Where((a, i) => i != ti && i != ti + 1).ToArray(); }
        Theme.Load(themeArg);
        if (args.Length >= 2 && args[0] == "--render") {
            // --render out.png [width height]: wallpaper + HUD with sample stats, for previewing
            int w = args.Length >= 4 ? int.Parse(args[2]) : Screen.PrimaryScreen.Bounds.Width;
            int h = args.Length >= 4 ? int.Parse(args[3]) : Screen.PrimaryScreen.Bounds.Height;
            using (var bmp = Wallpaper.Make(w, h))
            using (var g = Graphics.FromImage(bmp)) {
                // sample stats and identity, so previews (and the README screenshots) show nobody's real machine
                var st = new Stats { MemUsedGB = 1.02, MemTotalGB = 7.91, Cpu = 3, Battery = 87, Charging = true, Online = true, Vpn = "100.64.0.7" };
                Theme.Preview = true;
                Hud.Draw(g, new Size(w, h), h / 800f, DateTime.Now, st);
                bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            }
            return;
        }
        if (args.Length >= 2 && args[0] == "--render-dock") {
            // --render-dock out.png: dock with sample windows and the hover card open, over the wallpaper
            Index.Rebuild();
            var d = new Dock(false);
            foreach (var n in new[] { "Firefox", "VLC media player", "PowerShell 7 (x64)" }) {
                var e = Index.All.FirstOrDefault(x => x.Name == n);
                if (e != null) d.Wins.Add(new WinItem { Title = n == "Firefox" ? "Mozilla Firefox - neon city wallpapers" : n, Proc = n.Split(' ')[0].ToLowerInvariant(), Icon = Icons.ForPath(e.Target) });
            }
            d.Expanded = true; d.Slide = 0; d.Hover = 1; d.HoverBtn = 0;
            var b = Screen.PrimaryScreen.Bounds;
            using (var wall = Wallpaper.Make(b.Width, b.Height))
            using (var g = Graphics.FromImage(wall))
            using (var dockBmp = d.Compose()) {
                g.DrawImage(dockBmp, b.Width - dockBmp.Width, 0);
                wall.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            }
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-switch") {
            // --test-switch [+1|-1]: one swipe step without a touchpad, for checking switching and the card
            Application.EnableVisualStyles();
            var sw = new Switcher();
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += delegate { Application.Exit(); };
            sw.Step(args.Length >= 2 ? int.Parse(args[1]) : 1);
            t.Start();
            Application.Run();
            return;
        }
        if (args.Length >= 2 && args[0] == "--test-gestures") {
            // --test-gestures out.log: guided prompts; logs finger counts and detected swipes (no switching)
            Application.EnableVisualStyles();
            var log = new StreamWriter(args[1]) { AutoFlush = true };
            var t0 = Environment.TickCount;
            var form = new Form { Text = "Gesture test", TopMost = true, Width = 760, Height = 260, StartPosition = FormStartPosition.CenterScreen,
                                  BackColor = Color.FromArgb(16, 10, 30), FormBorderStyle = FormBorderStyle.FixedDialog, KeyPreview = true };
            var label = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(0, 232, 255), Font = new Font("Bahnschrift", 22f),
                                    TextAlign = ContentAlignment.MiddleCenter, Text = "Gesture test\r\n\r\nPress SPACE to start" };
            form.Controls.Add(label);
            var steps = new[] { "Move the pointer around with ONE finger", "Scroll with TWO fingers", "Swipe THREE fingers LEFT, 3 times",
                                "Swipe THREE fingers RIGHT, 3 times", "Done - closing" };
            int step = -1, swipes = 0;
            int lastDown = -1;
            var timer = new System.Windows.Forms.Timer { Interval = 6000 };
            Action next = () => {
                step++;
                if (step >= steps.Length) { log.WriteLine("# swipes detected: " + swipes); log.Close(); Application.Exit(); return; }
                log.WriteLine("{0,7} ==== STEP {1}: {2}", Environment.TickCount - t0, step, steps[step]);
                label.Text = "Step " + (step + 1) + " of " + steps.Length + "\r\n\r\n" + steps[step];
                timer.Interval = step == steps.Length - 1 ? 1500 : 6000;
                timer.Stop(); timer.Start();
            };
            timer.Tick += delegate { next(); };
            form.KeyDown += (o, e) => { if (e.KeyCode == Keys.Space && step < 0) next(); };
            Gestures g = null;
            form.Shown += delegate {
                g = new Gestures(dir => {
                    swipes++;
                    log.WriteLine("{0,7} SWIPE {1}", Environment.TickCount - t0, dir > 0 ? "next (left)" : "previous (right)");
                    label.Text = "Step " + (step + 1) + " of " + steps.Length + "\r\n\r\n" + (step >= 0 ? steps[step] : "") + "\r\n✔ swipe " + (dir > 0 ? "LEFT" : "RIGHT");
                });
                g.Trace = (down, x, y) => {
                    if (down != lastDown) { log.WriteLine("{0,7} fingers {1}  x {2:F3} y {3:F3}", Environment.TickCount - t0, down, x, y); lastDown = down; }
                };
            };
            Application.Run(form);
            GC.KeepAlive(g);
            return;
        }
        if (args.Length >= 1 && args[0] == "--test-tray") {
            // --test-tray [exe]: run the tray on its own, start exe (or nothing), list the icons it collects after
            // 10 s and exit. Must not be run while minilaunch is the shell (both would own Shell_TrayWnd).
            var t = new TrayHost();
            if (args.Length >= 2) Process.Start(args[1]);
            var timer = new System.Windows.Forms.Timer { Interval = 10000 };
            timer.Tick += delegate {
                foreach (var i in t.Icons)
                    Console.WriteLine("icon: " + (i.Proc.Length > 0 ? i.Proc : "?") + "  id " + i.Id + "  v" + i.Version + "  msg 0x" + i.CallbackMsg.ToString("X") +
                                      "  hidden " + i.Hidden + "  image " + (i.Icon != null) + "  tip \"" + i.Tip.Replace("\n", " / ") + "\"");
                Console.WriteLine(t.Icons.Count + " icon(s)");
                Application.Exit();
            };
            timer.Start();
            Application.Run();
            return;
        }
        if (args.Length >= 2 && args[0] == "--query") {
            Index.LoadHistory();
            Index.Rebuild();
            Console.WriteLine("indexed " + Index.All.Count + " entries");
            foreach (var e in Index.Search(string.Join(" ", args.Skip(1)), 8))
                Console.WriteLine(e.Score.ToString().PadLeft(5) + "  " + e.Kind.PadRight(7) + " " + e.Name + "  ->  " + e.Target + "   [" + e.Sub + "]");
            return;
        }
        Index.ShellMode = args.Contains("--shell");
        bool created;
        using (new Mutex(true, "minilaunch-single-instance", out created)) {
            if (!created) return;
            // as the shell, an unhandled exception must not take the session's only UI down
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) => Index.Log(e.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (o, e) => Index.Log(e.ExceptionObject.ToString());
            Index.LoadHistory();
            Index.Rebuild();
            Application.EnableVisualStyles();
            var launcher = new LauncherForm();
            if (Index.ShellMode || args.Contains("--desktop")) {
                desktop = new DesktopForm(launcher);   // applies ShellSettings as it sizes itself
                desktop.Show();
                dock = new Dock(true);
                switcher = new Switcher();
                gestures = new Gestures(dir => switcher.Step(dir));
                sessionFocus = new SessionFocus();
            }
            if (Index.ShellMode) {
                // the tray must exist before the startup apps run, or they find no tray (and some quit).
                // It's an extra: if it can't be set up, the shell carries on without it.
                try {
                    tray = new TrayHost();
                    dock.Tray = tray;
                    tray.Changed += () => dock.RefreshList();
                } catch (Exception ex) { Index.Log("tray disabled: " + ex); tray = null; }
                ShellDuties();
            }
            Application.Run(launcher);
        }
    }
}
