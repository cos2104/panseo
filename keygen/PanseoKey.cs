// 판서 인증키 만들기 - 개발자 PC에서만 씀
// 비밀키(private.key)는 이 폴더에만 두고 저장소에는 올리지 않음(.gitignore). 잃어버리면 기존 키를 확인하는 새 판을 만들 수 없으니 따로 백업할 것
// 빌드: build.bat
using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace PanseoKey
{
    static class Program
    {
        static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string PrivatePath = Path.Combine(Dir, "private.key");
        static readonly string LogPath = Path.Combine(Dir, "issued.txt");
        const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--new") { NewKeyPair(); return; }
            if (args.Length > 2 && args[0] == "--make") { Console.WriteLine(Make(args[1], args[2])); return; }
            Application.EnableVisualStyles();
            Application.Run(new KeyForm());
        }

        // 새 열쇠 한 쌍. 이미 있으면 덮어쓰지 않음(덮어쓰면 그동안 낸 키가 모두 못 쓰게 됨)
        static void NewKeyPair()
        {
            if (File.Exists(PrivatePath)) { Console.WriteLine("private.key 가 이미 있습니다."); return; }
            var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport };
            using (var k = CngKey.Create(CngAlgorithm.ECDsaP256, null, p))
            {
                File.WriteAllText(PrivatePath, Convert.ToBase64String(k.Export(CngKeyBlobFormat.EccPrivateBlob)));
                Console.WriteLine("PUBLIC=" + Convert.ToBase64String(k.Export(CngKeyBlobFormat.EccPublicBlob)));
            }
        }

        // PC 번호를 XXXX-XXXX-XXXX-XXXX 꼴로 (소문자·빈칸·대시 상관없이)
        public static string NormalizeId(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.ToUpperInvariant())
                if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
            if (sb.Length != 16) return null;
            string t = sb.ToString();
            return t.Substring(0, 4) + "-" + t.Substring(4, 4) + "-" + t.Substring(8, 4) + "-" + t.Substring(12, 4);
        }

        public static string Make(string pcId, string name)
        {
            string id = NormalizeId(pcId);
            if (id == null) throw new ArgumentException("PC 번호가 16자가 아닙니다.");
            name = (name ?? "").Trim().Replace(".", "·");
            byte[] blob = Convert.FromBase64String(File.ReadAllText(PrivatePath).Trim());
            using (var ec = new ECDsaCng(CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob)))
            {
                ec.HashAlgorithm = CngAlgorithm.Sha256;
                byte[] sig = ec.SignData(Encoding.UTF8.GetBytes("panseo1|" + id + "|" + name));
                string key = "P1." + B64(Encoding.UTF8.GetBytes(name)) + "." + B64(sig);
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\t" + name + "\t" + id + "\t" + key + "\r\n", Encoding.UTF8);
                return key;
            }
        }

        static string B64(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        public static bool HasPrivate { get { return File.Exists(PrivatePath); } }
    }

    class KeyForm : Form
    {
        public KeyForm()
        {
            Text = "판서 인증키 만들기";
            Font = new Font("Malgun Gothic", 10f);
            ClientSize = new Size(480, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var lblId = new Label { Text = "PC 번호", Location = new Point(14, 18), AutoSize = true };
            var txtId = new TextBox { Location = new Point(90, 14), Width = 374, Font = new Font("Consolas", 12f) };
            var lblName = new Label { Text = "이름", Location = new Point(14, 56), AutoSize = true };
            var txtName = new TextBox { Location = new Point(90, 52), Width = 374 };
            var make = new Button { Text = "만들기", Location = new Point(90, 88), Width = 100, Height = 30 };
            var copy = new Button { Text = "복사", Location = new Point(196, 88), Width = 80, Height = 30, Enabled = false };
            var txtKey = new TextBox { Location = new Point(14, 128), Width = 450, Height = 80, Multiline = true, ReadOnly = true, Font = new Font("Consolas", 10f) };
            var status = new Label { Location = new Point(14, 216), Width = 450, AutoSize = false, ForeColor = Color.Firebrick };
            Controls.AddRange(new Control[] { lblId, txtId, lblName, txtName, make, copy, txtKey, status });
            AcceptButton = make;

            if (!Program.HasPrivate) { status.Text = "private.key 가 없습니다."; make.Enabled = false; }

            make.Click += (s, e) =>
            {
                try
                {
                    txtKey.Text = Program.Make(txtId.Text, txtName.Text);
                    txtId.Text = Program.NormalizeId(txtId.Text);
                    copy.Enabled = true;
                    status.Text = "";
                }
                catch (Exception ex) { txtKey.Text = ""; copy.Enabled = false; status.Text = ex.Message; }
            };
            copy.Click += (s, e) => { Clipboard.SetText(txtKey.Text); status.ForeColor = Color.SeaGreen; status.Text = "복사했습니다."; };
            txtId.TextChanged += (s, e) => { status.ForeColor = Color.Firebrick; };
        }
    }
}
