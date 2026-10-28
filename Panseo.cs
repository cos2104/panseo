// 판서 프로그램 - 와콤(Windows Ink) 필압 지원 화면 판서 도구
// 빌드: build.bat 실행 (Windows 기본 내장 .NET Framework 4 컴파일러 사용)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

// 모니터별 DPI 대응(.NET 4.8 동작 기준)을 켜기 위한 대상 프레임워크 지정
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Panseo
{
    public enum Tool { Mouse, Pen, Highlighter, EraseStroke, ErasePoint, Pan, Text, Shape }
    public enum ShapeKind { Line, Arrow, Rect, Ellipse }
    public enum BoardBg { Transparent, White, Chalk }

    class Change
    {
        public StrokeCollection Added;
        public StrokeCollection Removed;
    }

    class Layer
    {
        public readonly StrokeCollection Strokes = new StrokeCollection();
        public readonly List<Change> Undo = new List<Change>();
        public readonly List<Change> Redo = new List<Change>();
        public int Version; // 사용자가 판서를 바꿀 때마다 증가 (스크롤 따라가기의 기준 화면 갱신용)
        bool suppress;

        public Layer()
        {
            Strokes.StrokesChanged += (s, e) =>
            {
                if (suppress) return;
                Version++;
                var c = new Change { Added = new StrokeCollection(e.Added), Removed = new StrokeCollection(e.Removed) };
                Undo.Add(c);
                Redo.Clear();
                // 부분 지우개가 텍스트에 닿으면 조각을 남기지 않고 텍스트 전체를 지움
                bool textCut = false;
                foreach (var r in e.Removed) if (r is TextStroke) textCut = true;
                if (textCut && e.Added.Count > 0)
                {
                    var frags = c.Added;
                    c.Added = new StrokeCollection();
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        suppress = true;
                        foreach (var fr in frags) if (Strokes.Contains(fr)) Strokes.Remove(fr);
                        suppress = false;
                    }));
                }
            };
        }

        // 되돌리기 기록 없이 숨기기/다시 보이기 (스크롤 따라가기용)
        public void RemoveSilently(Stroke s)
        {
            suppress = true;
            if (Strokes.Contains(s)) Strokes.Remove(s);
            suppress = false;
        }

        public void AddSilently(Stroke s)
        {
            suppress = true;
            if (!Strokes.Contains(s)) Strokes.Add(s);
            suppress = false;
        }

        public void DoUndo()
        {
            if (Undo.Count == 0) return;
            var c = Undo[Undo.Count - 1];
            Undo.RemoveAt(Undo.Count - 1);
            suppress = true;
            foreach (var s in c.Added) if (Strokes.Contains(s)) Strokes.Remove(s);
            Strokes.Add(c.Removed);
            suppress = false;
            Redo.Add(c);
        }

        public void DoRedo()
        {
            if (Redo.Count == 0) return;
            var c = Redo[Redo.Count - 1];
            Redo.RemoveAt(Redo.Count - 1);
            suppress = true;
            foreach (var s in c.Removed) if (Strokes.Contains(s)) Strokes.Remove(s);
            Strokes.Add(c.Added);
            suppress = false;
            Undo.Add(c);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 업데이트로 다시 실행된 경우: 예전 프로그램이 완전히 끝날 때까지 기다림 (단축키·파일이 겹치지 않게)
            foreach (var a in args)
                if (a.StartsWith("--wait-pid="))
                {
                    int pid;
                    if (int.TryParse(a.Substring(11), out pid))
                        try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(10000); } catch { }
                }
            Updater.CleanupOld(); // 지난 업데이트에서 남은 예전 파일 정리
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var overlay = new OverlayWindow();
            app.MainWindow = overlay;
            overlay.Show();
            var toolbar = new ToolbarWindow(overlay);
            toolbar.Owner = overlay;
            toolbar.Show();
            overlay.PlaceToolbar(true);
            app.Run();
        }
    }

    // ================= 업데이트 (GitHub 릴리스) =================
    // GitHub 저장소의 최신 릴리스에 올린 Panseo.exe를 받아서, 실행 중인 파일과 바꾼 뒤 다시 실행
    static class Updater
    {
        // 새 버전을 올리는 GitHub 저장소 (소유자/저장소 이름)
        public const string Repo = "cos2104/panseo";

        public class Release { public string Tag, Notes, Url; }

        static string ExePath { get { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; } }
        static string NewPath { get { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Panseo_update.exe"); } }

        // "v27" → 27
        public static int Number(string tag)
        {
            var digits = new StringBuilder();
            foreach (char ch in tag ?? "") if (char.IsDigit(ch)) digits.Append(ch);
            int n;
            return int.TryParse(digits.ToString(), out n) ? n : 0;
        }

        static void UseTls12()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        }

        // 최신 릴리스 정보 (없거나 인터넷이 안 되면 null)
        public static Release CheckLatest()
        {
            try
            {
                UseTls12();
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.UserAgent = "Panseo-Updater";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 10000;
                string json;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var rel = new Release { Tag = d["tag_name"] as string, Notes = d.ContainsKey("body") ? (d["body"] as string ?? "") : "" };
                if (rel.Notes.Length > 400) rel.Notes = rel.Notes.Substring(0, 400) + "...";
                var assets = d["assets"] as System.Collections.IEnumerable;
                if (assets != null)
                    foreach (var o in assets)
                    {
                        var a = o as Dictionary<string, object>;
                        var name = a == null ? null : a["name"] as string;
                        if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { rel.Url = a["browser_download_url"] as string; break; }
                    }
                return rel.Url == null ? null : rel;
            }
            catch { return null; }
        }

        // 새 버전 받기 (성공하면 null, 실패하면 이유)
        public static string Download(Release r)
        {
            try
            {
                UseTls12();
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "Panseo-Updater");
                    wc.DownloadFile(r.Url, NewPath);
                }
                var head = new byte[2];
                using (var fs = File.OpenRead(NewPath))
                {
                    if (fs.Length < 10000 || fs.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z')
                        return "받은 파일이 올바른 프로그램이 아닙니다";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // 실행 중인 파일은 덮어쓸 수 없지만 이름은 바꿀 수 있으므로: 지금 파일 → .old, 새 파일 → 원래 이름, 새로 실행
        public static string Install()
        {
            string exe = ExePath, old = exe + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Copy(NewPath, exe); }
                catch { File.Move(old, exe); throw; } // 실패하면 원래대로 되돌림
                System.Diagnostics.Process.Start(exe, "--wait-pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static void CleanupOld()
        {
            try
            {
                string old = ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(NewPath)) File.Delete(NewPath);
            }
            catch { }
        }
    }

    // ================= Win32 =================
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000,
            WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int WM_HOTKEY = 0x0312, WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E;
        public const uint MOD_NOREPEAT = 0x4000;

        public delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize; public int style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
            public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
            public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
        }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination, rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        public delegate IntPtr LowLevelMouseProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int obj);
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }

    // ================= 설정 파일 =================
    static class Settings
    {
        static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Panseo");
        static readonly string FilePath = System.IO.Path.Combine(Dir, "settings.ini");

        static Dictionary<string, string> ReadAll()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return d;
        }

        public static string Get(string key)
        {
            string v;
            return ReadAll().TryGetValue(key, out v) ? v : null;
        }

        public static void Set(string key, string val)
        {
            var d = new Dictionary<string, string>();
            d[key] = val;
            SetMany(d);
        }

        public static void SetMany(Dictionary<string, string> values)
        {
            try
            {
                var d = ReadAll();
                foreach (var kv in values) d[kv.Key] = kv.Value;
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    // ================= 모니터 =================
    public class MonitorInfo
    {
        public WinForms.Screen Screen;
        public int Number;
        public bool Wacom;
        public string HardwareId = ""; // 모니터 하드웨어 ID (모니터를 켜고 꺼도 바뀌지 않음)
        public bool Touch;             // Windows에 펜·터치 장치가 연결된 화면 (와콤, 전자칠판 등)
        public string Device { get { return Screen.DeviceName; } }
        // 설정 저장용 키: 하드웨어 ID가 있으면 그것, 없으면 DISPLAYn 이름
        public string Key { get { return HardwareId != "" ? HardwareId : Device; } }
        public string Label
        {
            get
            {
                var b = Screen.Bounds;
                return Number + "번   " + b.Width + "×" + b.Height + (Screen.Primary ? "   주 모니터" : "") + (Wacom ? "   · 와콤" : (Touch ? "   · 터치" : ""));
            }
        }
        public string Short { get { return "모니터 " + Number + (Wacom ? " (와콤)" : (Touch ? " (터치)" : "")); } }
    }

    static class Monitors
    {
        // 번호는 Windows 디스플레이 번호(\\.\DISPLAYn의 n)를 사용 → 전자칠판 등을 켜고 꺼도 번호 유지
        public static List<MonitorInfo> GetAll()
        {
            var touchTargets = PenTouchDisplays();
            var list = new List<MonitorInfo>();
            foreach (var s in WinForms.Screen.AllScreens)
            {
                var m = new MonitorInfo { Screen = s };
                int n;
                string digits = s.DeviceName.Replace("\\\\.\\DISPLAY", "");
                m.Number = int.TryParse(digits, out n) ? n : list.Count + 1;
                ReadHardware(m, touchTargets);
                list.Add(m);
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }

        // 하드웨어 ID로 먼저 찾고, 없으면 DISPLAYn 이름으로 찾음
        public static MonitorInfo Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var all = GetAll();
            foreach (var m in all) if (m.HardwareId != "" && m.HardwareId == key) return m;
            foreach (var m in all) if (m.Device == key) return m;
            return null;
        }

        public static MonitorInfo Wacom()
        {
            foreach (var m in GetAll()) if (m.Wacom) return m;
            return null;
        }

        // 자동 선택: 와콤 → 다른 펜·터치 화면(전자칠판 등) → 주 모니터
        public static MonitorInfo DefaultPen()
        {
            var all = GetAll();
            foreach (var m in all) if (m.Wacom) return m;
            foreach (var m in all) if (m.Touch) return m;
            return Primary();
        }

        // Windows의 펜·터치 장치 ↔ 모니터 연결 정보
        static HashSet<string> PenTouchDisplays()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Wisp\Pen\Digimon"))
                    if (k != null)
                        foreach (var n in k.GetValueNames())
                        {
                            var v = k.GetValue(n) as string;
                            if (!string.IsNullOrEmpty(v)) set.Add(v);
                        }
            }
            catch { }
            return set;
        }

        public static MonitorInfo Primary()
        {
            var all = GetAll();
            foreach (var m in all) if (m.Screen.Primary) return m;
            return all[0];
        }

        // 모니터 하드웨어 ID 읽기. 제조사 코드 WAC = Wacom
        static void ReadHardware(MonitorInfo m, HashSet<string> touchTargets)
        {
            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            if (!Native.EnumDisplayDevices(m.Device, 0, ref dd, 0)) return;
            m.HardwareId = dd.DeviceID ?? "";
            string s = (m.HardwareId + " " + (dd.DeviceString ?? "")).ToUpperInvariant();
            m.Wacom = s.Contains("\\WAC") || s.Contains("WACOM") || s.Contains("CINTIQ");
            // 장치 인터페이스 경로(\\?\DISPLAY#...)로 펜·터치 연결 여부 확인
            var di = new Native.DISPLAY_DEVICE();
            di.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            if (Native.EnumDisplayDevices(m.Device, 0, ref di, 1) && !string.IsNullOrEmpty(di.DeviceID))
                m.Touch = touchTargets.Contains(di.DeviceID);
            if (m.Wacom) m.Touch = true;
        }
    }

    // ================= 라이브 확대 (DWM 썸네일) =================
    // 판서 화면 아래에 있는 창들의 실시간 썸네일(작업 표시줄 미리보기와 같은 기술)을 확대해서 직접 그림.
    // Windows 확대 API로 그린 화면은 Zoom 등 화면 공유·녹화에 안 잡히지만, 이 방식은 일반 창처럼 함께 공유됨.
    class Magnifier
    {
        static readonly Native.WndProc hostProc = HostProc;
        static IntPtr HostProc(IntPtr h, uint m, IntPtr w, IntPtr l) { return Native.DefWindowProc(h, m, w, l); }
        static bool classRegistered;
        IntPtr host;
        bool ready;
        System.Drawing.Rectangle dest;
        readonly List<IntPtr> wins = new List<IntPtr>();    // 썸네일로 그리는 원본 창 (아래 → 위 순서)
        readonly List<IntPtr> thumbs = new List<IntPtr>();
        readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        readonly Native.EnumWindowsProc enumProc;
        List<IntPtr> enumResult;

        public Magnifier() { enumProc = OnEnumWindow; }

        public bool Init()
        {
            if (ready) return true;
            try
            {
                IntPtr inst = Native.GetModuleHandle(null);
                if (!classRegistered)
                {
                    var wc = new Native.WNDCLASSEX();
                    wc.cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX));
                    wc.lpfnWndProc = hostProc;
                    wc.hInstance = inst;
                    wc.hbrBackground = Native.GetStockObject(4); // 검정 (썸네일이 없는 곳)
                    wc.lpszClassName = "PanseoZoomHost";
                    Native.RegisterClassEx(ref wc);
                    classRegistered = true;
                }
                // 마우스가 통과하도록(클릭 전달) 겹침 창 + 투명 스타일
                host = Native.CreateWindowEx(
                    Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                    "PanseoZoomHost", "판서 확대", Native.WS_POPUP,
                    0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (host == IntPtr.Zero) return false;
                Native.SetLayeredWindowAttributes(host, 0, 255, 2);
                ready = true;
            }
            catch { ready = false; }
            return ready;
        }

        public void Show(System.Drawing.Rectangle d, IntPtr[] exclude)
        {
            dest = d;
            Native.SetWindowPos(host, Native.HWND_TOPMOST, d.X, d.Y, d.Width, d.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void Hide()
        {
            ClearThumbs();
            if (host != IntPtr.Zero) Native.ShowWindow(host, 0);
        }

        // src: 확대할 원본 영역의 왼쪽 위(물리 픽셀), zoom: 배율
        public void Update(Native.RECT src, float zoom)
        {
            if (!ready) return;
            var list = VisibleWindows();
            if (!SameWindows(list))
            {
                ClearThumbs();
                foreach (var w in list)
                {
                    IntPtr t;
                    if (Native.DwmRegisterThumbnail(host, w, out t) == 0) { wins.Add(w); thumbs.Add(t); }
                }
            }
            for (int i = 0; i < wins.Count; i++)
            {
                Native.RECT r;
                if (!Native.GetWindowRect(wins[i], out r)) continue;
                var p = new Native.DWM_THUMBNAIL_PROPERTIES();
                p.dwFlags = 0x1 | 0x4 | 0x8; // 대상 위치, 불투명도, 보이기
                p.rcDestination = new Native.RECT
                {
                    left = (int)Math.Round((r.left - src.left) * zoom),
                    top = (int)Math.Round((r.top - src.top) * zoom),
                    right = (int)Math.Round((r.right - src.left) * zoom),
                    bottom = (int)Math.Round((r.bottom - src.top) * zoom)
                };
                p.opacity = 255;
                p.fVisible = true;
                Native.DwmUpdateThumbnailProperties(thumbs[i], ref p);
            }
        }

        // 판서 모니터에 보이는 다른 프로그램 창 목록 (아래 → 위). 판서 프로그램 자신의 창은 제외
        List<IntPtr> VisibleWindows()
        {
            enumResult = new List<IntPtr>();
            Native.EnumWindows(enumProc, IntPtr.Zero);
            enumResult.Reverse(); // EnumWindows는 위 → 아래 순서
            return enumResult;
        }

        bool OnEnumWindow(IntPtr h, IntPtr l)
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // 다른 가상 데스크톱 등 숨겨진 창
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r) || r.right <= r.left || r.bottom <= r.top) return true;
            if (r.right <= dest.Left || r.left >= dest.Right || r.bottom <= dest.Top || r.top >= dest.Bottom) return true;
            enumResult.Add(h);
            return true;
        }

        bool SameWindows(List<IntPtr> list)
        {
            if (list.Count != wins.Count) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] != wins[i]) return false;
            return true;
        }

        void ClearThumbs()
        {
            foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t);
            thumbs.Clear();
            wins.Clear();
        }

        public void Dispose()
        {
            ClearThumbs();
            if (host != IntPtr.Zero) Native.DestroyWindow(host);
            host = IntPtr.Zero;
            ready = false;
        }
    }

    // ================= 도구 모양 커서 =================
    static class PenCursors
    {
        static readonly Pen Outline = MakeOutline();
        static Pen MakeOutline()
        {
            var p = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), 1.2) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        static Geometry Poly(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, false);
            }
            g.Freeze();
            return g;
        }

        // 연필: 끝(펜촉)이 왼쪽 아래, 펜촉에 현재 색 표시
        public static Cursor Pen(Color c, double scale)
        {
            return Make(32, scale, 1.5, 30.5, dc =>
            {
                var tip = new SolidColorBrush(c);
                dc.PushTransform(new TranslateTransform(1.5, 30.5));
                dc.PushTransform(new RotateTransform(-45));
                dc.DrawRectangle(Brushes.White, Outline, new Rect(10, -4.5, 26, 9));
                dc.DrawRectangle(tip, null, new Rect(30, -3.9, 5.4, 7.8));
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF3, 0xD9, 0xA4)), Outline,
                    Poly(new Point(0, 0), new Point(10, -4.5), new Point(10, 4.5)));
                dc.DrawGeometry(tip, Outline, Poly(new Point(0, 0), new Point(4.5, -2), new Point(4.5, 2)));
                dc.Pop(); dc.Pop();
            });
        }

        public static Cursor Highlighter(Color c, double scale)
        {
            return Make(32, scale, 2, 30, dc =>
            {
                var tip = new SolidColorBrush(c);
                dc.PushTransform(new TranslateTransform(2, 30));
                dc.PushTransform(new RotateTransform(-45));
                dc.DrawRectangle(Brushes.White, Outline, new Rect(11, -6, 24, 12));
                dc.DrawRectangle(tip, null, new Rect(29, -5.4, 5.4, 10.8));
                dc.DrawGeometry(tip, Outline, Poly(new Point(0, -3.5), new Point(0, 3.5), new Point(11, 6), new Point(11, -6)));
                dc.Pop(); dc.Pop();
            });
        }

        public static Cursor Eraser(double scale)
        {
            return Make(32, scale, 4, 28, dc =>
            {
                dc.PushTransform(new TranslateTransform(4, 28));
                dc.PushTransform(new RotateTransform(-45));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0x8F, 0xB1)), Outline, new Rect(0, -6.5, 30, 13), 2, 2);
                dc.DrawRectangle(Brushes.White, Outline, new Rect(11, -6.5, 6, 13));
                dc.Pop(); dc.Pop();
            });
        }

        // 부분 지우개: 실제 지우는 범위만큼의 원
        public static Cursor Circle(double diameter, double scale)
        {
            double size = Math.Max(16, diameter + 6);
            double c = size / 2;
            return Make(size, scale, c, c, dc =>
            {
                var center = new Point(c, c);
                dc.DrawEllipse(null, new Pen(Brushes.White, 3), center, diameter / 2, diameter / 2);
                dc.DrawEllipse(null, new Pen(Brushes.Black, 1.2), center, diameter / 2, diameter / 2);
                dc.DrawEllipse(Brushes.Black, new Pen(Brushes.White, 1), center, 1.5, 1.5);
            });
        }

        static Cursor Make(double sizeDip, double scale, double hx, double hy, Action<DrawingContext> draw)
        {
            int px = Math.Min(250, (int)Math.Ceiling(sizeDip * scale));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                draw(dc);
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var pix = new byte[px * px * 4];
            rtb.CopyPixels(pix, px * 4, 0);
            int x = Math.Max(0, Math.Min(px - 1, (int)Math.Round(hx * scale)));
            int y = Math.Max(0, Math.Min(px - 1, (int)Math.Round(hy * scale)));
            return FromPixels(pix, px, x, y);
        }

        // 32비트 .cur 파일을 메모리에서 만들어 커서로 변환
        static Cursor FromPixels(byte[] pbgra, int size, int hx, int hy)
        {
            int maskStride = ((size + 31) / 32) * 4;
            int dataBytes = size * size * 4 + maskStride * size;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((short)0); w.Write((short)2); w.Write((short)1);
            w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)hx); w.Write((short)hy);
            w.Write(40 + dataBytes); w.Write(22);
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(dataBytes); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    int a = pbgra[i + 3];
                    if (a == 0) { w.Write(0); continue; }
                    w.Write((byte)Math.Min(255, pbgra[i] * 255 / a));
                    w.Write((byte)Math.Min(255, pbgra[i + 1] * 255 / a));
                    w.Write((byte)Math.Min(255, pbgra[i + 2] * 255 / a));
                    w.Write((byte)a);
                }
            w.Write(new byte[maskStride * size]);
            w.Flush();
            ms.Position = 0;
            return new Cursor(ms);
        }
    }

    // ================= 단축키 =================
    public class KeyCombo
    {
        public ModifierKeys Mods;
        public Key Key;
        public bool Global;

        public bool IsEmpty { get { return Key == Key.None; } }
        public KeyCombo Clone() { return new KeyCombo { Mods = Mods, Key = Key, Global = Global }; }
        public bool SameKeys(KeyCombo o) { return !IsEmpty && o != null && Key == o.Key && Mods == o.Mods; }
        public string Serialize() { return ((int)Mods) + ";" + Key + ";" + (Global ? "1" : "0"); }

        public static KeyCombo Parse(string s)
        {
            var p = s.Split(';');
            int m;
            Key k;
            if (p.Length < 3 || !int.TryParse(p[0], out m) || !Enum.TryParse(p[1], out k)) return null;
            return new KeyCombo { Mods = (ModifierKeys)m, Key = k, Global = p[2] == "1" };
        }

        public string Display
        {
            get
            {
                if (IsEmpty) return "";
                var sb = new StringBuilder();
                if ((Mods & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
                if ((Mods & ModifierKeys.Alt) != 0) sb.Append("Alt+");
                if ((Mods & ModifierKeys.Shift) != 0) sb.Append("Shift+");
                if ((Mods & ModifierKeys.Windows) != 0) sb.Append("Win+");
                sb.Append(KeyName(Key));
                return sb.ToString();
            }
        }

        static string KeyName(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return (k - Key.D0).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num" + (k - Key.NumPad0);
            switch (k)
            {
                case Key.OemPlus: return "=";
                case Key.OemMinus: return "-";
                case Key.Add: return "Num+";
                case Key.Subtract: return "Num-";
                case Key.Multiply: return "Num*";
                case Key.Divide: return "Num/";
                case Key.Escape: return "Esc";
                case Key.Delete: return "Del";
                case Key.Back: return "Backspace";
                case Key.Return: return "Enter";
                case Key.PageUp: return "PgUp";
                case Key.Next: return "PgDn";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.Oem1: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemOpenBrackets: return "[";
                case Key.Oem6: return "]";
                case Key.Oem5: return "\\";
                case Key.Oem3: return "`";
            }
            return k.ToString();
        }
    }

    public class HotkeyAction
    {
        public string Id, Name;
        public ModifierKeys Mods;
        public Key Key;
        public bool Global;
    }

    public static class HotkeyStore
    {
        static HotkeyAction A(string id, string name, ModifierKeys m, Key k, bool g)
        {
            return new HotkeyAction { Id = id, Name = name, Mods = m, Key = k, Global = g };
        }
        const ModifierKeys N = ModifierKeys.None, C = ModifierKeys.Control, CS = ModifierKeys.Control | ModifierKeys.Shift;

        public static readonly HotkeyAction[] Actions =
        {
            A("mouse_toggle", "판서 ↔ 마우스 모드 전환", CS, Key.D, true),
            A("pen", "펜", N, Key.P, false),
            A("highlighter", "형광펜", N, Key.H, false),
            A("text", "텍스트", N, Key.T, false),
            A("shape", "도형 (마지막으로 쓴 모양)", N, Key.S, false),
            A("shape_line", "도형: 직선", N, Key.None, false),
            A("shape_arrow", "도형: 화살표", N, Key.None, false),
            A("shape_rect", "도형: 네모", N, Key.None, false),
            A("shape_ellipse", "도형: 동그라미", N, Key.None, false),
            A("hide_ink", "필기 숨기기/보이기", N, Key.I, false),
            A("eraser", "획 지우개", N, Key.E, false),
            A("eraser_point", "부분 지우개", N, Key.R, false),
            A("mouse", "마우스 모드", N, Key.M, false),
            A("zoom", "라이브 확대 켜기/끄기", N, Key.Z, false),
            A("zoom_in", "확대 배율 +", N, Key.OemPlus, false),
            A("zoom_out", "확대 배율 -", N, Key.OemMinus, false),
            A("pan", "확대 화면 이동 도구", N, Key.V, false),
            A("size_down", "굵기 줄이기", N, Key.OemOpenBrackets, false),
            A("size_up", "굵기 키우기", N, Key.Oem6, false),
            A("size1", "굵기: 가늘게 (2)", N, Key.D1, false),
            A("size2", "굵기: 보통 (4)", N, Key.D2, false),
            A("size3", "굵기: 굵게 (8)", N, Key.D3, false),
            A("bg", "배경 바꾸기 (투명→흰판→칠판)", N, Key.B, false),
            A("undo", "되돌리기", C, Key.Z, false),
            A("redo", "다시 실행", C, Key.Y, false),
            A("clear", "전체 삭제", N, Key.Delete, false),
            A("capture", "화면 저장", C, Key.S, false),
            A("color1", "색: 검정", N, Key.None, false),
            A("color2", "색: 흰색", N, Key.None, false),
            A("color3", "색: 빨강", N, Key.None, false),
            A("color4", "색: 파랑", N, Key.None, false),
            A("color5", "색: 초록", N, Key.None, false),
            A("color6", "색: 노랑", N, Key.None, false),
            A("color7", "색: 주황", N, Key.None, false),
            A("color8", "색: 보라", N, Key.None, false),
            A("next_monitor", "다음 모니터로 이동", N, Key.None, false),
            A("scroll_follow", "스크롤 따라가기 켜기/끄기 (실험)", N, Key.None, false),
            A("toolbar", "도구 모음 숨기기/보이기", N, Key.None, false),
        };

        public static Dictionary<string, KeyCombo> Defaults()
        {
            var d = new Dictionary<string, KeyCombo>();
            foreach (var a in Actions) d[a.Id] = new KeyCombo { Mods = a.Mods, Key = a.Key, Global = a.Global };
            return d;
        }

        public static Dictionary<string, KeyCombo> Load()
        {
            var d = Defaults();
            foreach (var a in Actions)
            {
                string s = Settings.Get("key." + a.Id);
                if (s == null) continue;
                var c = KeyCombo.Parse(s);
                if (c != null) d[a.Id] = c;
            }
            return d;
        }

        public static void Save(Dictionary<string, KeyCombo> d)
        {
            var values = new Dictionary<string, string>();
            foreach (var kv in d) values["key." + kv.Key] = kv.Value.Serialize();
            Settings.SetMany(values);
        }
    }

    // ================= 판서 창 =================
    public class OverlayWindow : Window
    {
        public InkCanvas Ink;
        public ToolbarWindow Toolbar;
        public Tool CurrentTool = Tool.Pen;
        public Tool LastDrawTool = Tool.Pen;
        public Color PenColor = Color.FromRgb(0xE5, 0x39, 0x35);
        public Color HlColor = Color.FromRgb(0xFD, 0xD8, 0x35);
        public double PenSize = 4;
        public const string Version = "v28";
        public BoardBg Bg = BoardBg.Transparent;
        public event Action ToolStateChanged;
        public static readonly double[] PenSizes = { 2, 4, 8 };
        public static readonly Color[] Palette =
        {
            Colors.Black, Colors.White, Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0x1E, 0x63, 0xE9),
            Color.FromRgb(0x2E, 0x9E, 0x44), Color.FromRgb(0xFD, 0xD8, 0x35), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0x8E, 0x24, 0xAA)
        };

        public MonitorInfo DrawMon;
        public bool AutoMonitor = true; // true: 펜·터치 화면 자동 선택, false: 직접 고른 모니터
        public bool Zooming;
        public double Zoom = 2.0;
        double originX, originY; // 확대 원본 영역의 왼쪽 위 (물리 픽셀)
        readonly Magnifier mag = new Magnifier();
        readonly DispatcherTimer magTimer;
        bool panning;
        bool cursorWasInside = true;
        bool forwarding;      // 확대 화면 클릭을 실제 위치로 전달하는 중
        Point panStart;
        double panOx, panOy;

        readonly Layer normalLayer;
        EraserPreview eraserPreview;
        ScrollFollower follower;
        readonly List<Stroke> parked = new List<Stroke>(); // 스크롤로 영역 밖에 나가 숨겨 둔 판서
        TextEditor textEditor;
        ShapeDrawer shapeDrawer;
        Border board;                       // 흰판·칠판 배경
        public ShapeKind Shape = ShapeKind.Line;
        public bool InkHidden;              // 필기 숨기기

        public Dictionary<string, KeyCombo> Hotkeys;
        readonly Dictionary<string, Action> actions = new Dictionary<string, Action>();
        readonly List<int> registeredIds = new List<int>();

        Border toast;
        TextBlock toastText;
        DispatcherTimer toastTimer;
        IntPtr hwnd;
        string startupMessage;

        public OverlayWindow()
        {
            Title = "판서";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Left = 0; Top = 0; Width = 800; Height = 600;
            InputMethod.SetIsInputMethodEnabled(this, false);

            // 판서할 모니터: 저장된 설정 → 와콤 모니터 자동 감지 → 주 모니터
            // 판서할 모니터: 직접 고른 모니터 → 없으면 자동(와콤 → 터치 화면 → 주 모니터)
            string savedMon = Settings.Get("monitor");
            AutoMonitor = savedMon == null || savedMon == "auto";
            DrawMon = AutoMonitor ? null : Monitors.Find(savedMon);
            if (DrawMon == null) DrawMon = Monitors.DefaultPen();
            if (savedMon == null && Monitors.GetAll().Count > 1)
            {
                startupMessage = DrawMon.Short + "에서 판서합니다 (자동 선택) · 도구 모음의 '화면'에서 바꿀 수 있어요";
                Settings.Set("monitor", "auto");
            }
            double savedSize;
            if (double.TryParse(Settings.Get("pensize") ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out savedSize))
                PenSize = ClampSize(savedSize);

            var grid = new Grid();
            // 흰판·칠판 배경은 판서 층과 따로 두어, 필기를 숨겨도 배경은 남도록
            board = new Border();
            grid.Children.Add(board);
            Ink = new InkCanvas();
            ConfigureInk(Ink);
            normalLayer = new Layer();
            Ink.Strokes = normalLayer.Strokes;
            grid.Children.Add(Ink);
            // 필기를 숨긴 상태에서 쓰기 시작하면 자동으로 다시 보이게
            // (텍스트·도형 처리기가 입력을 가져가기 전에 먼저 등록)
            Ink.PreviewStylusDown += (s, e) => UnhideForDrawing();
            Ink.PreviewMouseLeftButtonDown += (s, e) => UnhideForDrawing();
            eraserPreview = new EraserPreview(grid, Ink);
            textEditor = new TextEditor(grid, Ink, this);
            shapeDrawer = new ShapeDrawer(grid, Ink, this);

            Ink.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (Zooming && CurrentTool == Tool.Pan) { BeginPan(e.GetPosition(Ink)); e.Handled = true; }
                else if (Zooming && CurrentTool == Tool.Mouse) e.Handled = true; // 뗄 때 클릭 전달
            };
            Ink.PreviewMouseRightButtonDown += (s, e) => { if (Zooming) { BeginPan(e.GetPosition(Ink)); e.Handled = true; } };
            Ink.PreviewMouseMove += (s, e) =>
            {
                if (!panning) return;
                var p = e.GetPosition(Ink);
                double sc = DpiScale;
                SetView(Zoom, panOx - (p.X - panStart.X) * sc / Zoom, panOy - (p.Y - panStart.Y) * sc / Zoom);
            };
            Ink.PreviewMouseUp += (s, e) =>
            {
                var p = e.GetPosition(Ink);
                if (panning)
                {
                    panning = false;
                    Ink.ReleaseMouseCapture();
                    e.Handled = true;
                    // 확대 중 마우스 도구: 오른쪽을 끌지 않고 뗐으면 오른쪽 클릭 전달
                    if (Zooming && CurrentTool == Tool.Mouse && e.ChangedButton == MouseButton.Right && (p - panStart).Length < 5)
                        ForwardInput(p, 0x0008, 0x0010, 0);
                    return;
                }
                if (Zooming && CurrentTool == Tool.Mouse && e.ChangedButton == MouseButton.Left)
                {
                    ForwardInput(p, 0x0002, 0x0004, 0);
                    e.Handled = true;
                }
            };
            Ink.PreviewMouseWheel += (s, e) =>
            {
                if (!Zooming) return;
                var p = e.GetPosition(Ink);
                // 확대 중 마우스 도구: 휠은 화면 스크롤, Ctrl+휠은 배율
                if (CurrentTool == Tool.Mouse && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
                {
                    ForwardWheel(p, e.Delta);
                    e.Handled = true;
                    return;
                }
                double sc = DpiScale;
                ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, p.X * sc, p.Y * sc);
                e.Handled = true;
            };

            toastText = new TextBlock { Foreground = Brushes.White, FontSize = 15, FontFamily = new FontFamily("Malgun Gothic") };
            toast = new Border
            {
                Child = toastText,
                Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x22, 0x27)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 10, 18, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 80),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(toast);
            Content = grid;

            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            toastTimer.Tick += (s, e) => { toast.Visibility = Visibility.Collapsed; toastTimer.Stop(); };
            magTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            magTimer.Tick += (s, e) => { EdgePan(); UpdateMag(); };

            Hotkeys = HotkeyStore.Load();
            BuildActions();
            ApplyBg();
            PreviewKeyDown += (s, e) => HandleKey(e);
            SourceInitialized += OnSourceInitialized;
            ContentRendered += (s, e) =>
            {
                ApplyBounds();
                ApplyTool();
                if (Settings.Get("scrollfollow") == "1") { follower = new ScrollFollower(this); follower.Start(); SetWheelHook(true); Notify(); }
                if (startupMessage != null) ShowToast(startupMessage);
            };
            Closed += (s, e) =>
            {
                Settings.Set("pensize", PenSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                UnregisterGlobalHotkeys();
                if (follower != null) follower.Stop();
                SetWheelHook(false);
                magTimer.Stop();
                mag.Dispose();
            };
        }

        // 펜·터치로 바로 그려지도록 Windows 기본 터치 동작(길게 누르기=오른쪽 클릭 등) 끄기
        public static void ConfigureInk(InkCanvas ink)
        {
            ink.EditingModeInverted = InkCanvasEditingMode.EraseByStroke; // 와콤 펜 뒤쪽 지우개
            Stylus.SetIsPressAndHoldEnabled(ink, false);
            Stylus.SetIsFlicksEnabled(ink, false);
            Stylus.SetIsTapFeedbackEnabled(ink, false);
            Stylus.SetIsTouchFeedbackEnabled(ink, false);
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            ApplyBounds();
            RegisterGlobalHotkeys();
            ApplyTool();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int i = wParam.ToInt32() - 1000;
                if (i >= 0 && i < HotkeyStore.Actions.Length)
                {
                    Run(HotkeyStore.Actions[i].Id);
                    handled = true;
                }
            }
            else if (msg == Native.WM_DPICHANGED)
            {
                // WPF가 창 크기를 바꾼 뒤 다시 모니터 전체로 맞춤
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { ApplyBounds(); ApplyTool(); }));
            }
            else if (msg == Native.WM_DISPLAYCHANGE)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OnDisplayChanged));
            }
            return IntPtr.Zero;
        }

        // 모니터를 켜거나 끄면: 쓰던 모니터 → 와콤 → 주 모니터 순으로 다시 선택
        void OnDisplayChanged()
        {
            if (Zooming) StopZoom();
            DrawMon = (AutoMonitor ? null : Monitors.Find(Settings.Get("monitor"))) ?? Monitors.DefaultPen();
            PlaceToolbar(false);
            ApplyBounds();
            Notify();
        }

        void Notify()
        {
            if (ToolStateChanged != null) ToolStateChanged();
        }

        double DpiScale
        {
            get
            {
                var s = PresentationSource.FromVisual(this);
                return s != null && s.CompositionTarget != null ? s.CompositionTarget.TransformToDevice.M11 : 1.0;
            }
        }

        void SetClickThrough(bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            ex = on ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        }

        // ---------- 모니터 ----------
        void ApplyBounds()
        {
            if (hwnd == IntPtr.Zero) return;
            var b = DrawMon.Screen.Bounds;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE);
        }

        public void PlaceToolbar(bool force)
        {
            if (Toolbar == null) return;
            var th = new WindowInteropHelper(Toolbar).Handle;
            if (th == IntPtr.Zero) return;
            if (!force && WinForms.Screen.FromHandle(th).DeviceName == DrawMon.Device) return;
            var wa = DrawMon.Screen.WorkingArea;
            Native.SetWindowPos(th, IntPtr.Zero, wa.X + 16, wa.Y + 60, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public void SetDrawMonitor(MonitorInfo m)
        {
            CancelSelect();
            bool wasZoom = Zooming;
            if (wasZoom) StopZoom();
            DrawMon = m;
            AutoMonitor = false;
            Settings.Set("monitor", m.Key);
            ApplyBounds();
            PlaceToolbar(false);
            if (wasZoom) StartZoom();
            Notify();
            ShowToast(m.Short + "에서 판서합니다");
        }

        public void SetAutoMonitor()
        {
            var m = Monitors.DefaultPen();
            SetDrawMonitor(m);
            AutoMonitor = true;
            Settings.Set("monitor", "auto");
            Notify();
        }

        public void NextMonitor()
        {
            var all = Monitors.GetAll();
            int idx = 0;
            for (int i = 0; i < all.Count; i++) if (all[i].Device == DrawMon.Device) idx = i;
            SetDrawMonitor(all[(idx + 1) % all.Count]);
        }

        // ---------- 도구 ----------
        public void SetTool(Tool t)
        {
            if (t == Tool.Mouse) CancelSelect();
            if (t == Tool.Pan && !Zooming && !StartZoom()) return;
            CurrentTool = t;
            if (t == Tool.Pen || t == Tool.Highlighter) LastDrawTool = t;
            if (t == Tool.Mouse && Bg != BoardBg.Transparent)
            {
                Bg = BoardBg.Transparent;
                ApplyBg();
            }
            ApplyTool();
            if (t != Tool.Mouse) Activate();
        }

        public void ToggleMouse()
        {
            SetTool(CurrentTool == Tool.Mouse ? LastDrawTool : Tool.Mouse);
        }

        public void SetColor(Color c)
        {
            if (CurrentTool == Tool.Text || CurrentTool == Tool.Shape) { PenColor = c; ApplyTool(); return; }
            Tool target = CurrentTool == Tool.Pen || CurrentTool == Tool.Highlighter ? CurrentTool : LastDrawTool;
            if (target == Tool.Highlighter) HlColor = c; else PenColor = c;
            SetTool(target);
        }

        public Color ActiveColor
        {
            get
            {
                if (CurrentTool == Tool.Text || CurrentTool == Tool.Shape) return PenColor;
                Tool target = CurrentTool == Tool.Pen || CurrentTool == Tool.Highlighter ? CurrentTool : LastDrawTool;
                return target == Tool.Highlighter ? HlColor : PenColor;
            }
        }

        public const double MinSize = 1, MaxSize = 40;

        static double ClampSize(double v)
        {
            return Math.Max(MinSize, Math.Min(MaxSize, Math.Round(v * 2) / 2));
        }

        public void SetPenSize(double v)
        {
            PenSize = ClampSize(v);
            if (CurrentTool == Tool.Mouse) SetTool(LastDrawTool); else ApplyTool();
        }

        // 가는 쪽은 촘촘하게, 굵은 쪽은 크게 한 단계씩
        public void StepPenSize(int dir)
        {
            double v = PenSize;
            double step = v < 4 || (dir < 0 && v <= 4) ? 0.5 : (v < 10 || (dir < 0 && v <= 10) ? 1 : 2);
            SetPenSize(v + dir * step);
        }

        public void SetBg(BoardBg b)
        {
            if (b != BoardBg.Transparent && Zooming) StopZoom();
            Bg = b;
            if (b == BoardBg.Chalk && PenColor == Colors.Black) PenColor = Colors.White;
            if (b == BoardBg.White && PenColor == Colors.White) PenColor = Colors.Black;
            ApplyBg();
            if (CurrentTool == Tool.Mouse && b != BoardBg.Transparent) SetTool(LastDrawTool);
            else ApplyTool();
        }

        public void ToggleToolbar()
        {
            if (Toolbar == null) return;
            if (Toolbar.IsVisible) Toolbar.Hide(); else Toolbar.Show();
        }

        Brush BgBrush()
        {
            switch (Bg)
            {
                case BoardBg.White: return Brushes.White;
                case BoardBg.Chalk: return new SolidColorBrush(Color.FromRgb(0x1F, 0x3B, 0x2D));
                // 알파 1: 눈에는 안 보이지만 펜 입력은 받음
                default: return new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            }
        }

        void ApplyBg()
        {
            // 배경색은 판서 층 뒤의 board에, 판서 층은 항상 입력만 받는 거의 투명한 배경
            board.Background = Bg == BoardBg.Transparent ? null : BgBrush();
            Ink.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }

        // ---------- 도형 ----------
        public void SetShape(ShapeKind kind)
        {
            Shape = kind;
            SetTool(Tool.Shape);
        }

        // ---------- 필기 숨기기 ----------
        public void ToggleHideInk()
        {
            SetInkHidden(!InkHidden);
            ShowToast(InkHidden ? "필기 숨김 · 다시 누르거나 펜으로 쓰면 보입니다" : "필기 보이기");
        }

        void SetInkHidden(bool hidden)
        {
            InkHidden = hidden;
            Ink.Opacity = hidden ? 0 : 1; // 투명하게만 해서 입력·스크롤 따라가기는 그대로 동작
            Notify();
        }

        void UnhideForDrawing()
        {
            if (InkHidden && CurrentTool != Tool.Mouse && CurrentTool != Tool.Pan) SetInkHidden(false);
        }

        void ApplyTool()
        {
            // 확대 중 마우스 도구는 클릭을 받아서 실제 위치로 전달하므로 통과시키지 않음
            SetClickThrough(CurrentTool == Tool.Mouse && !Zooming);
            ConfigureTool(Ink, eraserPreview, DpiScale, CurrentTool);
            Notify();
        }

        public double EraserDiameter { get { return Math.Min(PenSize * 5 + 8, 120); } }
        public double TextFontSize { get { return Math.Round(12 + PenSize * 3); } }

        void ConfigureTool(InkCanvas ink, EraserPreview ep, double sc, Tool tool)
        {
            double size = PenSize;
            ink.UseCustomCursor = true;
            ep.Set(false, 0);
            switch (tool)
            {
                case Tool.Mouse:
                    ink.EditingMode = InkCanvasEditingMode.None;
                    ink.UseCustomCursor = false;
                    break;
                case Tool.Pen:
                    ink.DefaultDrawingAttributes = new DrawingAttributes
                    {
                        Color = PenColor, Width = size, Height = size,
                        FitToCurve = true, IgnorePressure = false, StylusTip = StylusTip.Ellipse
                    };
                    ink.EditingMode = InkCanvasEditingMode.Ink;
                    ink.Cursor = PenCursors.Pen(PenColor, sc);
                    break;
                case Tool.Highlighter:
                    ink.DefaultDrawingAttributes = new DrawingAttributes
                    {
                        Color = HlColor, Width = size * 2 + 2, Height = size * 4 + 10,
                        IsHighlighter = true, FitToCurve = true, IgnorePressure = true, StylusTip = StylusTip.Rectangle
                    };
                    ink.EditingMode = InkCanvasEditingMode.Ink;
                    ink.Cursor = PenCursors.Highlighter(HlColor, sc);
                    break;
                case Tool.EraseStroke:
                    ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                    ink.Cursor = PenCursors.Eraser(sc);
                    break;
                case Tool.ErasePoint:
                    double es = EraserDiameter;
                    ink.EraserShape = new EllipseStylusShape(es, es);
                    ink.EditingMode = InkCanvasEditingMode.None; // 지우개 크기 반영을 위해 재설정
                    ink.EditingMode = InkCanvasEditingMode.EraseByPoint;
                    // 커서는 작은 점만, 지우는 범위는 판서 화면 위에 실제 크기로 그림
                    ink.Cursor = PenCursors.Circle(3, sc);
                    ep.Set(true, es);
                    break;
                case Tool.Pan:
                    ink.EditingMode = InkCanvasEditingMode.None;
                    ink.Cursor = Cursors.SizeAll;
                    break;
                case Tool.Text:
                    ink.EditingMode = InkCanvasEditingMode.None;
                    ink.Cursor = Cursors.IBeam;
                    break;
                case Tool.Shape:
                    ink.EditingMode = InkCanvasEditingMode.None;
                    ink.Cursor = Cursors.Cross;
                    break;
            }
        }

        // ---------- 스크롤 따라가기 (실험) ----------
        public bool ScrollFollow { get { return follower != null && follower.Running; } }

        // 판서 화면 위에서 돈 휠 양 (120 = 한 칸). 스크롤 따라가기가 한 화면 이상 점프를 채울 때 사용
        public int WheelAccum;
        Native.LowLevelMouseProc wheelProc;
        IntPtr wheelHook;

        void SetWheelHook(bool on)
        {
            if (on && wheelHook == IntPtr.Zero)
            {
                wheelProc = WheelHookProc;
                wheelHook = Native.SetWindowsHookEx(14, wheelProc, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL
            }
            else if (!on && wheelHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(wheelHook);
                wheelHook = IntPtr.Zero;
            }
        }

        IntPtr WheelHookProc(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && w.ToInt32() == 0x020A) // WM_MOUSEWHEEL
            {
                var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                if (DrawMon.Screen.Bounds.Contains(info.pt.X, info.pt.Y))
                    System.Threading.Interlocked.Add(ref WheelAccum, (short)((info.mouseData >> 16) & 0xFFFF));
            }
            return Native.CallNextHookEx(wheelHook, code, w, l);
        }

        public void ToggleScrollFollow()
        {
            if (ScrollFollow)
            {
                follower.Stop();
                SetWheelHook(false);
                UnparkAll();
                ShowToast("스크롤 따라가기 꺼짐");
            }
            else
            {
                if (follower == null) follower = new ScrollFollower(this);
                follower.Start();
                SetWheelHook(true);
                ShowToast("스크롤 따라가기(실험) 켜짐 · 마우스 모드에서 스크롤하면 판서가 따라갑니다");
            }
            Settings.Set("scrollfollow", ScrollFollow ? "1" : "0");
            Notify();
        }

        // 백그라운드 감지용 정보: 모니터 영역과 비교에서 뺄 영역(판서, 도구 모음, 알림). 감지하면 안 될 때는 null
        internal ScrollSnapshot TakeScrollSnapshot()
        {
            if (Zooming || Selecting || !IsVisible) return null;
            if (CurrentTool != Tool.Mouse && (Native.GetAsyncKeyState(0x01) & 0x8000) != 0) return null; // 필기 중
            var b = DrawMon.Screen.Bounds;
            var snap = new ScrollSnapshot { Bounds = b, Version = normalLayer.Version };
            double sc = DpiScale;
            foreach (var s in Ink.Strokes)
            {
                var r = s.GetBounds();
                r.Inflate(6, 6);
                snap.Masks.Add(new System.Drawing.Rectangle((int)(r.X * sc), (int)(r.Y * sc), (int)(r.Width * sc) + 2, (int)(r.Height * sc) + 2));
            }
            if (Toolbar != null && Toolbar.IsVisible)
            {
                Native.RECT tr;
                if (Native.GetWindowRect(new WindowInteropHelper(Toolbar).Handle, out tr))
                    snap.Masks.Add(new System.Drawing.Rectangle(tr.left - b.X - 4, tr.top - b.Y - 4, tr.right - tr.left + 8, tr.bottom - tr.top + 8));
            }
            if (toast.Visibility == Visibility.Visible)
            {
                var r = toast.TransformToAncestor(this).TransformBounds(new Rect(toast.RenderSize));
                snap.Masks.Add(new System.Drawing.Rectangle((int)(r.X * sc) - 4, (int)(r.Y * sc) - 4, (int)(r.Width * sc) + 8, (int)(r.Height * sc) + 8));
            }
            return snap;
        }

        // 화면 영역 [xMin~xMax, yTop~yBot](물리 픽셀)의 내용이 dyPx만큼 움직였을 때 그 안의 판서를 함께 이동
        // 영역 밖으로 완전히 나간 판서는 숨겨 두었다가(parked) 다시 들어오면 보이게 함
        internal void ApplyScroll(int dyPx, int yTop, int yBot, int xMin, int xMax)
        {
            double sc = DpiScale;
            var m = new Matrix(1, 0, 0, 1, 0, dyPx / sc);
            var list = new List<Stroke>(Ink.Strokes);
            list.AddRange(parked);
            foreach (var s in list)
            {
                var r = s.GetBounds();
                double cx = (r.Left + r.Right) / 2 * sc, top = r.Top * sc, bot = r.Bottom * sc;
                if (cx < xMin || cx > xMax) continue;
                bool isParked = parked.Contains(s);
                const double Margin = 40; // 영역 가장자리 판서도 포함 (가장자리 흰 여백은 바뀐 곳으로 안 잡힘)
                if (!isParked && (bot < yTop - Margin || top > yBot + Margin)) continue; // 고정된 곳(주소창 등)의 판서
                s.Transform(m, false);
                bool nowInside = bot + dyPx >= yTop && top + dyPx <= yBot;
                if (!nowInside && !isParked) { normalLayer.RemoveSilently(s); parked.Add(s); }
                else if (nowInside && isParked) { parked.Remove(s); normalLayer.AddSilently(s); }
            }
        }

        void UnparkAll()
        {
            foreach (var s in parked) normalLayer.AddSilently(s);
            parked.Clear();
        }

        // ---------- 라이브 확대 ----------
        public int DestW { get { return DrawMon.Screen.Bounds.Width; } }
        public int DestH { get { return DrawMon.Screen.Bounds.Height; } }
        ZoomSelectWindow selectWin;
        public bool Selecting { get { return selectWin != null; } }

        // 확대 원본 = 판서하는 모니터
        System.Drawing.Rectangle SrcBounds { get { return DrawMon.Screen.Bounds; } }

        public void ToggleZoom()
        {
            if (Zooming) StopZoom();
            else if (Selecting) CancelSelect();
            else BeginZoomSelect();
        }

        // 확대할 곳 고르기: 네모 테두리가 마우스를 따라다니고, 클릭한 곳을 확대
        public void BeginZoomSelect()
        {
            if (Zooming || Selecting) return;
            if (Zoom < 1.25) Zoom = 2.0;
            selectWin = new ZoomSelectWindow(this, SrcBounds);
            selectWin.Picked = (x, y) => StartZoomAt(x, y);
            selectWin.Closed += (s, e) => { selectWin = null; Notify(); };
            selectWin.Show();
            Notify();
        }

        public void CancelSelect()
        {
            if (selectWin != null) selectWin.Close();
        }

        public void AdjustZoomLevel(double factor)
        {
            Zoom = Math.Max(1.25, Math.Min(8.0, Zoom * factor));
            Notify();
        }

        // 마우스 위치(원본 화면 위에 있으면)를 중심으로 바로 확대
        public bool StartZoom()
        {
            var src = SrcBounds;
            Native.POINT p;
            Native.GetCursorPos(out p);
            double cx = src.X + src.Width / 2.0, cy = src.Y + src.Height / 2.0;
            if (src.Contains(p.X, p.Y)) { cx = p.X; cy = p.Y; }
            return StartZoomAt(cx, cy);
        }

        public bool StartZoomAt(double cx, double cy)
        {
            if (Zooming) return true;
            CancelSelect();
            if (!mag.Init())
            {
                ShowToast("라이브 확대를 시작할 수 없습니다");
                return false;
            }
            if (Bg != BoardBg.Transparent) { Bg = BoardBg.Transparent; ApplyBg(); }

            originX = cx - DestW / Zoom / 2;
            originY = cy - DestH / Zoom / 2;
            ClampOrigin();

            cursorWasInside = true;
            // 기존 판서도 화면과 함께 확대해서 보여 줌
            var dm = DrawMon.Screen.Bounds;
            TransformBetween(normalLayer, 1.0, dm.X, dm.Y, Zoom, originX, originY);
            Zooming = true;

            var exclude = new List<IntPtr> { hwnd };
            if (Toolbar != null) exclude.Add(new WindowInteropHelper(Toolbar).Handle);
            mag.Show(DrawMon.Screen.Bounds, exclude.ToArray());
            UpdateMag();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            magTimer.Start();
            ApplyTool();
            Activate();
            return true;
        }

        public void StopZoom()
        {
            if (!Zooming) return;
            Zooming = false;
            panning = false;
            magTimer.Stop();
            mag.Hide();
            // 확대 중에 쓴 판서를 원래 크기·위치로 되돌려 남겨 둠
            var dm = DrawMon.Screen.Bounds;
            TransformBetween(normalLayer, Zoom, originX, originY, 1.0, dm.X, dm.Y);
            if (CurrentTool == Tool.Pan) CurrentTool = LastDrawTool;
            ApplyTool();
        }

        void ClampOrigin()
        {
            var s = SrcBounds;
            double vw = DestW / Zoom, vh = DestH / Zoom;
            originX = vw >= s.Width ? s.X : Math.Max(s.X, Math.Min(originX, s.Right - vw));
            originY = vh >= s.Height ? s.Y : Math.Max(s.Y, Math.Min(originY, s.Bottom - vh));
        }

        // 확대/이동 시 필기도 화면 내용에 붙어서 함께 움직이도록 변환
        void SetView(double newZoom, double ox, double oy)
        {
            double oz = Zoom, ox0 = originX, oy0 = originY;
            Zoom = Math.Max(1.0, Math.Min(8.0, newZoom));
            originX = ox;
            originY = oy;
            ClampOrigin();
            double sc = DpiScale;
            double k = Zoom / oz;
            var m = new Matrix(k, 0, 0, k, (ox0 - originX) * Zoom / sc, (oy0 - originY) * Zoom / sc);
            if (!m.IsIdentity) TransformLayer(normalLayer, m);
            UpdateMag();
            Notify();
        }

        // 보기 상태(배율, 원본 왼쪽 위) A에서 B로 바뀔 때 판서 좌표 변환
        void TransformBetween(Layer l, double z0, double ox0, double oy0, double z1, double ox1, double oy1)
        {
            double sc = DpiScale;
            double k = z1 / z0;
            var m = new Matrix(k, 0, 0, k, (ox0 - ox1) * z1 / sc, (oy0 - oy1) * z1 / sc);
            if (!m.IsIdentity) TransformLayer(l, m);
        }

        void TransformLayer(Layer l, Matrix m)
        {
            var set = new HashSet<Stroke>(l.Strokes);
            foreach (var s in parked) set.Add(s); // 스크롤로 숨겨 둔 판서도 함께 변환
            foreach (var list in new[] { l.Undo, l.Redo })
                foreach (var c in list)
                {
                    foreach (var s in c.Added) set.Add(s);
                    foreach (var s in c.Removed) set.Add(s);
                }
            foreach (var s in set) s.Transform(m, true);
        }

        void ZoomAt(double factor, double dx, double dy)
        {
            double px = originX + dx / Zoom, py = originY + dy / Zoom;
            double nz = Math.Max(1.0, Math.Min(8.0, Zoom * factor));
            SetView(nz, px - dx / nz, py - dy / nz);
        }

        public void ZoomStep(double factor)
        {
            if (Selecting) { AdjustZoomLevel(factor); return; }
            if (!Zooming) { BeginZoomSelect(); return; }
            ZoomAt(factor, DestW / 2.0, DestH / 2.0);
        }

        void PanBy(double fx, double fy)
        {
            SetView(Zoom, originX + fx * DestW / Zoom, originY + fy * DestH / Zoom);
        }

        // 휠은 커서를 움직이지 않고, 실제 위치 아래 창에 스크롤 메시지를 직접 보냄
        void ForwardWheel(Point d, int delta)
        {
            double sc = DpiScale;
            var pt = new Native.POINT
            {
                X = (int)Math.Round(originX + d.X * sc / Zoom),
                Y = (int)Math.Round(originY + d.Y * sc / Zoom)
            };
            SetClickThrough(true); // 판서 창을 잠시 통과시켜 그 아래 창을 찾음
            IntPtr target = Native.WindowFromPoint(pt);
            SetClickThrough(CurrentTool == Tool.Mouse && !Zooming);
            if (target == IntPtr.Zero || target == hwnd) return;
            var w = new IntPtr((delta & 0xFFFF) << 16);
            var l = new IntPtr(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            Native.PostMessage(target, 0x020A, w, l); // WM_MOUSEWHEEL
        }

        // 확대 화면에서 누른 위치(d)를 실제 화면 위치로 바꿔 클릭을 전달
        void ForwardInput(Point d, uint down, uint up, int wheel)
        {
            if (forwarding) return;
            double sc = DpiScale;
            int px = (int)Math.Round(originX + d.X * sc / Zoom);
            int py = (int)Math.Round(originY + d.Y * sc / Zoom);
            Native.POINT back;
            Native.GetCursorPos(out back);
            forwarding = true;
            SetClickThrough(true);
            Native.SetCursorPos(px, py);
            if (wheel != 0) Native.mouse_event(0x0800, 0, 0, wheel, IntPtr.Zero);
            else { Native.mouse_event(down, 0, 0, 0, IntPtr.Zero); Native.mouse_event(up, 0, 0, 0, IntPtr.Zero); }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Native.SetCursorPos(back.X, back.Y);
                SetClickThrough(CurrentTool == Tool.Mouse && !Zooming);
                forwarding = false;
            };
            timer.Start();
        }

        void BeginPan(Point p)
        {
            panning = true;
            panStart = p;
            panOx = originX;
            panOy = originY;
            Ink.CaptureMouse();
        }

        // 마우스(또는 떠 있는 펜)가 확대 화면 가장자리를 넘어가면 그만큼 확대 영역을 이동
        void EdgePan()
        {
            if (!Zooming || panning || forwarding) return;
            // 필기 중(펜 접촉/버튼 누름)에는 움직이지 않음
            if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0) return;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            var b = DrawMon.Screen.Bounds;
            bool inside = b.Contains(p.X, p.Y);
            if (!inside && !cursorWasInside) return; // 이미 다른 모니터에서 작업 중
            int edge = Math.Max(4, (int)(DestW * 0.004)); // 가장자리 판정 폭 (4K에서 약 15픽셀)
            const int leave = 150;    // 이보다 멀리 한 번에 넘어가면 다른 모니터로 의도적으로 이동한 것으로 봄
            if (p.X < b.Left - leave || p.X >= b.Right + leave || p.Y < b.Top - leave || p.Y >= b.Bottom + leave)
            {
                cursorWasInside = false;
                return;
            }
            double speed = Math.Max(8, DestW * 0.006); // 화면 끝에 붙어 있을 때 한 번에 이동할 양
            double dx = 0, dy = 0;
            int cx = p.X, cy = p.Y;
            if (p.X < b.Left) { dx = p.X - b.Left; cx = b.Left + edge + 1; }
            else if (p.X >= b.Right) { dx = p.X - (b.Right - 1); cx = b.Right - 2 - edge; }
            else if (p.X < b.Left + edge) dx = -speed;
            else if (p.X >= b.Right - edge) dx = speed;
            if (p.Y < b.Top) { dy = p.Y - b.Top; cy = b.Top + edge + 1; }
            else if (p.Y >= b.Bottom) { dy = p.Y - (b.Bottom - 1); cy = b.Bottom - 2 - edge; }
            else if (p.Y < b.Top + edge) dy = -speed;
            else if (p.Y >= b.Bottom - edge) dy = speed;
            cursorWasInside = true;
            if (dx == 0 && dy == 0) return;
            SetView(Zoom, originX + dx / Zoom, originY + dy / Zoom);
            if (cx != p.X || cy != p.Y) Native.SetCursorPos(cx, cy);
        }

        void UpdateMag()
        {
            if (!Zooming) return;
            var r = new Native.RECT();
            r.left = (int)Math.Round(originX);
            r.top = (int)Math.Round(originY);
            r.right = r.left + (int)Math.Round(DestW / Zoom);
            r.bottom = r.top + (int)Math.Round(DestH / Zoom);
            mag.Update(r, (float)Zoom);
        }

        // ---------- 되돌리기 ----------
        public void Undo()
        {
            if (InkHidden) SetInkHidden(false); // 숨긴 채로 바뀌면 알 수 없으므로 보이게
            normalLayer.DoUndo();
        }

        public void Redo()
        {
            if (InkHidden) SetInkHidden(false);
            normalLayer.DoRedo();
        }

        // 전체 삭제 (되돌리기 가능)
        public void ClearAll()
        {
            UnparkAll(); // 숨겨 둔 판서도 함께 지우고, 되돌리면 함께 돌아오도록
            if (Ink.Strokes.Count > 0) Ink.Strokes.Clear();
        }

        // ---------- 화면 캡처 ----------
        public async void Capture()
        {
            if (Toolbar != null) Toolbar.Hide();
            await Task.Delay(250);
            string path = null;
            string error = null;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "판서");
                Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, "판서_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                var b = DrawMon.Screen.Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            if (Toolbar != null) Toolbar.Show();
            ShowToast(error == null ? "저장됨: " + path : "저장 실패: " + error);
        }

        public void ShowToast(string msg)
        {
            toastText.Text = msg;
            toast.Visibility = Visibility.Visible;
            toastTimer.Stop();
            toastTimer.Start();
        }

        // ---------- 단축키 ----------
        void BuildActions()
        {
            actions["mouse_toggle"] = ToggleMouse;
            actions["pen"] = () => SetTool(Tool.Pen);
            actions["highlighter"] = () => SetTool(Tool.Highlighter);
            actions["eraser"] = () => SetTool(Tool.EraseStroke);
            actions["eraser_point"] = () => SetTool(Tool.ErasePoint);
            actions["mouse"] = () => SetTool(Tool.Mouse);
            actions["zoom"] = ToggleZoom;
            actions["zoom_in"] = () => ZoomStep(1.25);
            actions["zoom_out"] = () => ZoomStep(1 / 1.25);
            actions["pan"] = () => SetTool(Tool.Pan);
            actions["scroll_follow"] = ToggleScrollFollow;
            actions["text"] = () => SetTool(Tool.Text);
            actions["shape"] = () => SetTool(Tool.Shape);
            actions["shape_line"] = () => SetShape(ShapeKind.Line);
            actions["shape_arrow"] = () => SetShape(ShapeKind.Arrow);
            actions["shape_rect"] = () => SetShape(ShapeKind.Rect);
            actions["shape_ellipse"] = () => SetShape(ShapeKind.Ellipse);
            actions["hide_ink"] = ToggleHideInk;
            actions["size1"] = () => SetPenSize(PenSizes[0]);
            actions["size2"] = () => SetPenSize(PenSizes[1]);
            actions["size3"] = () => SetPenSize(PenSizes[2]);
            actions["size_down"] = () => StepPenSize(-1);
            actions["size_up"] = () => StepPenSize(1);
            actions["bg"] = () => SetBg((BoardBg)(((int)Bg + 1) % 3));
            actions["undo"] = Undo;
            actions["redo"] = Redo;
            actions["clear"] = ClearAll;
            actions["capture"] = Capture;
            for (int i = 0; i < Palette.Length; i++)
            {
                var c = Palette[i];
                actions["color" + (i + 1)] = () => SetColor(c);
            }
            actions["next_monitor"] = NextMonitor;
            actions["toolbar"] = ToggleToolbar;
        }

        public void Run(string id)
        {
            Action a;
            if (actions.TryGetValue(id, out a)) a();
        }

        public void RegisterGlobalHotkeys()
        {
            UnregisterGlobalHotkeys();
            if (hwnd == IntPtr.Zero) return;
            var failed = new List<string>();
            for (int i = 0; i < HotkeyStore.Actions.Length; i++)
            {
                KeyCombo c;
                if (!Hotkeys.TryGetValue(HotkeyStore.Actions[i].Id, out c) || c.IsEmpty || !c.Global) continue;
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(c.Key);
                if (Native.RegisterHotKey(hwnd, 1000 + i, (uint)c.Mods | Native.MOD_NOREPEAT, vk)) registeredIds.Add(1000 + i);
                else failed.Add(c.Display);
            }
            if (failed.Count > 0) ShowToast("다른 프로그램이 이미 쓰는 단축키라 등록하지 못했습니다: " + string.Join(", ", failed));
        }

        public void UnregisterGlobalHotkeys()
        {
            foreach (int id in registeredIds) Native.UnregisterHotKey(hwnd, id);
            registeredIds.Clear();
        }

        public void ApplyHotkeys(Dictionary<string, KeyCombo> h)
        {
            Hotkeys = h;
            HotkeyStore.Save(h);
            RegisterGlobalHotkeys();
            if (Toolbar != null) Toolbar.UpdateTips();
        }

        public void HandleKey(KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return; // 글자 입력 중에는 단축키 대신 입력
            Key k = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            var mods = Keyboard.Modifiers;
            foreach (var a in HotkeyStore.Actions)
            {
                KeyCombo c;
                if (Hotkeys.TryGetValue(a.Id, out c) && !c.IsEmpty && c.Key == k && c.Mods == mods)
                {
                    Run(a.Id);
                    e.Handled = true;
                    return;
                }
            }
            if (mods != ModifierKeys.None) return;
            if (k == Key.Escape) { SetTool(Tool.Mouse); e.Handled = true; }
            else if (Zooming && k == Key.Left) { PanBy(-0.1, 0); e.Handled = true; }
            else if (Zooming && k == Key.Right) { PanBy(0.1, 0); e.Handled = true; }
            else if (Zooming && k == Key.Up) { PanBy(0, -0.1); e.Handled = true; }
            else if (Zooming && k == Key.Down) { PanBy(0, 0.1); e.Handled = true; }
        }
    }

    // ================= 도구 모음 =================
    public class ToolbarWindow : Window
    {
        class TipInfo { public Button B; public string Base; public string Id; }

        readonly OverlayWindow ov;
        readonly Dictionary<Button, Func<bool>> activeMap = new Dictionary<Button, Func<bool>>();
        readonly List<TipInfo> tips = new List<TipInfo>();
        readonly StackPanel body;
        TextBlock zoomLabel, monitorLabel, sizeHeader;
        Slider sizeSlider;
        Ellipse sizePreview;
        bool refreshing;
        Button monitorBtn;
        MonitorPickerWindow picker;

        static SolidColorBrush B(byte r, byte g, byte b)
        {
            var x = new SolidColorBrush(Color.FromRgb(r, g, b));
            x.Freeze();
            return x;
        }
        internal static readonly Brush PanelBg = B(0x20, 0x22, 0x27);
        internal static readonly Brush Hover = B(0x3A, 0x3D, 0x46);
        internal static readonly Brush Accent = B(0x2F, 0x6F, 0xEB);
        internal static readonly Brush Fg = B(0xEE, 0xEE, 0xEE);
        internal static readonly Brush Sub = B(0x9A, 0x9E, 0xA8);
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        internal static readonly ControlTemplate FlatTpl = MakeTemplate();

        static ControlTemplate MakeTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        public ToolbarWindow(OverlayWindow o)
        {
            ov = o;
            ov.Toolbar = this;
            Title = "판서 도구";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true;
            ShowInTaskbar = false;
            Left = 16;
            Top = 100;
            FontFamily = new FontFamily("Malgun Gothic");
            InputMethod.SetIsInputMethodEnabled(this, false);

            var root = new Border
            {
                Background = PanelBg,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(6),
                BorderBrush = Hover,
                BorderThickness = new Thickness(1)
            };
            root.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            var outer = new StackPanel { Width = 124 };

            // 머리글 (끌어서 이동, 단축키 설정, 접기)
            var header = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
            var collapse = Flat(Glyph("", 12), "도구 접기/펴기", 24, 22);
            var gear = Flat(Glyph("", 13), "단축키 설정", 24, 22);
            DockPanel.SetDock(collapse, Dock.Right);
            DockPanel.SetDock(gear, Dock.Right);
            header.Children.Add(collapse);
            header.Children.Add(gear);
            // 새 버전이 있을 때만 보이는 업데이트 버튼
            updateBtn = Flat(Glyph("", 13), "업데이트", 24, 22);
            updateBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            updateBtn.Visibility = Visibility.Collapsed;
            updateBtn.Click += (s, e) => StartUpdate();
            DockPanel.SetDock(updateBtn, Dock.Right);
            header.Children.Add(updateBtn);
            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll };
            title.Inlines.Add(new System.Windows.Documents.Run("판서") { Foreground = Fg, FontWeight = FontWeights.Bold, FontSize = 13 });
            title.Inlines.Add(new System.Windows.Documents.Run(" " + OverlayWindow.Version) { Foreground = Sub, FontSize = 10 });
            header.Children.Add(title);
            outer.Children.Add(header);
            gear.Click += (s, e) => new HotkeyWindow(ov) { Owner = this }.ShowDialog();

            body = new StackPanel();
            outer.Children.Add(body);
            collapse.Click += (s, e) =>
            {
                bool hide = body.Visibility == Visibility.Visible;
                body.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
                collapse.Content = Glyph(hide ? "" : "", 12);
            };

            // 도구
            body.Children.Add(Section("도구"));
            var tools = new UniformGrid { Columns = 2 };
            tools.Children.Add(ToolBtn("", "마우스", "마우스 모드 - 뒤의 프로그램 조작\n확대 중에는 확대된 화면을 그대로 클릭 (휠: 스크롤, Ctrl+휠: 배율)", Tool.Mouse, "mouse"));
            tools.Children.Add(ToolBtn("", "펜", "펜 (필압 지원)", Tool.Pen, "pen"));
            tools.Children.Add(ToolBtn("", "형광펜", "형광펜", Tool.Highlighter, "highlighter"));
            tools.Children.Add(ToolBtn("\uE8D2", "텍스트", "텍스트: 누른 곳에 글자 입력\nEsc·Ctrl+Enter·다른 곳 누르기로 완료\n색·굵기로 글자 색·크기 조절", Tool.Text, "text"));
            tools.Children.Add(ToolBtn("", "획 지우개", "획 단위로 지우기\n와콤 펜 뒤쪽 지우개도 사용 가능", Tool.EraseStroke, "eraser"));
            tools.Children.Add(ToolBtn("", "부분 지우개", "문지른 부분만 지우기", Tool.ErasePoint, "eraser_point"));
            tools.Children.Add(ActionBtn("", "전체 삭제", "전체 삭제", () => ov.ClearAll(), "clear"));
            const string hideTip = "필기 숨기기/보이기 (지우지 않고 잠시 감춤)\n숨긴 상태에서 펜으로 쓰면 다시 보입니다";
            var hideBtn = ActionBtn("\uE7B3", "필기 숨기기", hideTip, () => ov.ToggleHideInk(), "hide_ink");
            Track(hideBtn, () => ov.InkHidden);
            tools.Children.Add(hideBtn);
            body.Children.Add(tools);

            // 도형
            body.Children.Add(Section("도형 (Shift: 반듯하게)"));
            var shapes = new UniformGrid { Columns = 4 };
            string[] shapeGlyphs = { "╱", "→", "□", "○" };
            string[] shapeNames = { "직선", "화살표", "네모", "동그라미" };
            string[] shapeIds = { "shape_line", "shape_arrow", "shape_rect", "shape_ellipse" };
            for (int i = 0; i < 4; i++)
            {
                var kind = (ShapeKind)i;
                var b = Flat(new TextBlock { Text = shapeGlyphs[i], FontSize = 17, FontFamily = new FontFamily("Segoe UI Symbol") }, "도형: " + shapeNames[i], 26, 30);
                b.Click += (s, e) => ov.SetShape(kind);
                Track(b, () => ov.CurrentTool == Tool.Shape && ov.Shape == kind);
                AddTip(b, "도형: " + shapeNames[i] + "\n끌어서 그리기 · Shift: 반듯하게", shapeIds[i]);
                shapes.Children.Add(b);
            }
            body.Children.Add(shapes);

            // 색상
            body.Children.Add(Section("색상"));
            var colors = new UniformGrid { Columns = 4 };
            string[] colorNames = { "검정", "흰색", "빨강", "파랑", "초록", "노랑", "주황", "보라" };
            for (int i = 0; i < OverlayWindow.Palette.Length; i++)
            {
                var color = OverlayWindow.Palette[i];
                var dot = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(color), Stroke = B(0x77, 0x77, 0x77), StrokeThickness = 1 };
                var b = Flat(dot, "색: " + colorNames[i], 26, 26);
                b.Click += (s, e) => ov.SetColor(color);
                Track(b, () => ov.ActiveColor == color);
                AddTip(b, "색: " + colorNames[i], "color" + (i + 1));
                colors.Children.Add(b);
            }
            body.Children.Add(colors);

            // 굵기
            sizeHeader = Section("굵기");
            body.Children.Add(sizeHeader);
            var sizeRow = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
            var previewBox = new Grid { Width = 30, Height = 30 };
            sizePreview = new Ellipse { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            previewBox.Children.Add(sizePreview);
            DockPanel.SetDock(previewBox, Dock.Left);
            sizeRow.Children.Add(previewBox);
            // 슬라이더 위치 → 굵기: 제곱 곡선이라 가는 쪽을 세밀하게 조절 가능
            sizeSlider = new Slider
            {
                Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center,
                Focusable = false, IsMoveToPointEnabled = true, Margin = new Thickness(2, 0, 0, 0)
            };
            sizeSlider.ValueChanged += (s, e) =>
            {
                if (refreshing) return;
                ov.SetPenSize(OverlayWindow.MinSize + (OverlayWindow.MaxSize - OverlayWindow.MinSize) * e.NewValue * e.NewValue);
            };
            sizeSlider.PreviewMouseWheel += (s, e) => { ov.StepPenSize(e.Delta > 0 ? 1 : -1); e.Handled = true; };
            sizeRow.Children.Add(sizeSlider);
            sizeRow.ToolTip = "굵기 조절 (마우스 휠로도 조절)";
            AutomationProperties.SetName(sizeSlider, "굵기");
            body.Children.Add(sizeRow);

            // 배경
            body.Children.Add(Section("배경"));
            var bgs = new UniformGrid { Columns = 3 };
            string[] bgNames = { "투명", "흰판", "칠판" };
            string[] bgTips = { "화면 위에 판서", "흰색 화이트보드", "초록 칠판" };
            for (int i = 0; i < 3; i++)
            {
                var mode = (BoardBg)i;
                var b = Flat(new TextBlock { Text = bgNames[i], FontSize = 12 }, bgTips[i], 36, 28);
                b.Click += (s, e) => ov.SetBg(mode);
                Track(b, () => ov.Bg == mode);
                AddTip(b, bgTips[i], "bg");
                bgs.Children.Add(b);
            }
            body.Children.Add(bgs);

            // 라이브 확대
            body.Children.Add(Section("라이브 확대"));
            var zoomGrid = new UniformGrid { Columns = 2 };
            var zsp = new StackPanel();
            zsp.Children.Add(Glyph("", 18));
            zoomLabel = new TextBlock { Text = "확대", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            zsp.Children.Add(zoomLabel);
            const string zoomTip = "라이브 확대: 누른 뒤 확대할 곳을 클릭\n움직이는 화면을 확대한 채로 필기\n마우스 휠: 배율 · 오른쪽 드래그/가장자리: 이동\n한 번 더 누르면 끄기";
            var zb = Flat(zsp, zoomTip, 56, 50);
            zb.Click += (s, e) => ov.ToggleZoom();
            Track(zb, () => ov.Zooming || ov.Selecting);
            AddTip(zb, zoomTip, "zoom");
            zoomGrid.Children.Add(zb);
            zoomGrid.Children.Add(ToolBtn("", "화면 이동", "확대 화면을 끌어서 이동", Tool.Pan, "pan"));
            zoomGrid.Children.Add(SmallBtn("", "확대 배율 -", () => ov.ZoomStep(1 / 1.25), "zoom_out"));
            zoomGrid.Children.Add(SmallBtn("", "확대 배율 +", () => ov.ZoomStep(1.25), "zoom_in"));
            body.Children.Add(zoomGrid);

            // 모니터
            body.Children.Add(Section("화면"));
            var msp = new StackPanel { Orientation = Orientation.Horizontal };
            msp.Children.Add(Glyph("", 15));
            monitorLabel = new TextBlock { FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            msp.Children.Add(monitorLabel);
            monitorBtn = Flat(msp, "판서할 모니터 / 확대 원본 화면 선택", 120, 32);
            monitorBtn.Click += (s, e) => TogglePicker();
            AddTip(monitorBtn, "판서할 모니터 / 확대 원본 화면 선택", "next_monitor");
            body.Children.Add(monitorBtn);
            const string sfTip = "스크롤 따라가기 (실험 기능)\nPDF·크롬 등을 마우스 모드에서 스크롤하면\n그 영역의 판서가 함께 움직입니다\n동영상처럼 계속 바뀌는 화면에서는 틀릴 수 있어요";
            var sf = Flat(new TextBlock { Text = "↕  스크롤 따라가기", FontSize = 11 }, sfTip, 120, 30);
            sf.Click += (s, e) => ov.ToggleScrollFollow();
            Track(sf, () => ov.ScrollFollow);
            AddTip(sf, sfTip, "scroll_follow");
            body.Children.Add(sf);

            // 편집
            body.Children.Add(Section("편집"));
            var actions = new UniformGrid { Columns = 4 };
            actions.Children.Add(IconBtn("", "되돌리기", () => ov.Undo(), "undo"));
            actions.Children.Add(IconBtn("", "다시 실행", () => ov.Redo(), "redo"));
            actions.Children.Add(IconBtn("", "화면 저장 (사진\\판서 폴더)", () => ov.Capture(), "capture"));
            actions.Children.Add(IconBtn("", "종료", () => ov.Close(), null));
            body.Children.Add(actions);

            root.Child = outer;
            Content = root;

            ov.ToolStateChanged += Refresh;
            PreviewKeyDown += (s, e) => ov.HandleKey(e);
            Loaded += (s, e) => { Refresh(); UpdateTips(); };

            // 업데이트 확인: 켜고 5초 뒤, 그 뒤로 6시간마다
            var updTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            updTimer.Tick += (s, e) => { updTimer.Interval = TimeSpan.FromHours(6); CheckUpdate(); };
            updTimer.Start();
        }

        Button updateBtn;
        Updater.Release latest;

        async void CheckUpdate()
        {
            var r = await Task.Run(() => Updater.CheckLatest());
            if (r == null || Updater.Number(r.Tag) <= Updater.Number(OverlayWindow.Version)) return;
            latest = r;
            updateBtn.ToolTip = "새 버전 " + r.Tag + "이(가) 있습니다. 눌러서 업데이트" + (string.IsNullOrEmpty(r.Notes) ? "" : "\n\n" + r.Notes);
            AutomationProperties.SetName(updateBtn, "업데이트 " + r.Tag);
            updateBtn.Visibility = Visibility.Visible;
        }

        async void StartUpdate()
        {
            if (latest == null) return;
            var ok = MessageBox.Show(this,
                "판서 " + OverlayWindow.Version + " → " + latest.Tag + " 로 업데이트할까요?\n\n" +
                (string.IsNullOrEmpty(latest.Notes) ? "" : latest.Notes + "\n\n") +
                "업데이트하면 프로그램이 다시 시작되고, 지금 화면의 판서는 지워집니다.",
                "판서 업데이트", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
            updateBtn.IsEnabled = false;
            ov.ShowToast("업데이트 내려받는 중...");
            var rel = latest;
            string error = await Task.Run(() => Updater.Download(rel));
            if (error != null)
            {
                updateBtn.IsEnabled = true;
                ov.ShowToast("업데이트 실패: " + error);
                return;
            }
            error = Updater.Install();
            if (error != null)
            {
                updateBtn.IsEnabled = true;
                ov.ShowToast("업데이트 실패: " + error);
                return;
            }
            ov.Close(); // 새 버전이 실행되었으므로 지금 프로그램 종료
        }

        // 화면 선택 창 열기/닫기 (팝업 메뉴는 펜을 떼는 순간 항목이 눌려 닫히는 문제가 있어 창으로 대체)
        void TogglePicker()
        {
            if (picker != null) { picker.Close(); return; }
            picker = new MonitorPickerWindow(ov, this);
            picker.Owner = this;
            picker.Closed += (s, e) => picker = null;
            picker.Show();
        }

        TextBlock Section(string text)
        {
            return new TextBlock { Text = text, Foreground = Sub, FontSize = 10.5, Margin = new Thickness(4, 8, 0, 2) };
        }

        TextBlock Glyph(string glyph, double size)
        {
            return new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        UIElement GlyphLabel(string glyph, string label)
        {
            var sp = new StackPanel();
            sp.Children.Add(Glyph(glyph, 18));
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
            return sp;
        }

        Button Flat(object content, string tip, double w, double h)
        {
            var b = new Button
            {
                Content = content, Width = w, Height = h, Margin = new Thickness(2),
                Foreground = Fg, Background = Brushes.Transparent, Focusable = false,
                Cursor = Cursors.Hand, Template = FlatTpl
            };
            if (tip != null)
            {
                b.ToolTip = tip;
                AutomationProperties.SetName(b, tip.Split('\n')[0]);
            }
            b.MouseEnter += (s, e) => Paint(b);
            b.MouseLeave += (s, e) => Paint(b);
            return b;
        }

        Button ToolBtn(string glyph, string label, string tip, Tool tool, string actionId)
        {
            var b = Flat(GlyphLabel(glyph, label), tip, 56, 50);
            b.Click += (s, e) => ov.SetTool(tool);
            Track(b, () => ov.CurrentTool == tool);
            AddTip(b, tip, actionId);
            return b;
        }

        Button ActionBtn(string glyph, string label, string tip, Action act, string actionId)
        {
            var b = Flat(GlyphLabel(glyph, label), tip, 56, 50);
            b.Click += (s, e) => act();
            AddTip(b, tip, actionId);
            return b;
        }

        Button SmallBtn(string glyph, string tip, Action act, string actionId)
        {
            var b = Flat(Glyph(glyph, 15), tip, 56, 28);
            b.Click += (s, e) => act();
            AddTip(b, tip, actionId);
            return b;
        }

        Button IconBtn(string glyph, string tip, Action act, string actionId)
        {
            var b = Flat(Glyph(glyph, 16), tip, 26, 32);
            b.Click += (s, e) => act();
            AddTip(b, tip, actionId);
            return b;
        }

        void AddTip(Button b, string baseTip, string actionId)
        {
            if (actionId != null) tips.Add(new TipInfo { B = b, Base = baseTip, Id = actionId });
        }

        // 버튼 도움말에 현재 단축키 표시
        public void UpdateTips()
        {
            foreach (var t in tips)
            {
                KeyCombo c;
                string extra = ov.Hotkeys.TryGetValue(t.Id, out c) && !c.IsEmpty
                    ? "\n단축키: " + c.Display + (c.Global ? " (어디서나)" : "") : "";
                t.B.ToolTip = t.Base + extra;
            }
        }

        void Track(Button b, Func<bool> isActive) { activeMap[b] = isActive; }

        void Paint(Button b)
        {
            Func<bool> f;
            bool active = activeMap.TryGetValue(b, out f) && f();
            b.Background = active ? Accent : (b.IsMouseOver ? Hover : Brushes.Transparent);
        }

        void Refresh()
        {
            foreach (var b in activeMap.Keys) Paint(b);
            zoomLabel.Text = ov.Zooming ? ov.Zoom.ToString("0.0") + "배" : (ov.Selecting ? "위치 선택" : "확대");
            refreshing = true;
            sizeSlider.Value = Math.Sqrt((ov.PenSize - OverlayWindow.MinSize) / (OverlayWindow.MaxSize - OverlayWindow.MinSize));
            refreshing = false;
            sizeHeader.Text = ov.CurrentTool == Tool.Text ? "글자 크기   " + ov.TextFontSize : "굵기   " + ov.PenSize.ToString("0.#");
            double d = Math.Min(28, Math.Max(2, ov.PenSize));
            sizePreview.Width = d;
            sizePreview.Height = d;
            sizePreview.Fill = new SolidColorBrush(ov.ActiveColor);
            sizePreview.Stroke = ov.ActiveColor == Colors.Black ? Sub : null;
            monitorLabel.Text = ov.DrawMon.Short;
            monitorBtn.ToolTip = "판서할 화면: " + ov.DrawMon.Short + (ov.AutoMonitor ? " (자동 선택)" : " (직접 선택)") +
                "\n눌러서 판서할 화면 / 확대 원본 화면 선택";
        }
    }

    // ================= 스크롤 따라가기 (실험) =================
    class ScrollSnapshot
    {
        public System.Drawing.Rectangle Bounds;
        public int Version;
        public readonly List<System.Drawing.Rectangle> Masks = new List<System.Drawing.Rectangle>();
    }

    // 판서 화면을 주기적으로 찍어 직전 화면과 비교하고, 위아래로 밀린 영역과 거리를 찾아 판서를 옮김
    class ScrollFollower
    {
        const int C = 96;           // 가로로 비교할 표본 열 수
        const int IdleInterval = 130; // 화면이 가만히 있을 때 비교 주기 (ms)
        const int BusyInterval = 5;   // 스크롤 중: 쉬지 않고 바로 다음 비교 (화면 복사 자체가 약 45ms)
        const int BusyHoldMs = 1500;  // 마지막 변화 뒤 이 시간 동안은 빠르게 비교
        readonly OverlayWindow ov;
        volatile bool running;
        byte[] prev;
        bool[] prevMask;
        int prevH;
        int[] xs;
        DateTime lastChange = DateTime.MinValue;
        int sesX0 = int.MaxValue, sesX1 = int.MinValue, sesY0 = int.MaxValue, sesY1 = int.MinValue; // 이번 스크롤 동안 합친 영역
        // 기준 화면: 판서를 그린 직후의 화면. 스크롤이 멈추면 지금 화면과 직접 비교해 쌓인 오차를 바로잡음
        const int SettleMs = 300;
        byte[] anchor;
        bool[] anchorMask;
        int anchorVersion = -1, anchorH;
        int applied;          // 기준 화면 이후 판서를 옮긴 거리 합계 (물리 픽셀)
        bool sessionScrolled, corrected;
        int staleCount;       // 비교 실패로 이전 화면을 유지한 횟수
        // 휠 보조: 한 화면 이상 한 번에 점프해서 화면 비교로 못 찾을 때, 휠 칸 수 × 한 칸 거리로 채움
        bool sesMissed;
        int sesShift, sesWheel, pendingWheel;
        double pxPerNotch;    // 휠 한 칸에 움직이는 거리 (천천히 스크롤할 때 배움)
        int missX0 = int.MaxValue, missX1 = int.MinValue, missY0 = int.MaxValue, missY1 = int.MinValue;
        IntPtr sdc, mdc, dib, bits;  // 화면 복사용 GDI 자원 (백그라운드 스레드 전용)
        int dibH;

        public ScrollFollower(OverlayWindow o) { ov = o; }
        public bool Running { get { return running; } }

        public void Start()
        {
            if (running) return;
            running = true;
            prev = null;
            var th = new System.Threading.Thread(Loop) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
            th.Start();
        }

        public void Stop() { running = false; }

        void Loop()
        {
            while (running)
            {
                try { Step(); } catch { prev = null; }
                bool busy = (DateTime.Now - lastChange).TotalMilliseconds < BusyHoldMs;
                System.Threading.Thread.Sleep(busy ? BusyInterval : IdleInterval);
            }
            FreeGdi();
        }

        // 화면을 세로줄 C개로 줄여서 한 번에 복사 (전체를 비트맵으로 찍는 것보다 빠름)
        byte[] Capture(System.Drawing.Rectangle b)
        {
            int H = b.Height;
            if (mdc == IntPtr.Zero || dibH != H)
            {
                FreeGdi();
                sdc = Native.GetDC(IntPtr.Zero);
                mdc = Native.CreateCompatibleDC(sdc);
                var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = C, biHeight = -H, biPlanes = 1, biBitCount = 32 };
                dib = Native.CreateDIBSection(mdc, ref bi, 0, out bits, IntPtr.Zero, 0);
                Native.SelectObject(mdc, dib);
                Native.SetStretchBltMode(mdc, 3); // COLORONCOLOR: 섞지 않고 표본 픽셀 그대로
                dibH = H;
            }
            Native.StretchBlt(mdc, 0, 0, C, H, sdc, b.X, b.Y, b.Width, H, 0x00CC0020);
            var raw = new int[C * H];
            Marshal.Copy(bits, raw, 0, raw.Length);
            var cur = new byte[C * H];
            for (int i = 0; i < raw.Length; i++)
            {
                int px = raw[i];
                cur[i] = (byte)((((px >> 16) & 0xFF) * 77 + ((px >> 8) & 0xFF) * 150 + (px & 0xFF) * 29) >> 8);
            }
            return cur;
        }

        void FreeGdi()
        {
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (mdc != IntPtr.Zero) Native.DeleteDC(mdc);
            if (sdc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, sdc);
            dib = mdc = sdc = IntPtr.Zero;
            dibH = 0;
        }

        void Step()
        {
            ScrollSnapshot snap = null;
            ov.Dispatcher.Invoke(new Action(() => { snap = ov.TakeScrollSnapshot(); }));
            if (snap == null || !running) { prev = null; return; }
            var b = snap.Bounds;
            int W = b.Width, H = b.Height;
            if (xs == null || prevH != H || xs[C - 1] >= W)
            {
                // StretchBlt가 가져오는 표본 열의 실제 위치
                xs = new int[C];
                for (int k = 0; k < C; k++) xs[k] = (int)((k + 0.5) * W / C);
            }

            // 표본 열의 밝기만 추출
            var cur = Capture(b);

            // 판서·도구 모음 자리는 비교에서 제외
            var mask = new bool[H * C];
            foreach (var r in snap.Masks)
            {
                int y0 = Math.Max(0, r.Top), y1 = Math.Min(H - 1, r.Bottom);
                for (int k = 0; k < C; k++)
                {
                    if (xs[k] < r.Left || xs[k] > r.Right) continue;
                    for (int y = y0; y <= y1; y++) mask[y * C + k] = true;
                }
            }

            if (prevH != H) { sesX0 = int.MaxValue; sesX1 = int.MinValue; sesY0 = int.MaxValue; sesY1 = int.MinValue; } // 모니터가 바뀌면 영역 새로
            if (prev == null || prevH != H)
            {
                prev = cur; prevMask = mask; prevH = H;
                return;
            }
            var m = new bool[H * C];
            for (int i = 0; i < m.Length; i++) m[i] = mask[i] || prevMask[i];

            int shift, yTop, yBot, cMin, cMax;
            bool changed;
            bool freshSession = (DateTime.Now - lastChange).TotalMilliseconds > BusyHoldMs;
            bool found = Detect(prev, cur, m, H, out shift, out yTop, out yBot, out cMin, out cMax, out changed);
            if (changed)
            {
                dumpNo++;
                Log(string.Format("frame#{12} changed={0} found={1} shift={2} y={3}..{4} c={5}..{6} good={7}/{8} good0={9}/{10} best4={11} stale={13}",
                    changed, found, shift, yTop, yBot, cMin, cMax, dGood, dRows, dGood0, dRows0, dBest4, dumpNo, staleCount));
                Dump(prev, H, "f" + dumpNo.ToString("0000") + "_prev");
                Dump(cur, H, "f" + dumpNo.ToString("0000") + "_cur");
            }
            int wheel = System.Threading.Interlocked.Exchange(ref ov.WheelAccum, 0); // 지난 비교 이후 판서 화면 위에서 돈 휠 (120 = 한 칸)
            if (changed)
            {
                if (freshSession)
                {
                    // 스크롤 영역(ses*)은 스크롤할 때마다 새로 정하지 않고 계속 누적 (경계가 흔들리면 가장자리 판서가 어긋나므로)
                    sessionScrolled = false; sesMissed = false; sesShift = 0; sesWheel = 0;
                }
                lastChange = DateTime.Now;
                corrected = false;
                sesWheel += wheel + pendingWheel;
                pendingWheel = 0;
                // 크게 바뀌었는데 거리를 못 찾은 화면 (한 화면 이상 점프, 창 전환 등)
                // → 스크롤 영역(ses*)에는 넣지 않고, 휠 보조에만 쓰는 영역(miss*)으로 따로 기록
                if (!found && yTop >= 0 && yBot - yTop >= 80 && cMin <= cMax)
                {
                    if (!sesMissed) { missX0 = int.MaxValue; missX1 = int.MinValue; missY0 = int.MaxValue; missY1 = int.MinValue; }
                    sesMissed = true;
                    int half = W / C / 2 + 1;
                    missX0 = Math.Min(missX0, xs[cMin] - half); missX1 = Math.Max(missX1, xs[cMax] + half);
                    missY0 = Math.Min(missY0, yTop); missY1 = Math.Max(missY1, yBot);
                }
            }
            else if ((DateTime.Now - lastChange).TotalMilliseconds < BusyHoldMs) sesWheel += wheel;
            else pendingWheel = wheel; // 화면이 아직 안 바뀐 휠 (다음 변화에 포함)
            if (found)
            {
                // 한 번 스크롤하는 동안 찾은 영역을 계속 합쳐서 사용
                // (순간적으로 흰 여백만 지나가 안 바뀐 열이 있어도 같은 영역의 판서는 모두 함께 이동)
                int half = W / C / 2 + 1;
                sesX0 = Math.Min(sesX0, xs[cMin] - half);
                sesX1 = Math.Max(sesX1, xs[cMax] + half);
                sesY0 = Math.Min(sesY0, yTop);
                sesY1 = Math.Max(sesY1, yBot);
                int x0 = sesX0, x1 = sesX1, y0 = sesY0, y1 = sesY1;
                ov.Dispatcher.Invoke(new Action(() => ov.ApplyScroll(-shift, y0, y1, x0, x1)));
                applied += -shift;
                sesShift += shift;
                sessionScrolled = true;
            }

            double quietMs = (DateTime.Now - lastChange).TotalMilliseconds;
            if (anchor == null || anchorH != H || snap.Version != anchorVersion)
            {
                // 판서를 새로 그렸으면, 화면이 잠잠해진 뒤 지금 화면을 새 기준으로
                if (quietMs >= SettleMs)
                {
                    anchor = cur; anchorMask = mask; anchorVersion = snap.Version; anchorH = H;
                    applied = 0;
                }
            }
            else if ((sessionScrolled || sesMissed) && !corrected && quietMs >= SettleMs)
            {
                corrected = true;
                // 놓친 화면 없이 모두 찾은 스크롤이면 "휠 한 칸당 거리"를 배움
                if (!sesMissed && sesWheel != 0 && sesShift != 0)
                {
                    double sample = -sesShift * 120.0 / sesWheel;
                    if (sample > 5 && sample < 2000) pxPerNotch = pxPerNotch <= 0 ? sample : pxPerNotch * 0.5 + sample * 0.5;
                }
                bool ok = Correct(cur, mask, H, W);
                if (ok)
                {
                    // 확인된 위치를 새 기준으로 (여러 화면을 끊어서 스크롤해도 한 화면씩 확인)
                    anchor = cur; anchorMask = mask; applied = 0;
                }
                else if (sesMissed && sesWheel != 0 && pxPerNotch > 0 && (sesX0 <= sesX1 || missX0 <= missX1))
                {
                    // 기준 화면과 비교할 수 없을 만큼 멀리 점프: 휠 칸 수 × 한 칸 거리로 놓친 만큼 채움
                    double expected = -sesWheel / 120.0 * pxPerNotch;
                    int missing = (int)Math.Round(expected - sesShift);
                    Log(string.Format("wheel fill wheel={0} pxPerNotch={1:0.0} found={2} missing={3}", sesWheel, pxPerNotch, sesShift, missing));
                    if (Math.Abs(missing) >= 20)
                    {
                        // 확인된 스크롤 영역이 있으면 그것을, 없으면 이번에 바뀐 영역을 사용
                        bool known = sesX0 <= sesX1;
                        int x0 = known ? sesX0 : missX0, x1 = known ? sesX1 : missX1, y0 = known ? sesY0 : missY0, y1 = known ? sesY1 : missY1;
                        ov.Dispatcher.Invoke(new Action(() => ov.ApplyScroll(-missing, y0, y1, x0, x1)));
                        applied += -missing;
                    }
                }
                // 이번 스크롤 기록은 다 썼으므로 비움 (같은 휠로 두 번 채우지 않도록)
                sessionScrolled = false; sesMissed = false; sesShift = 0; sesWheel = 0;
            }

            // 비교에 실패한 화면(한PDF가 빠른 스크롤 중 잠깐 보여 주는 덜 그려진 화면 등)은 건너뛰고
            // 마지막으로 성공한 화면과 계속 비교. 10번 연속 실패하면 새로 시작
            if (found || !changed || staleCount >= 10) { prev = cur; prevMask = mask; staleCount = 0; }
            else staleCount++;
            prevH = H;
        }

        // 스크롤이 멈춘 뒤: 기준 화면과 지금 화면을 직접 비교해 "처음부터 총 이동 거리"를 구하고,
        // 지금까지 옮긴 합계와 다르면 그 차이만큼 판서를 바로잡음 (중간에 놓친 프레임의 오차 제거)
        bool Correct(byte[] cur, bool[] mask, int H, int W)
        {
            var m = new bool[H * C];
            for (int i = 0; i < m.Length; i++) m[i] = mask[i] || anchorMask[i];
            int total, yTop, yBot, cMin, cMax;
            bool changed;
            bool found = Detect(anchor, cur, m, H, out total, out yTop, out yBot, out cMin, out cMax, out changed);
            int target;
            int x0 = sesX0, x1 = sesX1, y0 = sesY0, y1 = sesY1;
            if (!changed || yBot - yTop < 80) target = 0; // 기준 화면과 (거의) 같음 → 처음 위치로 돌아온 것
            else if (found)
            {
                target = -total;
                int half = W / C / 2 + 1;
                x0 = Math.Min(x0, xs[cMin] - half); x1 = Math.Max(x1, xs[cMax] + half);
                y0 = Math.Min(y0, yTop); y1 = Math.Max(y1, yBot);
            }
            else
            {
                // 광고 등 일부만 바뀌고 제자리인 경우: 움직이지 않은 채로 대부분 줄이 맞으면 처음 위치
                int g0, r0;
                long e0;
                Match(anchor, cur, m, yTop, yBot, 0, C - 1, 0, 1, 8, 40, out g0, out r0, out e0);
                if (r0 >= 20 && g0 >= r0 * 0.9) target = 0;
                else return false;                // 한 화면 이상 멀어져 비교 불가 → 그대로 둠
            }
            int corr = target - applied;
            Log(string.Format("correct target={0} applied={1} corr={2}", target, applied, corr));
            if (corr == 0) return true;
            if (x0 > x1 || y0 > y1) return false;
            ov.Dispatcher.Invoke(new Action(() => ov.ApplyScroll(corr, y0, y1, x0, x1)));
            applied = target;
            return true;
        }

        // 직전 화면 a와 지금 화면 c 비교: 바뀐 영역을 찾고, c[y] ≈ a[y + s]가 되는 s(스크롤 거리)를 찾음
        // 진단 로그 (설정 파일에 scrolldebug=1일 때만 %APPDATA%\Panseo\scroll.log에 기록)
        static readonly bool DebugLog = Settings.Get("scrolldebug") == "1" || Settings.Get("scrolldebug") == "2";
        static readonly bool DumpFrames = Settings.Get("scrolldebug") == "2"; // 화면 저장까지 (느려짐)
        static int dGood, dRows, dGood0, dRows0, dBest4, dumpNo;

        // 진단용: 비교한 표본 화면(세로줄 C개)을 그림 파일로 저장 (최대 200장)
        static void Dump(byte[] g, int H, string name)
        {
            if (!DumpFrames || g == null || dumpNo > 100) return;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Panseo", "dbg");
                Directory.CreateDirectory(dir);
                const int Z = 4;
                using (var bmp = new System.Drawing.Bitmap(C * Z, H / 2))
                {
                    for (int y = 0; y < H / 2; y++)
                        for (int k = 0; k < C; k++)
                        {
                            int v = g[(y * 2) * C + k];
                            var col = System.Drawing.Color.FromArgb(v, v, v);
                            for (int z = 0; z < Z; z++) bmp.SetPixel(k * Z + z, y, col);
                        }
                    bmp.Save(System.IO.Path.Combine(dir, name + ".png"));
                }
            }
            catch { }
        }
        static void Log(string msg)
        {
            if (!DebugLog) return;
            try
            {
                File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Panseo", "scroll.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
            }
            catch { }
        }

        static bool Detect(byte[] a, byte[] c, bool[] m, int H, out int best, out int yTop, out int yBot, out int cMin, out int cMax, out bool changed)
        {
            best = 0; yTop = -1; yBot = -1; cMin = C; cMax = -1; changed = false;
            dGood = dRows = dGood0 = dRows0 = dBest4 = -1;
            var colCount = new int[C];
            for (int y = 0; y < H; y++)
            {
                int cnt = 0, o = y * C;
                for (int k = 0; k < C; k++)
                {
                    if (m[o + k]) continue;
                    if (Math.Abs(a[o + k] - c[o + k]) > 16) { cnt++; colCount[k]++; }
                }
                if (cnt >= 2) { if (yTop < 0) yTop = y; yBot = y; }
            }
            changed = yTop >= 0;
            if (yTop < 0 || yBot - yTop < 80) return false;
            for (int k = 0; k < C; k++)
                if (colCount[k] >= 8) { cMin = Math.Min(cMin, k); cMax = Math.Max(cMax, k); }
            if (cMax - cMin < 3) return false;

            // 한 번에 움직일 수 있는 거리: 겹치는 부분이 50줄 이상 남는 데까지
            int maxS = yBot - yTop - 50;
            if (maxS < 4) return false;
            int colStep = Math.Max(1, (cMax - cMin + 1) / 24);

            // 1단계: 4줄씩 평균 낸 흐린 화면으로 대략적인 거리 찾기 (조금 어긋나도 맞는 것으로 봄)
            const int F = 4;
            int H4 = H / F;
            byte[] a4 = Shrink(a, H4, F), c4 = Shrink(c, H4, F);
            bool[] m4 = ShrinkMask(m, H4, F);
            int top4 = yTop / F, bot4 = Math.Min(H4 - 1, yBot / F), max4 = maxS / F;
            // 비교할 줄이 너무 적은(겹침이 아주 좁은) 거리는 우연히 맞을 수 있으므로 제외
            int minRows4 = Math.Max(8, (bot4 - top4) / 12), minRows = Math.Max(20, (yBot - yTop) / 12);
            double bestScore = double.MinValue;
            int best4 = 0;
            for (int s = -max4; s <= max4; s++)
            {
                if (s == 0) continue;
                double sc = Score(a4, c4, m4, top4, bot4, cMin, cMax, s, colStep, 10, 30, minRows4);
                if (sc > bestScore) { bestScore = sc; best4 = s; }
            }
            if (best4 == 0 || bestScore < 0.5) return false;

            // 2단계: 그 근처를 원래 해상도에서 1픽셀 단위로
            bestScore = double.MinValue;
            int bestS = 0;
            for (int s = best4 * F - F - 2; s <= best4 * F + F + 2; s++)
            {
                if (s == 0 || Math.Abs(s) > maxS) continue;
                double sc = Score(a, c, m, yTop, yBot, cMin, cMax, s, 1, 8, 40, minRows);
                if (sc > bestScore) { bestScore = sc; bestS = s; }
            }
            if (bestS == 0) return false;
            int good, rows, good0, rows0;
            long err;
            Match(a, c, m, yTop, yBot, cMin, cMax, bestS, 1, 8, 40, out good, out rows, out err);
            Match(a, c, m, yTop, yBot, cMin, cMax, 0, 1, 8, 40, out good0, out rows0, out err);
            dGood = good; dRows = rows; dGood0 = good0; dRows0 = rows0; dBest4 = best4;
            double r = rows > 0 ? (double)good / rows : 0, r0 = rows0 > 0 ? (double)good0 / rows0 : 0;
            // 맞는 줄 비율이 높고, 안 움직였다고 볼 때보다 확실히 높을 때만 인정
            // (고정 검색창·광고처럼 일부가 따로 바뀌어도 나머지 줄로 판단)
            if (rows >= minRows && r >= 0.75 && r > r0 + 0.15) { best = bestS; return true; }
            return false;
        }

        static byte[] Shrink(byte[] src, int H4, int F)
        {
            var d = new byte[H4 * C];
            for (int y = 0; y < H4; y++)
                for (int k = 0; k < C; k++)
                {
                    int sum = 0;
                    for (int i = 0; i < F; i++) sum += src[(y * F + i) * C + k];
                    d[y * C + k] = (byte)(sum / F);
                }
            return d;
        }

        static bool[] ShrinkMask(bool[] src, int H4, int F)
        {
            var d = new bool[H4 * C];
            for (int y = 0; y < H4; y++)
                for (int k = 0; k < C; k++)
                    for (int i = 0; i < F; i++)
                        if (src[(y * F + i) * C + k]) { d[y * C + k] = true; break; }
            return d;
        }

        // 거리 s에서 줄마다 비교: 평균 차이가 작고, 크게 다른 점(글자와 바탕이 어긋난 곳)이 거의 없으면 "맞는 줄"
        // 무늬 없는 줄(흰 여백 등)은 어느 거리에서나 맞으므로 제외
        static void Match(byte[] a, byte[] c, bool[] m, int yTop, int yBot, int cMin, int cMax, int s, int colStep, int tol, int big,
            out int good, out int rows, out long err)
        {
            good = 0; rows = 0; err = 0;
            int y0 = Math.Max(yTop, yTop - s), y1 = Math.Min(yBot, yBot - s);
            for (int y = y0; y <= y1; y++)
            {
                int oc = y * C, op = (y + s) * C, sum = 0, n = 0, lo = 255, hi = 0, outliers = 0, total = 0;
                for (int k = cMin; k <= cMax; k += colStep)
                {
                    if (m[oc + k] || m[op + k]) continue;
                    int v = c[oc + k];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                    total += v;
                    int d = Math.Abs(v - a[op + k]);
                    if (d > big) outliers++;
                    sum += d;
                    n++;
                }
                if (n < 3 || hi - lo < 24) continue;
                // 줄에서 바탕과 다른 "글자·그림" 점 개수. 듬성듬성한 글자 줄은 몇 개만 어긋나도 틀린 줄로 봄
                int mean = total / n, ink = 0;
                for (int k = cMin; k <= cMax; k += colStep)
                {
                    if (m[oc + k] || m[op + k]) continue;
                    if (Math.Abs(c[oc + k] - mean) > big || Math.Abs(a[op + k] - mean) > big) ink++;
                }
                rows++;
                err += sum / n;
                if (sum < n * tol && outliers <= Math.Max(1, ink / 4)) good++;
            }
        }

        // 점수 = 맞는 줄 비율 (같은 비율이면 오차가 작은 쪽). 비교 줄이 너무 적으면 제외
        static double Score(byte[] a, byte[] c, bool[] m, int yTop, int yBot, int cMin, int cMax, int s, int colStep, int tol, int big, int minRows)
        {
            int good, rows;
            long err;
            Match(a, c, m, yTop, yBot, cMin, cMax, s, colStep, tol, big, out good, out rows, out err);
            if (rows < minRows) return double.MinValue;
            return (double)good / rows - (double)err / rows * 0.0005;
        }
    }

    // ================= 텍스트 =================
    // 텍스트를 획처럼 다루는 특수 획: 되돌리기·지우개·확대/이동 변환이 그대로 적용됨
    public class TextStroke : Stroke
    {
        public readonly string Text;
        readonly double fontSize, ppd;
        readonly Color color;
        readonly Point origin;
        Matrix layout = Matrix.Identity;

        public TextStroke(string text, double size, Color c, Point o, double pixelsPerDip)
            : base(Outline(text, size, o, pixelsPerDip), HitAttributes(size))
        {
            Text = text;
            fontSize = size;
            color = c;
            origin = o;
            ppd = pixelsPerDip;
        }

        static FormattedText Format(string text, double size, Brush brush, double ppd)
        {
            return new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                size, brush, ppd);
        }

        static double Spacing(double size) { return Math.Max(4, size * 0.35); }

        // 지우개가 글자 영역 어디에 닿아도 인식되도록 영역을 지그재그로 채운 보이지 않는 점들
        static StylusPointCollection Outline(string text, double size, Point o, double ppd)
        {
            var ft = Format(text, size, Brushes.Black, ppd);
            double w = Math.Max(ft.WidthIncludingTrailingWhitespace, size * 0.5), h = ft.Height, s = Spacing(size);
            var pts = new StylusPointCollection();
            bool ltr = true;
            for (double y = o.Y + s / 2; ; y += s)
            {
                double yy = Math.Min(y, o.Y + h - s / 2);
                pts.Add(new StylusPoint(ltr ? o.X : o.X + w, yy));
                pts.Add(new StylusPoint(ltr ? o.X + w : o.X, yy));
                ltr = !ltr;
                if (yy >= o.Y + h - s / 2) break;
            }
            return pts;
        }

        static DrawingAttributes HitAttributes(double size)
        {
            double s = Spacing(size) * 1.3;
            return new DrawingAttributes { Color = Color.FromArgb(0, 0, 0, 0), Width = s, Height = s, FitToCurve = false, IgnorePressure = true };
        }

        protected override void DrawCore(DrawingContext dc, DrawingAttributes da)
        {
            var ft = Format(Text, fontSize, new SolidColorBrush(color), ppd);
            dc.PushTransform(new MatrixTransform(layout));
            dc.DrawText(ft, origin);
            dc.Pop();
        }

        public override void Transform(Matrix m, bool applyToStylusTip)
        {
            base.Transform(m, applyToStylusTip);
            layout.Append(m);
        }

        public override Stroke Clone()
        {
            var c = new TextStroke(Text, fontSize, color, origin, ppd);
            c.StylusPoints = StylusPoints.Clone();
            c.DrawingAttributes = DrawingAttributes.Clone();
            c.layout = layout;
            return c;
        }
    }

    // 판서 화면을 누르면 그 자리에 입력 칸을 띄우고, 완료하면 TextStroke로 바꿔 넣음
    public class TextEditor
    {
        readonly OverlayWindow main;
        readonly InkCanvas ink;
        readonly Canvas canvas = new Canvas();
        readonly Action onState;
        TextBox box;

        public TextEditor(Panel host, InkCanvas ink, OverlayWindow main)
        {
            this.main = main;
            this.ink = ink;
            host.Children.Add(canvas);
            ink.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (main.CurrentTool != Tool.Text) return;
                Commit();
                Begin(e.GetPosition(ink));
                e.Handled = true;
            };
            onState = OnState;
            main.ToolStateChanged += onState;
        }

        public void Detach()
        {
            main.ToolStateChanged -= onState;
        }

        void OnState()
        {
            if (box == null) return;
            if (main.CurrentTool != Tool.Text) { Commit(); return; }
            var brush = new SolidColorBrush(main.PenColor);
            box.Foreground = brush;
            box.CaretBrush = brush;
            box.FontSize = main.TextFontSize;
        }

        void Begin(Point p)
        {
            var brush = new SolidColorBrush(main.PenColor);
            box = new TextBox
            {
                FontFamily = new FontFamily("Malgun Gothic"),
                FontWeight = FontWeights.SemiBold,
                FontSize = main.TextFontSize,
                Foreground = brush,
                CaretBrush = brush,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderBrush = ToolbarWindow.Accent,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                AcceptsReturn = true,
                AcceptsTab = false,
                MinWidth = 60,
                TextWrapping = TextWrapping.NoWrap
            };
            // 판서 창은 단축키 때문에 한글 입력을 꺼 두었으므로 입력 칸에서만 켜고 한글로 시작
            InputMethod.SetIsInputMethodEnabled(box, true);
            InputMethod.SetPreferredImeState(box, InputMethodState.On);
            InputMethod.SetPreferredImeConversionMode(box, ImeConversionModeValues.Native);
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
                {
                    Commit();
                    e.Handled = true;
                }
            };
            Canvas.SetLeft(box, p.X);
            Canvas.SetTop(box, p.Y - main.TextFontSize * 0.7);
            canvas.Children.Add(box);
            var w = Window.GetWindow(ink);
            if (w != null) w.Activate();
            var b = box;
            b.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { b.Focus(); Keyboard.Focus(b); }));
        }

        public void Commit()
        {
            if (box == null) return;
            var b = box;
            box = null;
            canvas.Children.Remove(b);
            string text = b.Text.TrimEnd();
            if (text.Trim().Length == 0) return;
            // 입력 칸 테두리(1)와 글자 여백(2)만큼 안쪽이 실제 글자 위치
            var origin = new Point(Canvas.GetLeft(b) + 3, Canvas.GetTop(b) + 1);
            double ppd = VisualTreeHelper.GetDpi(ink).PixelsPerDip;
            ink.Strokes.Add(new TextStroke(text, b.FontSize, ((SolidColorBrush)b.Foreground).Color, origin, ppd));
        }
    }

    // ================= 도형 =================
    // 끌어서 미리보기 → 놓으면 일반 획(Stroke)으로 넣음 (되돌리기·지우개·확대·스크롤 따라가기가 그대로 적용)
    public class ShapeDrawer
    {
        readonly OverlayWindow main;
        readonly InkCanvas ink;
        readonly Canvas canvas = new Canvas { IsHitTestVisible = false };
        readonly System.Windows.Shapes.Path preview = new System.Windows.Shapes.Path
        {
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        Point start;
        bool drawing;

        public ShapeDrawer(Panel host, InkCanvas ink, OverlayWindow main)
        {
            this.main = main;
            this.ink = ink;
            canvas.Children.Add(preview);
            host.Children.Add(canvas);
            ink.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (main.CurrentTool != Tool.Shape) return;
                start = e.GetPosition(ink);
                drawing = true;
                ink.CaptureMouse();
                Update(start);
                e.Handled = true;
            };
            ink.PreviewMouseMove += (s, e) => { if (drawing) Update(e.GetPosition(ink)); };
            ink.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!drawing) return;
                drawing = false;
                ink.ReleaseMouseCapture();
                preview.Data = null;
                var pts = Points(start, e.GetPosition(ink));
                if (pts != null) ink.Strokes.Add(MakeStroke(pts));
                e.Handled = true;
            };
            ink.LostMouseCapture += (s, e) => { if (drawing) { drawing = false; preview.Data = null; } };
        }

        void Update(Point p)
        {
            var pts = Points(start, p);
            if (pts == null) { preview.Data = null; return; }
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], false, false);
                c.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, true);
            }
            g.Freeze();
            preview.Data = g;
            preview.Stroke = new SolidColorBrush(main.PenColor);
            preview.StrokeThickness = main.PenSize;
        }

        Stroke MakeStroke(List<Point> pts)
        {
            var sp = new StylusPointCollection();
            foreach (var p in pts) sp.Add(new StylusPoint(p.X, p.Y));
            var da = new DrawingAttributes
            {
                Color = main.PenColor, Width = main.PenSize, Height = main.PenSize,
                FitToCurve = false, IgnorePressure = true, StylusTip = StylusTip.Ellipse // 모서리가 둥글게 뭉개지지 않도록 곡선 보정 끔
            };
            return new Stroke(sp, da);
        }

        // 도형 모양대로 점 목록 만들기. Shift: 정사각형·정원·15° 단위 선
        List<Point> Points(Point a, Point b)
        {
            if ((b - a).Length < 3) return null;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            var pts = new List<Point>();
            switch (main.Shape)
            {
                case ShapeKind.Line:
                case ShapeKind.Arrow:
                    if (shift)
                    {
                        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 12)) * (Math.PI / 12);
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        b = new Point(a.X + len * Math.Cos(ang), a.Y + len * Math.Sin(ang));
                    }
                    pts.Add(a);
                    pts.Add(b);
                    if (main.Shape == ShapeKind.Arrow)
                    {
                        // 화살촉: 끝점에서 양쪽으로 갔다 오는 한 획
                        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X), head = Math.Max(14, main.PenSize * 4 + 8), spread = Math.PI / 7;
                        var l = new Point(b.X - head * Math.Cos(ang - spread), b.Y - head * Math.Sin(ang - spread));
                        var r = new Point(b.X - head * Math.Cos(ang + spread), b.Y - head * Math.Sin(ang + spread));
                        pts.Add(l); pts.Add(b); pts.Add(r);
                    }
                    break;
                case ShapeKind.Rect:
                    if (shift) { double d = Math.Max(Math.Abs(dx), Math.Abs(dy)); b = new Point(a.X + Math.Sign(dx) * d, a.Y + Math.Sign(dy) * d); }
                    pts.Add(a); pts.Add(new Point(b.X, a.Y)); pts.Add(b); pts.Add(new Point(a.X, b.Y)); pts.Add(a);
                    break;
                case ShapeKind.Ellipse:
                    if (shift) { double d = Math.Max(Math.Abs(dx), Math.Abs(dy)); b = new Point(a.X + Math.Sign(dx) * d, a.Y + Math.Sign(dy) * d); }
                    double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                    int n = (int)Math.Max(36, Math.Min(180, (rx + ry) / 3));
                    for (int i = 0; i <= n; i++)
                    {
                        double t = 2 * Math.PI * i / n;
                        pts.Add(new Point(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
                    }
                    break;
            }
            return pts;
        }
    }

    // ================= 부분 지우개 범위 표시 =================
    // 커서 이미지 대신 판서 화면 위에 실제 지우개 크기의 원을 그려서 지우는 범위와 정확히 일치시킴
    public class EraserPreview
    {
        readonly Canvas canvas = new Canvas { IsHitTestVisible = false };
        readonly Ellipse outer = new Ellipse { Stroke = Brushes.White, StrokeThickness = 3, Visibility = Visibility.Collapsed };
        readonly Ellipse inner = new Ellipse { Stroke = Brushes.Black, StrokeThickness = 1.2, Visibility = Visibility.Collapsed };
        readonly InkCanvas ink;
        bool active;
        double d;

        public EraserPreview(Panel host, InkCanvas ink)
        {
            this.ink = ink;
            canvas.Children.Add(outer);
            canvas.Children.Add(inner);
            host.Children.Add(canvas);
            ink.PreviewMouseMove += (s, e) => Move(e.GetPosition(ink));
            ink.PreviewStylusMove += (s, e) => Move(e.GetPosition(ink));
            ink.PreviewStylusInAirMove += (s, e) => Move(e.GetPosition(ink));
            ink.MouseLeave += (s, e) => Hide();
            ink.StylusOutOfRange += (s, e) => Hide();
        }

        public void Set(bool on, double diameter)
        {
            active = on;
            d = diameter;
            outer.Width = outer.Height = d;
            inner.Width = inner.Height = d;
            if (!on) Hide();
        }

        void Move(Point p)
        {
            if (!active) return;
            Canvas.SetLeft(outer, p.X - d / 2); Canvas.SetTop(outer, p.Y - d / 2);
            Canvas.SetLeft(inner, p.X - d / 2); Canvas.SetTop(inner, p.Y - d / 2);
            outer.Visibility = inner.Visibility = Visibility.Visible;
        }

        void Hide()
        {
            outer.Visibility = inner.Visibility = Visibility.Collapsed;
        }
    }

    // ================= 확대할 곳 선택 =================
    public class ZoomSelectWindow : Window
    {
        readonly OverlayWindow ov;
        readonly System.Drawing.Rectangle mon; // 원본 모니터 (물리 픽셀)
        readonly RectangleGeometry full = new RectangleGeometry();
        readonly RectangleGeometry hole = new RectangleGeometry();
        readonly Rectangle frame, frameInner;
        readonly Border hint;
        readonly TextBlock hintText;
        Point pos = new Point(double.NaN, double.NaN);
        bool pressed, done;
        public Action<double, double> Picked; // 확대 중심 (물리 픽셀)

        public ZoomSelectWindow(OverlayWindow o, System.Drawing.Rectangle monitor)
        {
            ov = o;
            mon = monitor;
            Title = "확대할 곳 선택";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Left = 0; Top = 0; Width = 400; Height = 300;
            Cursor = Cursors.Cross;
            InputMethod.SetIsInputMethodEnabled(this, false);

            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            // 네모 바깥만 살짝 어둡게
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, hole),
                IsHitTestVisible = false
            });
            frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)), StrokeThickness = 4, IsHitTestVisible = false };
            frameInner = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
            canvas.Children.Add(frame);
            canvas.Children.Add(frameInner);
            hintText = new TextBlock { Foreground = Brushes.White, FontSize = 14, FontFamily = new FontFamily("Malgun Gothic") };
            hint = new Border
            {
                Child = hintText,
                Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x22, 0x27)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                IsHitTestVisible = false
            };
            canvas.Children.Add(hint);
            Content = canvas;

            MouseMove += (s, e) => MoveTo(e.GetPosition(this));
            MouseLeftButtonDown += (s, e) => { pressed = true; CaptureMouse(); MoveTo(e.GetPosition(this)); e.Handled = true; };
            // 펜을 뗀 뒤에 확대해야 남은 펜 입력이 판서로 들어가지 않음
            MouseLeftButtonUp += (s, e) => { if (!pressed) return; ReleaseMouseCapture(); MoveTo(e.GetPosition(this)); Pick(); e.Handled = true; };
            MouseRightButtonDown += (s, e) => { Close(); e.Handled = true; };
            MouseWheel += (s, e) => { ov.AdjustZoomLevel(e.Delta > 0 ? 1.2 : 1 / 1.2); Layout(); e.Handled = true; };
            KeyDown += (s, e) =>
            {
                Key k = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
                if (k == Key.Escape) Close();
                else if (k == Key.OemPlus || k == Key.Add) { ov.AdjustZoomLevel(1.2); Layout(); }
                else if (k == Key.OemMinus || k == Key.Subtract) { ov.AdjustZoomLevel(1 / 1.2); Layout(); }
                else if (k == Key.Enter || k == Key.Space) Pick();
                else return;
                e.Handled = true;
            };
            SourceInitialized += (s, e) => Place();
            SizeChanged += (s, e) => Layout();
            ContentRendered += (s, e) =>
            {
                Place();
                Native.POINT p;
                Native.GetCursorPos(out p);
                double sc = Scale;
                pos = mon.Contains(p.X, p.Y)
                    ? new Point((p.X - mon.X) / sc, (p.Y - mon.Y) / sc)
                    : new Point(ActualWidth / 2, ActualHeight / 2);
                Layout();
                Activate();
            };
        }

        double Scale
        {
            get
            {
                var s = PresentationSource.FromVisual(this);
                return s != null && s.CompositionTarget != null ? s.CompositionTarget.TransformToDevice.M11 : 1.0;
            }
        }

        void Place()
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST,
                mon.X, mon.Y, mon.Width, mon.Height, 0);
        }

        void MoveTo(Point p)
        {
            pos = p;
            Layout();
        }

        // 네모 = 현재 배율로 확대했을 때 보이는 영역
        Rect SelRect()
        {
            double sc = Scale;
            double w = ActualWidth, h = ActualHeight;
            double rw = Math.Min(w, ov.DestW / ov.Zoom / sc), rh = Math.Min(h, ov.DestH / ov.Zoom / sc);
            double cx = double.IsNaN(pos.X) ? w / 2 : pos.X, cy = double.IsNaN(pos.Y) ? h / 2 : pos.Y;
            cx = Math.Max(rw / 2, Math.Min(w - rw / 2, cx));
            cy = Math.Max(rh / 2, Math.Min(h - rh / 2, cy));
            return new Rect(cx - rw / 2, cy - rh / 2, rw, rh);
        }

        void Layout()
        {
            if (ActualWidth <= 0) return;
            full.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            var r = SelRect();
            hole.Rect = r;
            Canvas.SetLeft(frame, r.X - 2); Canvas.SetTop(frame, r.Y - 2);
            frame.Width = r.Width + 4; frame.Height = r.Height + 4;
            Canvas.SetLeft(frameInner, r.X + 1); Canvas.SetTop(frameInner, r.Y + 1);
            frameInner.Width = Math.Max(0, r.Width - 2); frameInner.Height = Math.Max(0, r.Height - 2);
            hintText.Text = "클릭한 곳을 " + ov.Zoom.ToString("0.0") + "배로 확대   ·   휠: 배율   ·   Esc: 취소";
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double hx = Math.Max(4, Math.Min(ActualWidth - hint.DesiredSize.Width - 4, r.X + (r.Width - hint.DesiredSize.Width) / 2));
            double hy = r.Bottom + 10 + hint.DesiredSize.Height < ActualHeight ? r.Bottom + 10 : Math.Max(4, r.Y - hint.DesiredSize.Height - 10);
            if (hy < 4 || (hy > r.Y && hy < r.Bottom)) hy = r.Y + 10;
            Canvas.SetLeft(hint, hx);
            Canvas.SetTop(hint, hy);
        }

        void Pick()
        {
            if (done) return;
            done = true;
            var r = SelRect();
            double sc = Scale;
            double x = mon.X + (r.X + r.Width / 2) * sc, y = mon.Y + (r.Y + r.Height / 2) * sc;
            var cb = Picked;
            Close();
            if (cb != null) cb(x, y);
        }
    }

    // ================= 화면 선택 창 =================
    public class MonitorPickerWindow : Window
    {
        readonly OverlayWindow ov;
        readonly Window toolbar;
        readonly StackPanel list = new StackPanel();
        readonly List<IdentifyWindow> ids = new List<IdentifyWindow>();
        readonly Action onStateChanged;
        bool closed;

        public MonitorPickerWindow(OverlayWindow o, Window tb)
        {
            ov = o;
            toolbar = tb;
            Title = "화면 선택";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = true;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Malgun Gothic");
            InputMethod.SetIsInputMethodEnabled(this, false);

            var root = new Border
            {
                Background = ToolbarWindow.PanelBg,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10),
                BorderBrush = ToolbarWindow.Hover,
                BorderThickness = new Thickness(1),
                MinWidth = 380,
                Child = list
            };
            root.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            Content = root;
            Build();

            onStateChanged = () => Dispatcher.BeginInvoke(new Action(Refresh));
            ov.ToolStateChanged += onStateChanged;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => { PlaceNextToToolbar(); ShowIds(); };
            Closed += (s, e) => { closed = true; ov.ToolStateChanged -= onStateChanged; CloseIds(); };
        }

        void Refresh()
        {
            if (closed) return; // 닫힌 뒤에 예약된 갱신이 번호 표시를 다시 띄우지 않도록
            Build();
            CloseIds();
            ShowIds();
        }

        void Build()
        {
            list.Children.Clear();
            var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
            var close = MakeButton(new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 }, false);
            close.Width = 30; close.Height = 26;
            close.ToolTip = "닫기 (Esc)";
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = "화면 선택", Foreground = ToolbarWindow.Fg, FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(header);

            var mons = Monitors.GetAll();
            Section("판서할 화면 (전자칠판·와콤 펜을 다른 모니터에 연결해 쓸 때는 그 모니터 선택)");
            Item("자동: 와콤 → 전자칠판 → 주 모니터   (지금 " + Monitors.DefaultPen().Short + ")", ov.AutoMonitor, () => ov.SetAutoMonitor());
            foreach (var m in mons)
            {
                var mm = m;
                Item(m.Label, !ov.AutoMonitor && m.Device == ov.DrawMon.Device, () => ov.SetDrawMonitor(mm));
            }

        }

        void Section(string text)
        {
            list.Children.Add(new TextBlock { Text = text, Foreground = ToolbarWindow.Sub, FontSize = 11, Margin = new Thickness(4, 10, 0, 3) });
        }

        Button MakeButton(object content, bool on)
        {
            var b = new Button
            {
                Content = content, Foreground = ToolbarWindow.Fg, Focusable = false, Cursor = Cursors.Hand,
                Template = ToolbarWindow.FlatTpl, Background = on ? ToolbarWindow.Accent : Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            b.MouseEnter += (s, e) => { if (!on) b.Background = ToolbarWindow.Hover; };
            b.MouseLeave += (s, e) => { if (!on) b.Background = Brushes.Transparent; };
            return b;
        }

        void Item(string text, bool on, Action act)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Left };
            string mark = on ? "●" : "○";
            sp.Children.Add(new TextBlock { Text = mark, Width = 22, FontSize = 14 });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 13 });
            var b = MakeButton(sp, on);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(0, 1, 0, 1);
            AutomationProperties.SetName(b, text);
            // 고르면 적용하고 창 닫기
            b.Click += (s, e) => { act(); Close(); };
            list.Children.Add(b);
        }

        void PlaceNextToToolbar()
        {
            var h = new WindowInteropHelper(this).Handle;
            var th = new WindowInteropHelper(toolbar).Handle;
            Native.RECT tr, me;
            if (!Native.GetWindowRect(th, out tr) || !Native.GetWindowRect(h, out me)) return;
            int w = me.right - me.left, hh = me.bottom - me.top;
            var wa = WinForms.Screen.FromHandle(th).WorkingArea;
            int x = tr.right + 8;
            if (x + w > wa.Right) x = Math.Max(wa.Left, tr.left - 8 - w);
            int y = Math.Max(wa.Top, Math.Min(tr.top, wa.Bottom - hh));
            Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        }

        void ShowIds()
        {
            foreach (var m in Monitors.GetAll())
            {
                var w = new IdentifyWindow(m, m.Device == ov.DrawMon.Device);
                w.Show();
                w.Place();
                ids.Add(w);
            }
        }

        void CloseIds()
        {
            foreach (var w in ids) w.Close();
            ids.Clear();
        }
    }

    // ================= 모니터 번호 표시 =================
    public class IdentifyWindow : Window
    {
        readonly MonitorInfo mon;

        public IdentifyWindow(MonitorInfo m, bool current)
        {
            mon = m;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Width = 320;
            Height = 200;
            var b = m.Screen.Bounds;
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = m.Number.ToString(), FontSize = 90, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock
            {
                Text = b.Width + "×" + b.Height + (m.Wacom ? "  와콤" : "") + (current ? "  · 현재 판서 중" : ""),
                FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Malgun Gothic")
            });
            Content = new Border
            {
                Background = new SolidColorBrush(current ? Color.FromArgb(0xE6, 0x2F, 0x6F, 0xEB) : Color.FromArgb(0xE6, 0x20, 0x22, 0x27)),
                CornerRadius = new CornerRadius(20),
                Child = new Viewbox { Child = sp, Margin = new Thickness(18) }
            };
        }

        public void Place()
        {
            var h = new WindowInteropHelper(this).Handle;
            var b = mon.Screen.Bounds;
            int w = 320, hh = 200;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, b.X + (b.Width - w) / 2, b.Y + (b.Height - hh) / 2, w, hh, Native.SWP_NOACTIVATE);
        }
    }

    // ================= 단축키 설정 창 =================
    public class HotkeyWindow : Window
    {
        readonly OverlayWindow ov;
        readonly Dictionary<string, KeyCombo> edits = new Dictionary<string, KeyCombo>();
        readonly Dictionary<string, TextBox> boxes = new Dictionary<string, TextBox>();
        readonly Dictionary<string, CheckBox> checks = new Dictionary<string, CheckBox>();
        readonly TextBlock status;
        bool loading;

        public HotkeyWindow(OverlayWindow o)
        {
            ov = o;
            Title = "단축키 설정";
            Width = 500;
            Height = 660;
            Topmost = true;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Malgun Gothic");
            FontSize = 13;
            InputMethod.SetIsInputMethodEnabled(this, false);
            foreach (var kv in ov.Hotkeys) edits[kv.Key] = kv.Value.Clone();

            var dock = new DockPanel { Margin = new Thickness(14) };
            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Text = "입력 칸을 클릭한 뒤 원하는 키(조합)를 누르세요.\n" +
                       "'어디서나'를 켜면 다른 프로그램을 쓰는 중에도 동작합니다 (Ctrl·Alt 조합 권장).\n" +
                       "와콤 익스프레스키·펜 버튼에 이 단축키를 지정해서 쓸 수도 있습니다."
            };
            DockPanel.SetDock(intro, Dock.Top);
            dock.Children.Add(intro);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var reset = new Button { Content = "기본값으로", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "취소", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var save = new Button { Content = "저장", Padding = new Thickness(16, 4, 16, 4), FontWeight = FontWeights.Bold };
            buttons.Children.Add(reset);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);

            status = new TextBlock { Foreground = Brushes.Chocolate, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(status, Dock.Bottom);
            dock.Children.Add(status);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            foreach (var a in HotkeyStore.Actions)
            {
                var id = a.Id;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new TextBlock { Text = a.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
                var tb = new TextBox
                {
                    IsReadOnly = true, IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 3, 6, 3),
                    Padding = new Thickness(4, 3, 4, 3), Cursor = Cursors.Hand, Tag = id
                };
                tb.PreviewKeyDown += OnCapture;
                tb.GotKeyboardFocus += (s, e) => tb.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xD5));
                tb.LostKeyboardFocus += (s, e) => tb.Background = Brushes.White;
                var cb = new CheckBox { Content = "어디서나", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                cb.Checked += (s, e) => OnGlobalChanged(id, true);
                cb.Unchecked += (s, e) => OnGlobalChanged(id, false);
                var clear = new Button { Content = "지우기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 3, 0, 3) };
                clear.Click += (s, e) => { edits[id] = new KeyCombo { Global = edits[id].Global }; boxes[id].Text = ""; };
                Grid.SetRow(name, row); Grid.SetColumn(name, 0);
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
                Grid.SetRow(cb, row); Grid.SetColumn(cb, 2);
                Grid.SetRow(clear, row); Grid.SetColumn(clear, 3);
                grid.Children.Add(name);
                grid.Children.Add(tb);
                grid.Children.Add(cb);
                grid.Children.Add(clear);
                boxes[id] = tb;
                checks[id] = cb;
                row++;
            }
            dock.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = dock;
            ShowAll();

            reset.Click += (s, e) =>
            {
                edits.Clear();
                foreach (var kv in HotkeyStore.Defaults()) edits[kv.Key] = kv.Value;
                ShowAll();
                status.Text = "기본값으로 되돌렸습니다. 저장을 눌러야 적용됩니다.";
            };
            save.Click += (s, e) => { ov.ApplyHotkeys(edits); Close(); };

            // 설정하는 동안에는 전역 단축키를 잠시 해제해야 키 입력을 받을 수 있음
            SourceInitialized += (s, e) => ov.UnregisterGlobalHotkeys();
            Closed += (s, e) => ov.RegisterGlobalHotkeys();
            Loaded += (s, e) => CenterOnDrawMonitor();
        }

        void ShowAll()
        {
            loading = true;
            foreach (var a in HotkeyStore.Actions)
            {
                boxes[a.Id].Text = edits[a.Id].Display;
                checks[a.Id].IsChecked = edits[a.Id].Global;
            }
            loading = false;
        }

        void CenterOnDrawMonitor()
        {
            var ps = PresentationSource.FromVisual(this);
            double sc = ps != null && ps.CompositionTarget != null ? ps.CompositionTarget.TransformToDevice.M11 : 1.0;
            var wa = ov.DrawMon.Screen.WorkingArea;
            int w = (int)(ActualWidth * sc), h = (int)(ActualHeight * sc);
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
                wa.X + Math.Max(0, (wa.Width - w) / 2), wa.Y + Math.Max(0, (wa.Height - h) / 2), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        }

        void OnGlobalChanged(string id, bool on)
        {
            if (loading) return;
            edits[id].Global = on;
            if (on && !edits[id].IsEmpty && edits[id].Mods == ModifierKeys.None)
                status.Text = "주의: 조합 없이 한 글자 키를 '어디서나'로 지정하면 다른 프로그램에서 그 키를 입력할 수 없습니다.";
            else
                status.Text = "";
        }

        void OnCapture(object sender, KeyEventArgs e)
        {
            var tb = (TextBox)sender;
            string id = (string)tb.Tag;
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : (e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt ||
                k == Key.LeftShift || k == Key.RightShift || k == Key.LWin || k == Key.RWin || k == Key.None) return;
            if (k == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = false; return; }

            var c = new KeyCombo { Mods = Keyboard.Modifiers, Key = k, Global = edits[id].Global };
            string msg = "";
            foreach (var a in HotkeyStore.Actions)
            {
                if (a.Id != id && edits[a.Id].SameKeys(c))
                {
                    edits[a.Id] = new KeyCombo { Global = edits[a.Id].Global };
                    boxes[a.Id].Text = "";
                    msg = "'" + a.Name + "'에 같은 키가 있어서 그쪽은 해제했습니다. ";
                }
            }
            edits[id] = c;
            tb.Text = c.Display;
            if (c.Global && c.Mods == ModifierKeys.None)
                msg += "주의: 조합 없이 한 글자 키를 '어디서나'로 지정하면 다른 프로그램에서 그 키를 입력할 수 없습니다.";
            status.Text = msg;
        }
    }
}
