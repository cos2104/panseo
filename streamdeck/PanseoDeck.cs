// 판서 Stream Deck 플러그인 (PanseoDeck.exe)
// Stream Deck 앱이 실행하는 플러그인 프로그램.
//  - 버튼을 누르면 판서 프로그램에 명령을 보냄 (같은 PC 안의 파이프 \\.\pipe\Panseo)
//  - 판서의 지금 상태(도구·색·확대·숨기기 등)를 받아 버튼 그림을 다시 그림 (판서 도구 모음과 같은 아이콘)
// 빌드: build.bat   /   그림·manifest 만들기: PanseoDeck.exe --setup <플러그인 폴더>   /   설치 파일: --pack <폴더> <파일>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PanseoDeck
{
    enum Kind { Tool, Command, Color, Bg, Shape }

    // 버튼(동작) 하나의 정의
    class Def
    {
        public string Id, Name, Tip, Glyph, Cmd;
        public Kind Kind;
        public Func<Dictionary<string, object>, bool> Active; // 판서 상태로 "켜짐" 판단 (파란 바탕)
    }

    static class Defs
    {
        public const string Prefix = "com.cos2104.panseo.";
        public static readonly List<Def> All = new List<Def>();

        static string S(Dictionary<string, object> s, string key)
        {
            object v;
            return s != null && s.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static bool B(Dictionary<string, object> s, string key)
        {
            object v;
            return s != null && s.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static void Tool(string id, string name, string glyph, string cmd, string tool, string tip)
        {
            All.Add(new Def { Id = id, Name = name, Glyph = glyph, Cmd = cmd, Kind = Kind.Tool, Tip = tip, Active = s => S(s, "tool") == tool });
        }

        static void Cmd(string id, string name, string glyph, string cmd, string tip, Func<Dictionary<string, object>, bool> active)
        {
            All.Add(new Def { Id = id, Name = name, Glyph = glyph, Cmd = cmd, Kind = Kind.Command, Tip = tip, Active = active });
        }

        static Defs()
        {
            // 도구 (판서 도구 모음과 같은 아이콘)
            Tool("pen", "펜", "\uE70F", "run pen", "Pen", "펜 (필압 지원)");
            Tool("highlighter", "형광펜", "\uE7E6", "run highlighter", "Highlighter", "형광펜");
            Tool("text", "텍스트", "\uE8D2", "run text", "Text", "누른 곳에 글자 입력");
            All.Add(new Def { Id = "shape", Name = "도형", Kind = Kind.Shape, Tip = "직선·화살표·네모·동그라미 (속성에서 고르기)" });
            Tool("eraser", "획 지우개", "\uE75C", "run eraser", "EraseStroke", "획 단위로 지우기");
            Tool("eraserpoint", "부분 지우개", "\uE75C", "run eraser_point", "ErasePoint", "문지른 부분만 지우기");
            Tool("mouse", "마우스", "\uE7C9", "run mouse", "Mouse", "뒤의 프로그램 조작 (확대 중에는 확대된 화면을 클릭)");
            Tool("pan", "화면 이동", "\uE7C2", "run pan", "Pan", "확대 화면 끌어서 이동");
            // 색·배경
            All.Add(new Def { Id = "color", Name = "색", Kind = Kind.Color, Tip = "펜 색 (속성에서 고르기)" });
            All.Add(new Def { Id = "bg", Name = "배경", Kind = Kind.Bg, Tip = "투명·흰판·칠판 (속성에서 고르기)" });
            // 동작
            Cmd("undo", "되돌리기", "\uE7A7", "run undo", "되돌리기", null);
            Cmd("redo", "다시 실행", "\uE7A6", "run redo", "다시 실행", null);
            Cmd("clear", "전체 삭제", "\uE74D", "run clear", "판서 전체 삭제 (되돌리기 가능)", null);
            Cmd("hide", "필기 숨기기", "\uE7B3", "run hide_ink", "필기 숨기기/보이기", s => B(s, "hidden"));
            Cmd("zoom", "라이브 확대", "\uE71E", "run zoom", "누른 뒤 확대할 곳 고르기, 다시 누르면 끄기", s => B(s, "zooming") || B(s, "selecting"));
            Cmd("zoomin", "확대 +", "\uE8A3", "run zoom_in", "확대 배율 올리기", null);
            Cmd("zoomout", "확대 −", "\uE71F", "run zoom_out", "확대 배율 내리기", null);
            Cmd("sizeup", "굵게", "\uE710", "run size_up", "펜 굵기 키우기", null);
            Cmd("sizedown", "가늘게", "\uE738", "run size_down", "펜 굵기 줄이기", null);
            Cmd("toggle", "판서↔마우스", "\uE8AB", "run mouse_toggle", "판서와 마우스 모드 바꾸기", null);
            Cmd("capture", "화면 저장", "\uE722", "run capture", "화면 캡처 (사진\\판서 폴더)", null);
            Cmd("scrollfollow", "스크롤 따라가기", "\u2195", "run scroll_follow", "스크롤 따라가기 켜기/끄기 (실험)", s => B(s, "scrollFollow"));
            Cmd("toolbar", "도구 모음", "\uE700", "run toolbar", "판서 도구 모음 숨기기/보이기", null);
        }

        public static Def Find(string uuid)
        {
            if (uuid == null || !uuid.StartsWith(Prefix)) return null;
            string id = uuid.Substring(Prefix.Length);
            foreach (var d in All) if (d.Id == id) return d;
            return null;
        }

        // ---- 속성(설정) 값 ----
        public static string Setting(Dictionary<string, object> settings, string key, string def)
        {
            object v;
            return settings != null && settings.TryGetValue(key, out v) && v != null && v.ToString() != "" ? v.ToString() : def;
        }

        public static string ColorOf(Dictionary<string, object> st) { return Setting(st, "color", "#E53935").ToUpperInvariant(); }
        public static string ShapeOf(Dictionary<string, object> st) { return Setting(st, "kind", "line"); }
        public static string BgOf(Dictionary<string, object> st) { return Setting(st, "mode", "cycle"); }

        public static readonly Dictionary<string, string> ShapeNames = new Dictionary<string, string>
        {
            { "line", "직선" }, { "arrow", "화살표" }, { "rect", "네모" }, { "ellipse", "동그라미" }
        };
        public static readonly Dictionary<string, string> ShapeEnum = new Dictionary<string, string>
        {
            { "line", "Line" }, { "arrow", "Arrow" }, { "rect", "Rect" }, { "ellipse", "Ellipse" }
        };
        public static readonly Dictionary<string, string> BgNames = new Dictionary<string, string>
        {
            { "cycle", "배경" }, { "transparent", "투명" }, { "white", "흰판" }, { "chalk", "칠판" }
        };
        public static readonly Dictionary<string, string> BgEnum = new Dictionary<string, string>
        {
            { "transparent", "Transparent" }, { "white", "White" }, { "chalk", "Chalk" }
        };

        // 판서에 보낼 명령
        public static string CommandFor(Def d, Dictionary<string, object> st)
        {
            switch (d.Kind)
            {
                case Kind.Color: return "color " + ColorOf(st);
                case Kind.Shape: return "run shape_" + ShapeOf(st);
                case Kind.Bg: string m = BgOf(st); return m == "cycle" ? "run bg" : "bg " + m;
                default: return d.Cmd;
            }
        }

        // 버튼에 "켜짐"(파란 바탕)으로 보일지
        public static bool IsActive(Def d, Dictionary<string, object> st, Dictionary<string, object> state)
        {
            if (state == null) return false;
            switch (d.Kind)
            {
                case Kind.Color:
                    string tool = S(state, "tool");
                    return (tool == "Pen" || tool == "Highlighter" || tool == "Text" || tool == "Shape") &&
                           string.Equals(S(state, "color"), ColorOf(st), StringComparison.OrdinalIgnoreCase);
                case Kind.Shape:
                    string e;
                    return S(state, "tool") == "Shape" && ShapeEnum.TryGetValue(ShapeOf(st), out e) && S(state, "shape") == e;
                case Kind.Bg:
                    string b;
                    return BgEnum.TryGetValue(BgOf(st), out b) && S(state, "bg") == b;
                default:
                    return d.Active != null && d.Active(state);
            }
        }

        // 버튼 아래 글자 (상태에 따라 바뀌는 것 포함)
        public static string LabelFor(Def d, Dictionary<string, object> st, Dictionary<string, object> state)
        {
            switch (d.Kind)
            {
                case Kind.Shape: string n; return ShapeNames.TryGetValue(ShapeOf(st), out n) ? n : "도형";
                case Kind.Color: return "";
                case Kind.Bg:
                    string mode = BgOf(st);
                    if (mode == "cycle" && state != null)
                    {
                        string cur = S(state, "bg");
                        return cur == "White" ? "흰판" : cur == "Chalk" ? "칠판" : "투명";
                    }
                    string bn; return BgNames.TryGetValue(mode, out bn) ? bn : "배경";
            }
            if (state != null)
            {
                if (d.Id == "zoom" && B(state, "zooming")) return S(state, "zoom") + "배";
                if (d.Id == "zoom" && B(state, "selecting")) return "위치 고르기";
                if (d.Id == "sizeup" || d.Id == "sizedown") return d.Name + " " + S(state, "size");
            }
            return d.Name;
        }

        // cycle 배경 버튼은 지금 배경을 그림으로 보여 줌
        public static string BgShown(Def d, Dictionary<string, object> st, Dictionary<string, object> state)
        {
            string mode = BgOf(st);
            if (mode != "cycle") return mode;
            string cur = S(state, "bg");
            return cur == "White" ? "white" : cur == "Chalk" ? "chalk" : "transparent";
        }
    }

    // ================= 그림 (판서 도구 모음과 같은 색·아이콘) =================
    static class Art
    {
        static readonly Color Panel = Color.FromArgb(0x20, 0x22, 0x27);
        static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xEB);
        static readonly Color Fg = Color.FromArgb(0xEE, 0xEE, 0xEE);
        static string iconFont;

        static string IconFont
        {
            get
            {
                if (iconFont == null)
                {
                    iconFont = "Segoe MDL2 Assets";
                    using (var fc = new InstalledFontCollection())
                        foreach (var f in fc.Families) if (f.Name == "Segoe Fluent Icons") iconFont = f.Name;
                }
                return iconFont;
            }
        }

        static Graphics Prepare(Bitmap bmp)
        {
            var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            return g;
        }

        // 버튼 그림: 어두운 바탕(켜짐이면 파랑) + 아이콘 + 아래 이름
        public static Bitmap Key(Def d, Dictionary<string, object> st, Dictionary<string, object> state, bool offline, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prepare(bmp))
            {
                bool active = Defs.IsActive(d, st, state);
                g.Clear(active ? Accent : Panel);
                float k = size / 144f;
                int alpha = offline ? 130 : 255;
                string label = Defs.LabelFor(d, st, state);
                // 아이콘 영역: 이름이 없으면(색 버튼) 가운데 크게
                var area = string.IsNullOrEmpty(label) ? new RectangleF(0, 0, size, size) : new RectangleF(0, 6 * k, size, 92 * k);
                DrawContent(g, d, st, state, area, k, alpha, active, false);
                if (!string.IsNullOrEmpty(label))
                {
                    float fs = (label.Length > 5 ? 15 : 18) * k;
                    using (var font = new Font("Malgun Gothic", fs, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var br = new SolidBrush(Color.FromArgb(alpha, Fg)))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(label, font, br, new RectangleF(2 * k, 100 * k, size - 4 * k, 36 * k), sf);
                }
            }
            return bmp;
        }

        // 동작 목록 아이콘: 투명 바탕 + 흰 아이콘
        public static Bitmap ListIcon(Def d, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prepare(bmp))
                DrawContent(g, d, null, null, new RectangleF(0, 0, size, size), size / 96f, 255, false, true);
            return bmp;
        }

        // 플러그인 아이콘: 파란 둥근 네모 + 흰 펜
        public static Bitmap PluginIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prepare(bmp))
            {
                float r = size * 0.22f;
                using (var path = Rounded(new RectangleF(0, 0, size, size), r))
                using (var br = new SolidBrush(Accent))
                    g.FillPath(br, path);
                Glyph(g, "\uE70F", new RectangleF(0, 0, size, size), size * 0.56f, Color.White);
            }
            return bmp;
        }

        // 분류 아이콘: 투명 바탕 + 흰 펜
        public static Bitmap CategoryIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Prepare(bmp))
                Glyph(g, "\uE70F", new RectangleF(0, 0, size, size), size * 0.8f, Color.White);
            return bmp;
        }

        static void DrawContent(Graphics g, Def d, Dictionary<string, object> st, Dictionary<string, object> state, RectangleF area, float k, int alpha, bool active, bool listIcon)
        {
            var white = Color.FromArgb(alpha, Color.White);
            float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
            switch (d.Kind)
            {
                case Kind.Color:
                    {
                        var c = ParseColor(Defs.ColorOf(st));
                        float r = (listIcon ? 38 : 40) * k;
                        if (active)
                            using (var ring = new Pen(Color.White, 6 * k)) g.DrawEllipse(ring, cx - r - 8 * k, cy - r - 8 * k, (r + 8 * k) * 2, (r + 8 * k) * 2);
                        using (var br = new SolidBrush(Color.FromArgb(alpha, c))) g.FillEllipse(br, cx - r, cy - r, r * 2, r * 2);
                        using (var pen = new Pen(Color.FromArgb(alpha, 0x88, 0x88, 0x88), 2 * k)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                        return;
                    }
                case Kind.Bg:
                    {
                        string mode = listIcon ? "white" : Defs.BgShown(d, st, state);
                        float w = 66 * k, h = 48 * k;
                        var rect = new RectangleF(cx - w / 2, cy - h / 2, w, h);
                        using (var path = Rounded(rect, 6 * k))
                        {
                            if (mode == "transparent" && !listIcon)
                            {
                                // 투명: 바둑판 무늬
                                var old = g.Clip;
                                g.SetClip(path);
                                float cell = 8 * k;
                                for (int iy = 0; iy * cell < h; iy++)
                                    for (int ix = 0; ix * cell < w; ix++)
                                        using (var br = new SolidBrush((ix + iy) % 2 == 0 ? Color.FromArgb(alpha, 0xCC, 0xCC, 0xCC) : Color.FromArgb(alpha, 0x88, 0x88, 0x88)))
                                            g.FillRectangle(br, rect.X + ix * cell, rect.Y + iy * cell, cell, cell);
                                g.Clip = old;
                            }
                            else
                            {
                                var fill = mode == "chalk" ? Color.FromArgb(alpha, 0x1F, 0x3B, 0x2D) : Color.FromArgb(alpha, Color.White);
                                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                                if (mode == "chalk")
                                    using (var chalk = new Pen(Color.FromArgb(alpha, 0xEE, 0xEE, 0xEE), 3 * k))
                                        g.DrawBezier(chalk, rect.X + 12 * k, rect.Y + 32 * k, rect.X + 24 * k, rect.Y + 12 * k, rect.X + 40 * k, rect.Y + 40 * k, rect.X + 54 * k, rect.Y + 16 * k);
                            }
                            using (var pen = new Pen(Color.FromArgb(alpha, 0xAA, 0xAA, 0xAA), 2 * k)) g.DrawPath(pen, path);
                        }
                        if (Defs.BgOf(st) == "cycle" && !listIcon)
                            Glyph(g, "\uE72C", new RectangleF(rect.Right - 14 * k, rect.Bottom - 16 * k, 24 * k, 24 * k), 16 * k, white); // 순환 표시
                        return;
                    }
                case Kind.Shape:
                    {
                        string kind = listIcon ? "rect" : Defs.ShapeOf(st);
                        using (var pen = new Pen(white, 7 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            switch (kind)
                            {
                                case "line": g.DrawLine(pen, cx - 28 * k, cy + 24 * k, cx + 28 * k, cy - 24 * k); break;
                                case "arrow":
                                    g.DrawLine(pen, cx - 28 * k, cy + 24 * k, cx + 28 * k, cy - 24 * k);
                                    g.DrawLines(pen, new[] { new PointF(cx + 4 * k, cy - 26 * k), new PointF(cx + 28 * k, cy - 24 * k), new PointF(cx + 26 * k, cy) });
                                    break;
                                case "ellipse": g.DrawEllipse(pen, cx - 32 * k, cy - 24 * k, 64 * k, 48 * k); break;
                                default: g.DrawRectangle(pen, cx - 30 * k, cy - 23 * k, 60 * k, 46 * k); break;
                            }
                        }
                        return;
                    }
            }
            if (d.Id == "scrollfollow")
            {
                using (var font = new Font("Segoe UI Symbol", 54 * k, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(white))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(d.Glyph, font, br, area, sf);
                return;
            }
            Glyph(g, d.Glyph, area, 54 * k, white);
            if (d.Id == "eraserpoint") // 부분 지우개: 작은 원으로 구분
                using (var pen = new Pen(white, 3 * k)) g.DrawEllipse(pen, cx + 14 * k, cy + 10 * k, 18 * k, 18 * k);
        }

        static void Glyph(Graphics g, string glyph, RectangleF area, float px, Color color)
        {
            using (var font = new Font(IconFont, px, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(glyph, font, br, area, sf);
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color ParseColor(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); } catch { return Color.Red; }
        }

        public static string DataUrl(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
    }

    // ================= 판서 프로그램 연결 (\\.\pipe\Panseo) =================
    class PanseoPipe
    {
        NamedPipeClientStream stream;
        StreamReader reader;
        StreamWriter writer;
        DateTime nextTry = DateTime.MinValue;
        readonly object gate = new object();

        // 명령 한 줄 보내고 상태(JSON) 받기. 판서가 꺼져 있으면 null
        public string Send(string cmd, bool force)
        {
            lock (gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (stream == null)
                    {
                        if (!force && DateTime.Now < nextTry) return null;
                        try
                        {
                            stream = new NamedPipeClientStream(".", "Panseo", PipeDirection.InOut);
                            stream.Connect(300);
                            var enc = new UTF8Encoding(false);
                            reader = new StreamReader(stream, enc);
                            writer = new StreamWriter(stream, enc) { AutoFlush = true, NewLine = "\n" };
                        }
                        catch
                        {
                            Close();
                            nextTry = DateTime.Now.AddSeconds(1.5);
                            return null;
                        }
                    }
                    try
                    {
                        writer.WriteLine(cmd);
                        string line = reader.ReadLine();
                        if (line != null) return line;
                    }
                    catch { }
                    Close(); // 끊겼으면 한 번 더 연결해 봄
                }
                return null;
            }
        }

        void Close()
        {
            try { if (stream != null) stream.Dispose(); } catch { }
            stream = null; reader = null; writer = null;
        }
    }

    // ================= Stream Deck 연결 =================
    class Plugin
    {
        class Ctx
        {
            public Def Def;
            public Dictionary<string, object> Settings;
            public string LastKey;
        }

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Dictionary<string, Ctx> contexts = new Dictionary<string, Ctx>();
        readonly object ctxGate = new object();
        readonly PanseoPipe pipe = new PanseoPipe();
        ClientWebSocket ws;
        readonly object sendGate = new object();
        string pluginUuid;
        Dictionary<string, object> state;     // 판서의 지금 상태 (꺼져 있으면 null)
        string exePath;                       // 판서 프로그램 위치 (판서가 알려 준 것을 기억)
        string pending;                       // 판서를 켜는 동안 기다리는 명령
        DateTime pendingUntil;

        public void Run(string[] args)
        {
            int port = 0;
            string registerEvent = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "-pluginUUID") pluginUuid = args[i + 1];
                else if (args[i] == "-registerEvent") registerEvent = args[i + 1];
            }
            if (port == 0 || pluginUuid == null) return;

            ws = new ClientWebSocket();
            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None).Wait();
            Send(new Dictionary<string, object> { { "event", registerEvent }, { "uuid", pluginUuid } });
            Send(new Dictionary<string, object> { { "event", "getGlobalSettings" }, { "context", pluginUuid } });

            new Thread(PollLoop) { IsBackground = true }.Start();

            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                try { r = ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result; }
                catch { break; }
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                string text = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);
                try { Handle(text); } catch (Exception ex) { Log("handle: " + ex.Message); }
            }
        }

        void Handle(string text)
        {
            var m = json.Deserialize<Dictionary<string, object>>(text);
            string ev = Str(m, "event"), context = Str(m, "context"), action = Str(m, "action");
            var payload = m.ContainsKey("payload") ? m["payload"] as Dictionary<string, object> : null;
            var settings = payload != null && payload.ContainsKey("settings") ? payload["settings"] as Dictionary<string, object> : null;
            switch (ev)
            {
                case "willAppear":
                    {
                        var def = Defs.Find(action);
                        if (def == null) return;
                        var ctx = new Ctx { Def = def, Settings = settings ?? new Dictionary<string, object>() };
                        lock (ctxGate) contexts[context] = ctx;
                        Refresh(context, ctx);
                        break;
                    }
                case "willDisappear":
                    lock (ctxGate) contexts.Remove(context);
                    break;
                case "didReceiveSettings":
                    {
                        Ctx ctx;
                        lock (ctxGate) contexts.TryGetValue(context, out ctx);
                        if (ctx == null) return;
                        ctx.Settings = settings ?? new Dictionary<string, object>();
                        ctx.LastKey = null;
                        Refresh(context, ctx);
                        break;
                    }
                case "keyDown":
                    {
                        Ctx ctx;
                        lock (ctxGate) contexts.TryGetValue(context, out ctx);
                        if (ctx != null) Press(context, ctx);
                        break;
                    }
                case "didReceiveGlobalSettings":
                    if (settings != null && settings.ContainsKey("exe")) exePath = Str(settings, "exe");
                    break;
            }
        }

        void Press(string context, Ctx ctx)
        {
            string cmd = Defs.CommandFor(ctx.Def, ctx.Settings);
            string reply = pipe.Send(cmd, true);
            if (reply != null)
            {
                Apply(reply);
                return;
            }
            // 판서가 꺼져 있으면 켜고, 켜지면 이 명령을 실행
            string path = FindPanseo();
            if (path == null)
            {
                Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", context } });
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = true });
                pending = cmd;
                pendingUntil = DateTime.Now.AddSeconds(20);
                Send(new Dictionary<string, object> { { "event", "showOk" }, { "context", context } });
            }
            catch (Exception ex)
            {
                Log("launch: " + ex.Message);
                Send(new Dictionary<string, object> { { "event", "showAlert" }, { "context", context } });
            }
        }

        string FindPanseo()
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) return exePath;
            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "판서프로그램", "Panseo.exe");
            return File.Exists(desk) ? desk : null;
        }

        // 판서 상태를 주기적으로 받아 버튼 그림을 맞춤
        void PollLoop()
        {
            while (true)
            {
                int count;
                lock (ctxGate) count = contexts.Count;
                if (count == 0) { Thread.Sleep(1000); continue; }
                string reply = pipe.Send("state", pending != null);
                if (reply != null && pending != null)
                {
                    if (DateTime.Now < pendingUntil)
                    {
                        Thread.Sleep(300); // 판서 창이 다 뜰 때까지 잠깐
                        reply = pipe.Send(pending, true) ?? reply;
                    }
                    pending = null;
                }
                if (pending != null && DateTime.Now > pendingUntil) pending = null;
                if (reply != null) Apply(reply);
                else if (state != null) { state = null; RefreshAll(); }
                else RefreshAll();
                Thread.Sleep(400);
            }
        }

        void Apply(string reply)
        {
            try
            {
                state = json.Deserialize<Dictionary<string, object>>(reply);
                string exe = Str(state, "exe");
                if (!string.IsNullOrEmpty(exe) && exe != exePath)
                {
                    exePath = exe;
                    Send(new Dictionary<string, object> { { "event", "setGlobalSettings" }, { "context", pluginUuid }, { "payload", new Dictionary<string, object> { { "exe", exe } } } });
                }
            }
            catch { state = null; }
            RefreshAll();
        }

        void RefreshAll()
        {
            List<KeyValuePair<string, Ctx>> list;
            lock (ctxGate) list = new List<KeyValuePair<string, Ctx>>(contexts);
            foreach (var kv in list) Refresh(kv.Key, kv.Value);
        }

        // 그림이 실제로 바뀔 때만 다시 보냄
        void Refresh(string context, Ctx ctx)
        {
            var st = state;
            bool offline = st == null;
            string key = string.Join("|", new[]
            {
                ctx.Def.Id, offline ? "off" : "on", Defs.IsActive(ctx.Def, ctx.Settings, st) ? "1" : "0",
                Defs.LabelFor(ctx.Def, ctx.Settings, st), Defs.ColorOf(ctx.Settings), Defs.ShapeOf(ctx.Settings), Defs.BgOf(ctx.Settings),
                ctx.Def.Kind == Kind.Bg ? Defs.BgShown(ctx.Def, ctx.Settings, st) : ""
            });
            if (key == ctx.LastKey) return;
            ctx.LastKey = key;
            using (var bmp = Art.Key(ctx.Def, ctx.Settings, st, offline, 144))
                Send(new Dictionary<string, object>
                {
                    { "event", "setImage" }, { "context", context },
                    { "payload", new Dictionary<string, object> { { "image", Art.DataUrl(bmp) }, { "target", 0 } } }
                });
        }

        void Send(Dictionary<string, object> msg)
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(msg));
            lock (sendGate)
            {
                try { ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
                catch (Exception ex) { Log("send: " + ex.Message); }
            }
        }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            return m != null && m.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        public static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Panseo");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "streamdeck.log"), DateTime.Now.ToString("MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ================= manifest·그림·설치 파일 만들기 =================
    static class Setup
    {
        public const string Version = "1.0.0.0";

        public static void Make(string dir)
        {
            Directory.CreateDirectory(dir);
            SavePair(Art.PluginIcon(256), Art.PluginIcon(512), Path.Combine(dir, "imgs", "plugin", "icon"));
            SavePair(Art.CategoryIcon(28), Art.CategoryIcon(56), Path.Combine(dir, "imgs", "plugin", "category"));
            var actions = new List<object>();
            foreach (var d in Defs.All)
            {
                string folder = Path.Combine(dir, "imgs", "actions", d.Id);
                SavePair(Art.ListIcon(d, 20), Art.ListIcon(d, 40), Path.Combine(folder, "icon"));
                SavePair(Art.Key(d, null, null, false, 72), Art.Key(d, null, null, false, 144), Path.Combine(folder, "key"));
                actions.Add(new Dictionary<string, object>
                {
                    { "UUID", Defs.Prefix + d.Id },
                    { "Name", d.Name },
                    { "Tooltip", d.Tip },
                    { "Icon", "imgs/actions/" + d.Id + "/icon" },
                    { "Controllers", new[] { "Keypad" } },
                    { "SupportedInMultiActions", true },
                    { "States", new[] { new Dictionary<string, object> { { "Image", "imgs/actions/" + d.Id + "/key" }, { "ShowTitle", true } } } }
                });
            }
            var manifest = new Dictionary<string, object>
            {
                { "UUID", "com.cos2104.panseo" },
                { "Name", "판서" },
                { "Author", "cos2104" },
                { "Description", "와콤·전자칠판 판서 프로그램 '판서'를 Stream Deck 버튼으로 조작합니다. 지금 도구·색·확대 상태가 버튼에 표시됩니다." },
                { "Version", Version },
                { "SDKVersion", 2 },
                { "Software", new Dictionary<string, object> { { "MinimumVersion", "6.4" } } },
                { "OS", new[] { new Dictionary<string, object> { { "Platform", "windows" }, { "MinimumVersion", "10" } } } },
                { "CodePath", "PanseoDeck.exe" },
                { "Icon", "imgs/plugin/icon" },
                { "Category", "판서" },
                { "CategoryIcon", "imgs/plugin/category" },
                { "PropertyInspectorPath", "pi.html" },
                { "URL", "https://github.com/cos2104/panseo" },
                { "Actions", actions }
            };
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Pretty(new JavaScriptSerializer().Serialize(manifest)), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "pi.html"), PiHtml, new UTF8Encoding(false));
        }

        static void SavePair(Bitmap one, Bitmap two, string basePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(basePath));
            using (one) one.Save(basePath + ".png", ImageFormat.Png);
            using (two) two.Save(basePath + "@2x.png", ImageFormat.Png);
        }

        // 설치 파일(.streamDeckPlugin) = 플러그인 폴더를 통째로 담은 zip
        public static void Pack(string dir, string outFile)
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            if (File.Exists(outFile)) File.Delete(outFile);
            string root = Path.GetFileName(dir);
            using (var zip = System.IO.Compression.ZipFile.Open(outFile, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(dir.Length + 1).Replace('\\', '/');
                    if (rel.EndsWith(".log")) continue;
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, root + "/" + rel);
                }
        }

        // JSON 보기 좋게 (들여쓰기)
        static string Pretty(string s)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool str = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) str = !str;
                if (str) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[': sb.Append(c).Append('\n').Append(new string(' ', ++indent * 2)); break;
                    case '}': case ']': sb.Append('\n').Append(new string(' ', --indent * 2)).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(new string(' ', indent * 2)); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // 속성 창 (Stream Deck 앱 오른쪽 아래): 색·도형·배경 고르기
        const string PiHtml = @"<!DOCTYPE html>
<html lang=""ko"">
<head>
<meta charset=""utf-8"">
<style>
  body { font-family: 'Malgun Gothic', sans-serif; font-size: 12px; color: #d8d8d8; background: #2d2d2d; margin: 0; padding: 10px 14px; }
  .row { display: flex; align-items: center; margin: 9px 0; }
  .row > label { width: 78px; color: #9a9a9a; flex: none; }
  select, input[type=color] { flex: 1; background: #3d3d3d; color: #ddd; border: 1px solid #555; border-radius: 3px; height: 24px; }
  input[type=color] { padding: 0 2px; }
  .sw { display: flex; gap: 6px; flex-wrap: wrap; flex: 1; }
  .sw button { width: 22px; height: 22px; border-radius: 50%; border: 2px solid #555; cursor: pointer; padding: 0; }
  .sw button.on { border-color: #fff; box-shadow: 0 0 0 1px #2f6feb; }
  .note { color: #8a8a8a; line-height: 1.6; margin-top: 12px; }
</style>
</head>
<body>
<div id=""color"" hidden>
  <div class=""row""><label>색</label><div class=""sw"" id=""swatches""></div></div>
  <div class=""row""><label>직접 고르기</label><input type=""color"" id=""custom""></div>
</div>
<div id=""shape"" hidden>
  <div class=""row""><label>도형</label>
    <select id=""kind""><option value=""line"">직선</option><option value=""arrow"">화살표</option><option value=""rect"">네모</option><option value=""ellipse"">동그라미</option></select>
  </div>
</div>
<div id=""bg"" hidden>
  <div class=""row""><label>배경</label>
    <select id=""mode""><option value=""cycle"">누를 때마다 바꾸기</option><option value=""transparent"">투명 (화면 위)</option><option value=""white"">흰판</option><option value=""chalk"">칠판</option></select>
  </div>
</div>
<div class=""note"">판서 프로그램이 켜져 있어야 동작합니다.<br>꺼져 있을 때 누르면 판서 프로그램을 켭니다.</div>
<script>
  var ws, uuid, settings = {}, id = '';
  var palette = ['#000000', '#FFFFFF', '#E53935', '#1E63E9', '#2E9E44', '#FDD835', '#FB8C00', '#8E24AA'];
  var names = ['검정', '흰색', '빨강', '파랑', '초록', '노랑', '주황', '보라'];

  function connectElgatoStreamDeckSocket(port, inUUID, registerEvent, info, actionInfo) {
    uuid = inUUID;
    var ai = JSON.parse(actionInfo);
    id = ai.action.split('.').pop();
    settings = (ai.payload && ai.payload.settings) || {};
    ws = new WebSocket('ws://127.0.0.1:' + port);
    ws.onopen = function () { ws.send(JSON.stringify({ event: registerEvent, uuid: uuid })); };
    ws.onmessage = function (e) {
      var m = JSON.parse(e.data);
      if (m.event === 'didReceiveSettings') { settings = m.payload.settings || {}; show(); }
    };
    show();
  }

  function save() { ws.send(JSON.stringify({ event: 'setSettings', context: uuid, payload: settings })); show(); }

  function show() {
    document.getElementById('color').hidden = id !== 'color';
    document.getElementById('shape').hidden = id !== 'shape';
    document.getElementById('bg').hidden = id !== 'bg';
    if (id === 'color') {
      var cur = (settings.color || '#E53935').toUpperCase();
      var box = document.getElementById('swatches');
      box.innerHTML = '';
      palette.forEach(function (c, i) {
        var b = document.createElement('button');
        b.style.background = c;
        b.title = names[i];
        if (c === cur) b.className = 'on';
        b.onclick = function () { settings.color = c; save(); };
        box.appendChild(b);
      });
      document.getElementById('custom').value = cur.toLowerCase();
    }
    if (id === 'shape') document.getElementById('kind').value = settings.kind || 'line';
    if (id === 'bg') document.getElementById('mode').value = settings.mode || 'cycle';
  }

  document.getElementById('custom').addEventListener('change', function () { settings.color = this.value.toUpperCase(); save(); });
  document.getElementById('kind').addEventListener('change', function () { settings.kind = this.value; save(); });
  document.getElementById('mode').addEventListener('change', function () { settings.mode = this.value; save(); });
</script>
</body>
</html>
";
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && args[0] == "--setup") { Setup.Make(args[1]); return; }
                if (args.Length >= 3 && args[0] == "--pack") { Setup.Pack(args[1], args[2]); return; }
                new Plugin().Run(args);
            }
            catch (Exception ex)
            {
                Plugin.Log("fatal: " + ex);
            }
        }
    }
}
