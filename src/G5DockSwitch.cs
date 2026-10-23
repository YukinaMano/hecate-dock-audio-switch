// HECATE dock audio switch
// Switches the Windows default playback device when the headset is picked up from / placed on its base.
//
// The dock pushes status frames on an HID control interface; it never reports "headset is on the base"
// through normal audio APIs, so the audio endpoint stays present forever and Windows never auto-switches.
//
// Everything machine specific is discovered at runtime:
//   - HID control interface : registry DeviceClasses\{HID}\..., filtered by VID/PID/interface string
//   - audio endpoint        : MMDevices\Audio\Render\..., filtered by the device description prefix
//
// Build: see build.ps1 (uses the C# compiler that ships with Windows, no SDK required).

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class Program
{
    // ---- defaults, may be overridden by config.ini next to the exe ----
    static string CfgVid   = "35BB";
    static string CfgPid   = "A228";
    static string CfgDesc  = "HECATE";
    static string CfgIface = "MI_04&Col03";

    const string HidClassKey = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{4d1e55b2-f16f-11cf-88cb-001111000030}";
    const string RenderKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string DescPropId  = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    static string Dir = "";
    static string LogPath = "", StatePath = "", PrevPath = "", OverridePath = "", OutPath = "";

    static string hidPath = "";
    static string hsId = "";      // {0.0.0.00000000}.{guid}
    static string hsGuid = "";    // {guid}

    static string applied = "";
    static string curState = "";
    static string lastHex = "";
    static string prevDefault = "";

    // ------------------------------------------------------------------ lifecycle

    static void Main(string[] args)
    {
        Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        LogPath      = Path.Combine(Dir, "g5-switch.log");
        StatePath    = Path.Combine(Dir, "last-state.txt");
        PrevPath     = Path.Combine(Dir, "last-default.txt");
        OverridePath = Path.Combine(Dir, "override.txt");
        OutPath      = Path.Combine(Dir, "probe.txt");

        LoadConfig();
        try { if (File.Exists(PrevPath)) prevDefault = File.ReadAllText(PrevPath).Trim(); } catch { }

        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (mode == "--probe" || mode == "probe")   { Probe(); return; }
        if (mode == "--capture" || mode == "capture")
        {
            int secs = 60;
            if (args.Length > 1) { int.TryParse(args[1], out secs); if (secs <= 0) secs = 60; }
            Capture(secs);
            return;
        }
        if (mode == "headset"  || mode == "--headset")  { Manual("inuse");  return; }
        if (mode == "external" || mode == "--external") { Manual("docked"); return; }

        bool created;
        using (var mutex = new Mutex(true, "HECATE_DockAudioSwitch", out created))
        {
            if (!created) return;
            Run();
        }
    }

    static void LoadConfig()
    {
        string cfg = Path.Combine(Dir, "config.ini");
        if (!File.Exists(cfg)) return;
        try
        {
            foreach (string raw in File.ReadAllLines(cfg))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (v.Length == 0) continue;
                switch (k)
                {
                    case "vid":   CfgVid = v; break;
                    case "pid":   CfgPid = v; break;
                    case "desc":  CfgDesc = v; break;
                    case "iface": CfgIface = v; break;
                }
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ discovery

    static void Resolve()
    {
        if (hidPath.Length == 0) hidPath = FindHidPath() ?? "";
        if (hsId.Length == 0)
        {
            string g;
            string id = FindEndpoint(out g);
            if (id != null) { hsId = id; hsGuid = g; }
        }
    }

    static string FindHidPath()
    {
        try
        {
            string wantVid = "vid_" + CfgVid.ToLowerInvariant();
            string wantPid = "pid_" + CfgPid.ToLowerInvariant();
            string wantIf = CfgIface.ToLowerInvariant();

            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = baseKey.OpenSubKey(HidClassKey))
            {
                if (k == null) return null;
                foreach (string name in k.GetSubKeyNames())
                {
                    string low = name.ToLowerInvariant();
                    if (!low.Contains(wantVid)) continue;
                    if (!low.Contains(wantPid)) continue;
                    if (wantIf.Length > 0 && !low.Contains(wantIf)) continue;
                    // key names look like: ##?#HID#VID_35BB&PID_A228&MI_04&Col03#7&...&0002#{guid}
                    // the real device path is the same string with "\\?\" instead of the leading "##?#"
                    if (!name.StartsWith("##?#")) continue;
                    return "\\\\?\\" + name.Substring(4);
                }
            }
        }
        catch { }
        return null;
    }

    static string FindEndpoint(out string guid)
    {
        guid = null;
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = baseKey.OpenSubKey(RenderKey))
            {
                if (root == null) return null;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (var props = root.OpenSubKey(sub + "\\Properties"))
                    {
                        if (props == null) continue;
                        object v = props.GetValue(DescPropId);
                        string desc = v as string;
                        if (desc == null) continue;
                        if (!desc.StartsWith(CfgDesc, StringComparison.OrdinalIgnoreCase)) continue;
                        guid = sub;
                        return "{0.0.0.00000000}." + sub;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    static int HsDeviceState()
    {
        if (hsGuid == null || hsGuid.Length == 0) return -1;
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = baseKey.OpenSubKey(RenderKey + "\\" + hsGuid))
            {
                if (k == null) return -1;
                object v = k.GetValue("DeviceState");
                return v == null ? -1 : Convert.ToInt32(v);
            }
        }
        catch { return -1; }
    }

    static string FirstOtherActive()
    {
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = baseKey.OpenSubKey(RenderKey))
            {
                if (root == null) return null;
                foreach (string sub in root.GetSubKeyNames())
                {
                    if (sub == hsGuid) continue;
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        object v = k.GetValue("DeviceState");
                        if (v == null || Convert.ToInt32(v) != 1) continue;
                        return "{0.0.0.00000000}." + sub;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ daemon

    static void Run()
    {
        Log("=== start pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " ===");
        Resolve();
        if (hidPath.Length == 0) Log("warn: dock HID interface not found yet");
        if (hsId.Length == 0) Log("warn: headset audio endpoint not found yet");

        try { if (File.Exists(StatePath)) applied = File.ReadAllText(StatePath).Trim(); } catch { }
        if (applied == "docked" || applied == "inuse")
        {
            Log("startup: applying last known state '" + applied + "'");
            ApplyState(applied, false);
        }

        IntPtr h = new IntPtr(-1), ev = IntPtr.Zero;
        byte[] buf = new byte[256];
        DateTime lastResolve = DateTime.UtcNow;

        while (true)
        {
            try
            {
                if ((DateTime.UtcNow - lastResolve).TotalSeconds > 30)
                {
                    lastResolve = DateTime.UtcNow;
                    if (hidPath.Length == 0) hidPath = FindHidPath() ?? "";
                    if (hsId.Length == 0) { string g; string id = FindEndpoint(out g); if (id != null) { hsId = id; hsGuid = g; } }
                }

                if (hidPath.Length == 0) { Thread.Sleep(2000); continue; }

                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    h = OpenHid(hidPath);
                    if (h == new IntPtr(-1)) { Thread.Sleep(2000); continue; }
                    ev = CreateEvent(IntPtr.Zero, true, false, null);
                    Log("hid: listening " + hidPath);
                }

                byte[] f = ReadOnce(h, ev, buf, 900);
                if (f != null && f.Length >= 4)
                {
                    string hex = Hex(f, Math.Min(f.Length, 14));
                    string st = Decode(f);
                    if (hex != lastHex)
                    {
                        lastHex = hex;
                        Log("frame: cmd=0x" + f[3].ToString("X2") + " [" + hex + "] -> " + (st ?? "ignored"));
                    }
                    if (st != null)
                    {
                        if (st != curState)
                        {
                            if (File.Exists(OverridePath))
                            {
                                try { File.Delete(OverridePath); Log("override cleared by dock event"); } catch { }
                            }
                            curState = st;
                        }
                        if (st != applied) { applied = st; Log("state: " + st); ApplyState(st, false); }
                    }
                }

                string desired = curState;
                if (File.Exists(OverridePath))
                {
                    string ov = "";
                    try { ov = File.ReadAllText(OverridePath).Trim(); } catch { }
                    if (ov == "inuse" || ov == "docked") desired = ov;
                }
                if (desired.Length > 0 && hsId.Length > 0) Enforce(desired);
            }
            catch (Exception ex)
            {
                Log("error: " + ex.Message);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
                if (ev != IntPtr.Zero) CloseHandle(ev);
                h = new IntPtr(-1); ev = IntPtr.Zero;
                Thread.Sleep(2000);
            }
        }
    }

    // ------------------------------------------------------------------ decoding
    //
    // 0x0A frame, payload bytes 5..10:
    //     in use   : 01 64 00 01 01 09
    //     on base  : 00 00 00 00 00 00   (headset is powered down, no telemetry)
    // 0x03 frame, byte 4: EF = in use, 01 = on base
    //
    // Do NOT poll the dock yourself: a self-issued 0x0A query returns a different, stale record
    // (00 64 01 00 00 00) that does not track the headset. Only pushed frames are reliable.

    static string Decode(byte[] f)
    {
        if (f.Length < 8) return null;
        if (f[0] != 0xD0 || f[1] != 0x35 || f[2] != 0xBB) return null;

        if (f[3] == 0x0A)
        {
            bool nz = false;
            for (int j = 5; j <= 10; j++) if (f[j] != 0) { nz = true; break; }
            if (!nz) return "docked";
            if (f[5] != 0) return "inuse";
            return null;
        }
        if (f[3] == 0x03 && f.Length >= 6)
        {
            if (f[4] == 0xEF) return "inuse";
            if (f[4] == 0x01) return "docked";
            return null;
        }
        return null;
    }

    // ------------------------------------------------------------------ audio

    static void ApplyState(string st, bool manual)
    {
        if (hsId.Length == 0) { Log("audio: headset endpoint unknown"); return; }
        string tag = manual ? "[manual] " : "";

        if (st == "inuse")
        {
            string cur = DefaultId();
            if (cur != null && cur != hsId)
            {
                prevDefault = cur;
                try { File.WriteAllText(PrevPath, cur, Encoding.ASCII); } catch { }
            }
            int hr = Show(hsId);
            Thread.Sleep(250);
            SetDefault(hsId);
            Log(tag + "audio: HEADSET (Show hr=" + hr + ")");
        }
        else
        {
            int hr = Hide(hsId);
            Thread.Sleep(250);
            string target = prevDefault;
            if (string.IsNullOrEmpty(target) || target == hsId) target = FirstOtherActive();
            if (target != null) SetDefault(target);
            Log(tag + "audio: EXTERNAL (Hide hr=" + hr + " -> " + (target ?? "none") + ")");
        }

        try { File.WriteAllText(StatePath, st, Encoding.ASCII); } catch { }
    }

    static void Enforce(string st)
    {
        int devState = HsDeviceState();
        string cur = DefaultId();

        if (st == "inuse")
        {
            if (devState != 1)
            {
                Show(hsId);
                Log("enforce: show headset");
                Thread.Sleep(200);
                cur = DefaultId();
            }
            if (cur != hsId)
            {
                if (cur != null) { prevDefault = cur; try { File.WriteAllText(PrevPath, cur, Encoding.ASCII); } catch { } }
                SetDefault(hsId);
                Log("enforce: default -> headset");
            }
        }
        else
        {
            if (devState == 1)
            {
                Hide(hsId);
                Log("enforce: hide headset");
                Thread.Sleep(200);
                cur = DefaultId();
            }
            if (cur == hsId)
            {
                string target = prevDefault;
                if (string.IsNullOrEmpty(target) || target == hsId) target = FirstOtherActive();
                if (target != null) { SetDefault(target); Log("enforce: default -> external"); }
            }
        }
    }

    static void Manual(string st)
    {
        Resolve();
        Log("[manual] -> " + st);
        try { File.WriteAllText(OverridePath, st, Encoding.ASCII); } catch { }
        ApplyState(st, true);
    }

    // ------------------------------------------------------------------ diagnostic modes

    static void Say(string line)
    {
        try { Console.WriteLine(line); } catch { }
        try { File.AppendAllText(OutPath, line + "\r\n", Encoding.UTF8); } catch { }
    }

    static void Probe()
    {
        Resolve();
        Say("== HECATE dock audio switch probe ==");
        Say("time        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Say("config      : vid=" + CfgVid + " pid=" + CfgPid + " desc=" + CfgDesc + " iface=" + CfgIface);
        Say("hid path    : " + (hidPath.Length > 0 ? hidPath : "(not found)"));
        Say("endpoint id : " + (hsId.Length > 0 ? hsId : "(not found)"));
        Say("endpoint vis: " + HsDeviceState());
        Say("default id  : " + (DefaultId() ?? "(none)"));

        if (hidPath.Length > 0)
        {
            IntPtr h = OpenHid(hidPath);
            if (h == new IntPtr(-1)) Say("open        : failed err=" + Marshal.GetLastWin32Error());
            else
            {
                IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
                byte[] buf = new byte[256];
                Say("listening   : 3 seconds for pushed frames...");
                DateTime end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end)
                {
                    byte[] f = ReadOnce(h, ev, buf, 500);
                    if (f == null || f.Length < 4) continue;
                    Say("frame       : cmd=0x" + f[3].ToString("X2") + " [" + Hex(f, Math.Min(f.Length, 14)) + "] -> " + (Decode(f) ?? "ignored"));
                }
                CloseHandle(ev);
                CloseHandle(h);
            }
        }
        Say("");
        Say("output also written to: " + OutPath);
    }

    static void Capture(int seconds)
    {
        Resolve();
        string file = Path.Combine(Dir, "capture.log");
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# capture started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("# hid path: " + hidPath);
        if (hidPath.Length == 0) { File.AppendAllText(file, sb.ToString(), Encoding.UTF8); Say("hid not found"); return; }

        IntPtr h = OpenHid(hidPath);
        if (h == new IntPtr(-1)) { Say("open failed"); return; }
        IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
        byte[] buf = new byte[256];
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        Say("capturing " + seconds + "s -> " + file);
        while (DateTime.UtcNow < end)
        {
            byte[] f = ReadOnce(h, ev, buf, 500);
            if (f == null || f.Length < 4) continue;
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  cmd=0x").Append(f[3].ToString("X2"))
              .Append("  ").Append(Hex(f, f.Length)).Append("  -> ").Append(Decode(f) ?? "ignored").AppendLine();
            File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            sb.Length = 0;
        }
        CloseHandle(ev);
        CloseHandle(h);
        Say("done");
    }

    // ------------------------------------------------------------------ plumbing

    static void Log(string msg)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + msg + "\r\n";
        for (int i = 0; i < 3; i++)
        {
            try { File.AppendAllText(LogPath, line, Encoding.UTF8); return; }
            catch { Thread.Sleep(40); }
        }
    }

    static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n && i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, uint n, out uint read, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED ov, out uint read, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")]
    static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll")]
    static extern bool CancelIoEx(IntPtr h, IntPtr ov);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    static IntPtr OpenHid(string path)
    {
        return CreateFile(path, 0x80000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
    }

    static byte[] ReadOnce(IntPtr h, IntPtr ev, byte[] buf, int timeoutMs)
    {
        var ov = new OVERLAPPED();
        ov.hEvent = ev;
        ResetEvent(ev);
        uint read;
        bool imm = ReadFile(h, buf, 64, out read, ref ov);
        if (imm)
        {
            var r = new byte[read];
            Array.Copy(buf, r, (int)read);
            return r;
        }
        if (Marshal.GetLastWin32Error() != 997) return null;
        if (WaitForSingleObject(ev, (uint)timeoutMs) != 0)
        {
            CancelIoEx(h, IntPtr.Zero);
            uint junk;
            GetOverlappedResult(h, ref ov, out junk, true);
            return null;
        }
        if (!GetOverlappedResult(h, ref ov, out read, false)) return null;
        var r2 = new byte[read];
        Array.Copy(buf, r2, (int)read);
        return r2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, out IntPtr pp);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string id, out IntPtr fmt);
        int GetDeviceFormat(string id, bool def, out IntPtr fmt);
        int ResetDeviceFormat(string id);
        int SetDeviceFormat(string id, IntPtr fmt, IntPtr fmt2);
        int GetProcessingPeriod(string id, bool def, out long a, out long b);
        int SetProcessingPeriod(string id, ref long p);
        int GetShareMode(string id, out IntPtr m);
        int SetShareMode(string id, IntPtr m);
        int GetPropertyValue(string id, IntPtr key, out IntPtr v);
        int SetPropertyValue(string id, IntPtr key, IntPtr v);
        int SetDefaultEndpoint(string id, int role);
        int SetEndpointVisibility(string id, bool visible);
    }

    static string DefaultId()
    {
        try
        {
            var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
            IMMDevice dev;
            if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return null;
            string id;
            dev.GetId(out id);
            return id;
        }
        catch { return null; }
    }

    static int Hide(string id)
    {
        try { return ((IPolicyConfig)(new CPolicyConfigClient())).SetEndpointVisibility(id, false); }
        catch { return -1; }
    }

    static int Show(string id)
    {
        try { return ((IPolicyConfig)(new CPolicyConfigClient())).SetEndpointVisibility(id, true); }
        catch { return -1; }
    }

    static void SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)(new CPolicyConfigClient());
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
        }
        catch { }
    }
}
