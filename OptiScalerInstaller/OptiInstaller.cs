using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OptiInstaller
{
    static class App
    {
        public const string Version = "2.1";
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AutoUpdate.TryUpdate();
            Application.Run(new MainForm());
        }
    }

    static class AutoUpdate
    {
        public static void TryUpdate()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string cfg = Path.Combine(dir, "update.cfg");
                if (!File.Exists(cfg)) return;
                string repo = null;
                foreach (string line in File.ReadAllLines(cfg))
                {
                    string t = line.Trim();
                    if (t.StartsWith("repo=", StringComparison.OrdinalIgnoreCase)) repo = t.Substring(5).Trim();
                }
                if (string.IsNullOrEmpty(repo) || repo.IndexOf("OWNER", StringComparison.OrdinalIgnoreCase) >= 0 || !repo.Contains("/"))
                    return;

                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;

                string json = HttpGet("https://api.github.com/repos/" + repo + "/releases?per_page=30");
                if (json == null) return;

                string tag, url;
                if (!FindInstallerRelease(json, out tag, out url)) return;
                if (!IsNewer(tag, App.Version)) return;

                string cur = Path.Combine(dir, "Instalar_Mod.exe");
                string neu = Path.Combine(dir, "Instalar_Mod_new.exe");
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "OptiInstaller");
                    wc.DownloadFile(url, neu);
                }
                if (!File.Exists(neu) || new FileInfo(neu).Length < 20000)
                {
                    try { File.Delete(neu); } catch { }
                    return;
                }

                string bat = Path.Combine(dir, "_update.bat");
                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    "tasklist /fi \"imagename eq Instalar_Mod.exe\" | find /i \"Instalar_Mod.exe\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
                    "move /y \"" + neu + "\" \"" + cur + "\" >nul\r\n" +
                    "start \"\" \"" + cur + "\"\r\n" +
                    "del \"%~f0\"\r\n");
                ProcessStartInfo psi = new ProcessStartInfo(bat);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = true;
                Process.Start(psi);
                Environment.Exit(0);
            }
            catch { }
        }

        static string HttpGet(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "OptiInstaller";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // Procura, da release mais nova para a mais antiga, a primeira que tenha
        // o asset "Instalar_Mod.exe" (ignora releases de outros produtos no mesmo repo).
        static bool FindInstallerRelease(string json, out string tag, out string url)
        {
            tag = null; url = null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            object[] releases = js.DeserializeObject(json) as object[];
            if (releases == null) return false;
            foreach (object ro in releases)
            {
                Dictionary<string, object> rel = ro as Dictionary<string, object>;
                if (rel == null) continue;
                if (rel.ContainsKey("draft") && rel["draft"] is bool && (bool)rel["draft"]) continue;
                object[] assets = rel.ContainsKey("assets") ? rel["assets"] as object[] : null;
                if (assets == null) continue;
                foreach (object ao in assets)
                {
                    Dictionary<string, object> a = ao as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = a.ContainsKey("name") ? a["name"] as string : null;
                    if (name != null && name.Equals("Instalar_Mod.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        tag = rel.ContainsKey("tag_name") ? rel["tag_name"] as string : null;
                        url = a.ContainsKey("browser_download_url") ? a["browser_download_url"] as string : null;
                        return tag != null && url != null;
                    }
                }
            }
            return false;
        }

        static bool IsNewer(string remote, string local)
        {
            try { return new Version(Norm(remote)).CompareTo(new Version(Norm(local))) > 0; }
            catch { return false; }
        }

        static string Norm(string v)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in v) if (char.IsDigit(c) || c == '.') sb.Append(c);
            string r = sb.ToString().Trim('.');
            if (r.Length == 0) r = "0";
            if (!r.Contains(".")) r += ".0";
            return r;
        }
    }

    static class Theme
    {
        public static Color Bg = Color.FromArgb(13, 14, 24);
        public static Color Card = Color.FromArgb(23, 25, 43);
        public static Color CardHi = Color.FromArgb(33, 36, 60);
        public static Color Field = Color.FromArgb(30, 32, 54);
        public static Color Text = Color.FromArgb(238, 240, 252);
        public static Color Sub = Color.FromArgb(143, 149, 182);
        public static Color P1 = Color.FromArgb(139, 92, 246);
        public static Color P2 = Color.FromArgb(96, 110, 245);
        public static Color Blue = Color.FromArgb(79, 123, 255);
        public static Color Teal = Color.FromArgb(20, 184, 166);
        public static Color Pink = Color.FromArgb(236, 72, 153);
        public static Color Green = Color.FromArgb(45, 200, 120);
        public static Color Orange = Color.FromArgb(245, 165, 60);
        public static Color Red = Color.FromArgb(244, 73, 100);

        public static Font FTitle = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        public static Font FSub = new Font("Segoe UI", 8.25f, FontStyle.Regular);

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Lighten(Color c, int d)
        {
            return Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr h, int m, int w, int l);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    }

    class RoundPanel : Panel
    {
        public int Radius = 14;
        public Color Fill = Theme.Card;
        public Color Accent = Color.Transparent;
        public int AccentW = 0;
        public RoundPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, path);
                if (AccentW > 0 && Accent.A > 0)
                    using (Pen p = new Pen(Accent, AccentW)) g.DrawPath(p, path);
            }
        }
    }

    class GradButton : Control
    {
        public Color C1 = Theme.P1, C2 = Theme.P2;
        public int Radius = 11;
        bool hover = false, down = false;
        public GradButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color a = C1, b = C2;
            if (!Enabled) { a = Color.FromArgb(58, 61, 84); b = Color.FromArgb(48, 51, 72); }
            else if (down) { a = Theme.Lighten(C1, -10); b = Theme.Lighten(C2, -10); }
            else if (hover) { a = Theme.Lighten(C1, 22); b = Theme.Lighten(C2, 22); }
            using (GraphicsPath path = Theme.Round(r, Radius))
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, LinearGradientMode.Horizontal))
                g.FillPath(br, path);
            Color tc = Enabled ? ForeColor : Color.FromArgb(150, 152, 165);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    class IconButton : Control
    {
        public int Kind = 0; // 0 = close, 1 = minimize
        bool hover = false;
        public IconButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (hover)
            {
                Color hb = Kind == 0 ? Theme.Red : Color.FromArgb(60, 64, 92);
                using (GraphicsPath path = Theme.Round(r, 7))
                using (SolidBrush b = new SolidBrush(hb)) g.FillPath(b, path);
            }
            using (Pen p = new Pen(Color.FromArgb(220, 224, 240), 1.6f))
            {
                int cx = Width / 2, cy = Height / 2;
                if (Kind == 0)
                {
                    g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                }
                else
                {
                    g.DrawLine(p, cx - 5, cy + 4, cx + 5, cy + 4);
                }
            }
        }
    }

    class OptionCard : Control
    {
        public string Title = "";
        public string Sub = "";
        public bool Checked = false;
        public Color Accent = Theme.P1;
        public event EventHandler CheckedChanged;
        bool hover = false;
        public OptionCard()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            Height = 60;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            Checked = !Checked;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            base.OnClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(r, 12))
            {
                using (SolidBrush b = new SolidBrush(hover && Enabled ? Theme.CardHi : Theme.Card)) g.FillPath(b, path);
                Color bc = Checked ? Color.FromArgb(200, Accent) : Color.FromArgb(55, 58, 84);
                using (Pen p = new Pen(bc, Checked ? 1.6f : 1f)) g.DrawPath(p, path);
            }
            // accent icon square
            Rectangle ic = new Rectangle(13, (Height - 34) / 2, 34, 34);
            using (GraphicsPath p2 = Theme.Round(ic, 9))
            using (LinearGradientBrush br = new LinearGradientBrush(ic, Accent, Theme.Lighten(Accent, 45), 50f))
                g.FillPath(br, p2);
            // texts
            Color tcol = Enabled ? Theme.Text : Color.FromArgb(120, 124, 150);
            TextRenderer.DrawText(g, Title, Theme.FTitle, new Rectangle(58, 9, Width - 110, 20), tcol,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Sub, Theme.FSub, new Rectangle(58, 31, Width - 110, 18), Theme.Sub,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            // check indicator
            Rectangle ck = new Rectangle(Width - 44, (Height - 26) / 2, 26, 26);
            using (GraphicsPath p3 = Theme.Round(ck, 8))
            {
                if (Checked)
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(ck, Theme.P1, Theme.Blue, LinearGradientMode.ForwardDiagonal))
                        g.FillPath(br, p3);
                    using (Pen pen = new Pen(Color.White, 2.3f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new Point[] {
                            new Point(ck.X + 7, ck.Y + 13),
                            new Point(ck.X + 11, ck.Y + 18),
                            new Point(ck.X + 19, ck.Y + 8) });
                    }
                }
                else
                {
                    using (Pen pen = new Pen(Color.FromArgb(75, 79, 110), 1.6f)) g.DrawPath(pen, p3);
                }
            }
        }
    }

    public class MainForm : Form
    {
        TextBox txtPath, txtLog;
        GradButton btnBrowse, btnInstall;
        IconButton btnClose, btnMin;
        OptionCard cardDlss, cardFsr4, cardForce;
        Label lblTitle, lblSubtitle, lblTarget, lblStatus;
        RoundPanel pathCard, logCard;

        string srcDir, selectedRoot, targetDir, lastAnalyzed;
        List<string> allFiles = new List<string>();
        bool antiCheatFound = false, hasUpscaler = false, fsr4Available = false;
        string antiCheatFile = null;

        public MainForm()
        {
            srcDir = AppDomain.CurrentDomain.BaseDirectory;
            DoubleBuffered = true;
            Text = "Instalador de Mods";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 728);
            BackColor = Theme.Bg;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            int M = 22, W = ClientSize.Width - M * 2;

            // title bar buttons
            btnClose = new IconButton(); btnClose.Kind = 0; btnClose.SetBounds(ClientSize.Width - 38, 9, 28, 28);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
            btnMin = new IconButton(); btnMin.Kind = 1; btnMin.SetBounds(ClientSize.Width - 70, 9, 28, 28);
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(btnMin);

            // header texts
            lblTitle = MakeLabel("Instalador de Mods", new Font("Segoe UI", 14.5f, FontStyle.Bold), Theme.Text);
            lblTitle.SetBounds(78, 52, 320, 30);
            Controls.Add(lblTitle);
            lblSubtitle = MakeLabel("Frame Generation com um clique", new Font("Segoe UI", 9f), Theme.Sub);
            lblSubtitle.SetBounds(80, 84, 360, 20);
            Controls.Add(lblSubtitle);

            // path card
            pathCard = new RoundPanel(); pathCard.SetBounds(M, 118, W, 96); pathCard.Radius = 14;
            Controls.Add(pathCard);
            Label lblP = MakeLabel("1) Pasta raiz do jogo  (cole o caminho ou selecione)", Theme.FSub, Theme.Sub);
            lblP.SetBounds(18, 12, W - 36, 16); pathCard.Controls.Add(lblP);
            txtPath = new TextBox();
            txtPath.SetBounds(18, 40, W - 160, 30);
            txtPath.BorderStyle = BorderStyle.None;
            txtPath.BackColor = Theme.Field; txtPath.ForeColor = Theme.Text;
            txtPath.Font = new Font("Segoe UI", 10f);
            txtPath.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryAnalyze(); }
            };
            txtPath.Leave += delegate { TryAnalyze(); };
            pathCard.Controls.Add(txtPath);
            // a subtle field underline drawn via a thin panel
            Panel fieldLine = new Panel(); fieldLine.SetBounds(18, 72, W - 160, 2); fieldLine.BackColor = Theme.P2;
            pathCard.Controls.Add(fieldLine);
            btnBrowse = new GradButton(); btnBrowse.Text = "Procurar"; btnBrowse.C1 = Theme.P1; btnBrowse.C2 = Theme.P2;
            btnBrowse.SetBounds(W - 130, 40, 112, 34);
            btnBrowse.Click += Browse_Click;
            pathCard.Controls.Add(btnBrowse);

            // target + status lines
            lblTarget = MakeLabel("", Theme.FSub, Theme.Blue);
            lblTarget.SetBounds(M + 2, 220, W - 4, 16); Controls.Add(lblTarget);
            lblStatus = MakeLabel("Selecione ou cole a pasta do jogo para comecar.", new Font("Segoe UI", 8.75f, FontStyle.Bold), Theme.Sub);
            lblStatus.SetBounds(M + 2, 238, W - 4, 18); Controls.Add(lblStatus);

            // option cards
            int oy = 264;
            cardDlss = new OptionCard();
            cardDlss.Title = "Atualizar DLSS para 310.6";
            cardDlss.Sub = "Melhora a nitidez do DLSS (com backup automatico)";
            cardDlss.Accent = Theme.Green; cardDlss.Checked = true;
            cardDlss.SetBounds(M, oy, W, 60); Controls.Add(cardDlss);

            cardFsr4 = new OptionCard();
            cardFsr4.Title = "Usar FSR4 em vez de DLSS";
            cardFsr4.Sub = "Imagem melhor, porem mais pesado na sua placa";
            cardFsr4.Accent = Theme.Pink; cardFsr4.Checked = false;
            cardFsr4.SetBounds(M, oy + 70, W, 60); Controls.Add(cardFsr4);

            cardForce = new OptionCard();
            cardForce.Title = "Forcar Frame Gen sempre ligado";
            cardForce.Sub = "Define Enabled=true (liga o FG sem depender do menu)";
            cardForce.Accent = Theme.P1; cardForce.Checked = false;
            cardForce.SetBounds(M, oy + 140, W, 60); Controls.Add(cardForce);

            // log card
            logCard = new RoundPanel(); logCard.SetBounds(M, oy + 212, W, 150); logCard.Radius = 14; logCard.Fill = Color.FromArgb(17, 18, 32);
            Controls.Add(logCard);
            txtLog = new TextBox();
            txtLog.SetBounds(12, 12, W - 24, 126);
            txtLog.Multiline = true; txtLog.ReadOnly = true; txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.BackColor = Color.FromArgb(17, 18, 32); txtLog.ForeColor = Color.FromArgb(176, 182, 214);
            txtLog.Font = new Font("Consolas", 8.5f);
            logCard.Controls.Add(txtLog);

            // install button
            btnInstall = new GradButton();
            btnInstall.Text = "INSTALAR  →";
            btnInstall.C1 = Color.FromArgb(124, 92, 252); btnInstall.C2 = Color.FromArgb(79, 123, 255);
            btnInstall.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            btnInstall.Radius = 14;
            btnInstall.SetBounds(M, oy + 374, W, 52);
            btnInstall.Enabled = false;
            btnInstall.Click += Install_Click;
            Controls.Add(btnInstall);

            // drag from top bar / header
            MouseDown += DragForm;
            lblTitle.MouseDown += DragForm;
            lblSubtitle.MouseDown += DragForm;

            CheckSource();
        }

        static Label MakeLabel(string t, Font f, Color c)
        {
            Label l = new Label();
            l.Text = t; l.Font = f; l.ForeColor = c;
            l.BackColor = Theme.Bg; l.AutoSize = false;
            return l;
        }

        void DragForm(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // top accent gradient line
            Rectangle top = new Rectangle(0, 0, ClientSize.Width, 3);
            using (LinearGradientBrush br = new LinearGradientBrush(top, Theme.P1, Theme.Pink, LinearGradientMode.Horizontal))
                g.FillRectangle(br, top);
            // header icon (gradient rounded square + lightning bolt)
            Rectangle ic = new Rectangle(22, 50, 44, 44);
            using (GraphicsPath path = Theme.Round(ic, 12))
            using (LinearGradientBrush br = new LinearGradientBrush(ic, Color.FromArgb(124, 58, 237), Color.FromArgb(168, 85, 247), 55f))
                g.FillPath(br, path);
            DrawSkull(g, ic);
            // version badge
            Rectangle bd = new Rectangle(300, 60, 50, 19);
            using (GraphicsPath path = Theme.Round(bd, 9))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 43, 70))) g.FillPath(b, path);
            TextRenderer.DrawText(g, "v" + App.Version, new Font("Segoe UI", 8f, FontStyle.Bold), bd, Theme.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static void DrawSkull(Graphics g, Rectangle r)
        {
            float S = r.Width, X = r.X, Y = r.Y;
            Color socket = Color.FromArgb(74, 28, 120);
            using (SolidBrush wb = new SolidBrush(Color.White))
            {
                g.FillEllipse(wb, X + S * 0.24f, Y + S * 0.17f, S * 0.52f, S * 0.47f);
                Rectangle jaw = new Rectangle((int)(X + S * 0.34f), (int)(Y + S * 0.50f), (int)(S * 0.32f), (int)(S * 0.26f));
                using (GraphicsPath jp = Theme.Round(jaw, (int)(S * 0.07f))) g.FillPath(wb, jp);
            }
            using (SolidBrush sb = new SolidBrush(socket))
            {
                float er = S * 0.145f;
                g.FillEllipse(sb, X + S * 0.295f, Y + S * 0.32f, er, er);
                g.FillEllipse(sb, X + S * 0.56f, Y + S * 0.32f, er, er);
                PointF[] nose = {
                    new PointF(X + S * 0.5f, Y + S * 0.49f),
                    new PointF(X + S * 0.452f, Y + S * 0.59f),
                    new PointF(X + S * 0.548f, Y + S * 0.59f) };
                g.FillPolygon(sb, nose);
            }
            using (Pen pen = new Pen(socket, Math.Max(1f, S * 0.03f)))
            {
                float ty = Y + S * 0.52f, by = Y + S * 0.745f, cx = X + S * 0.5f;
                g.DrawLine(pen, cx, ty, cx, by);
                g.DrawLine(pen, X + S * 0.425f, ty, X + S * 0.425f, by);
                g.DrawLine(pen, X + S * 0.575f, ty, X + S * 0.575f, by);
            }
        }

        void Log(string m)
        {
            txtLog.AppendText(m + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        void CheckSource()
        {
            string[] need = new string[] {
                "dxgi.dll", "OptiScaler.ini", "dlssg_to_fsr3_amd_is_better.dll",
                "fakenvapi.dll", "fakenvapi.ini", "amd_fidelityfx_dx12.dll",
                "D3D12_Optiscaler\\D3D12Core.dll"
            };
            List<string> missing = need.Where(f => !File.Exists(Path.Combine(srcDir, f))).ToList();
            fsr4Available = File.Exists(Path.Combine(srcDir, "FSR4_INT8_4.0.2c\\amd_fidelityfx_upscaler_dx12.dll"));
            if (!fsr4Available)
            {
                cardFsr4.Enabled = false;
                cardFsr4.Sub = "Pasta 'FSR4_INT8_4.0.2c' nao encontrada na pasta mods";
            }
            if (missing.Count > 0)
            {
                Log("ERRO: este .exe precisa ficar na pasta 'mods', junto dos arquivos do mod.");
                Log("Faltando: " + string.Join(", ", missing.ToArray()));
                btnBrowse.Enabled = false;
            }
            else
            {
                Log("Arquivos-fonte do mod: OK");
                Log("Origem: " + srcDir);
                Log("");
                Log("Cole o caminho da pasta do jogo ou clique em Procurar.");
            }
        }

        void Browse_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Selecione a pasta raiz do jogo";
            if (Directory.Exists(txtPath.Text)) dlg.SelectedPath = txtPath.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                txtPath.Text = dlg.SelectedPath;
                TryAnalyze();
            }
        }

        void TryAnalyze()
        {
            string p = txtPath.Text.Trim().Trim('"');
            if (p.Length == 0) return;
            if (p == lastAnalyzed) return;
            if (!Directory.Exists(p))
            {
                lblTarget.Text = "";
                lblStatus.ForeColor = Theme.Orange;
                lblStatus.Text = "Pasta nao encontrada - verifique o caminho.";
                btnInstall.Enabled = false;
                return;
            }
            selectedRoot = p;
            lastAnalyzed = p;
            Analyze();
        }

        void Analyze()
        {
            txtLog.Clear();
            btnInstall.Enabled = false;
            lblStatus.Text = ""; lblTarget.Text = "";
            Log("Analisando: " + selectedRoot);
            Cursor = Cursors.WaitCursor;
            try { allFiles = SafeWalk(selectedRoot); }
            finally { Cursor = Cursors.Default; }
            Log("Arquivos no jogo: " + allFiles.Count);

            antiCheatFound = false; antiCheatFile = null;
            string[] acm = new string[] { "easyanticheat", "battleye", "beservice", "eac_launcher", "eaanticheat", "vanguard" };
            foreach (string f in allFiles)
            {
                string nm = Path.GetFileName(f).ToLowerInvariant();
                bool hit = false;
                foreach (string m in acm) { if (nm.Contains(m)) { hit = true; break; } }
                if (hit) { antiCheatFound = true; antiCheatFile = f; break; }
            }

            hasUpscaler = allFiles.Any(delegate(string f)
            {
                if (IsExcluded(f)) return false;
                string nm = Path.GetFileName(f).ToLowerInvariant();
                return nm.StartsWith("nvngx_") || nm.StartsWith("sl.") || nm.Contains("dlss")
                    || nm.Contains("fsr") || nm.Contains("fidelityfx") || nm.Contains("libxess")
                    || nm.Contains("xess") || nm.Contains("streamline");
            });

            targetDir = DetectTarget();
            bool ue = targetDir.ToLowerInvariant().Contains("\\binaries\\win64");
            lblTarget.Text = "Destino: " + targetDir;

            Log("");
            Log("Motor: " + (ue ? "Unreal Engine (instala em Binaries\\Win64)" : "Flat / RE Engine (instala na raiz)"));
            Log("Upscaler nativo (DLSS/FSR/XeSS): " + (hasUpscaler ? "SIM" : "NAO encontrado"));

            if (antiCheatFound)
            {
                lblStatus.ForeColor = Theme.Red;
                lblStatus.Text = "BLOQUEADO: anti-cheat detectado (" + Path.GetFileName(antiCheatFile) + ").";
                Log("");
                Log("!!! ANTI-CHEAT DETECTADO: " + Path.GetFileName(antiCheatFile));
                Log("Injetar mods aqui pode causar BANIMENTO. Instalacao bloqueada.");
                return;
            }
            if (!hasUpscaler)
            {
                lblStatus.ForeColor = Theme.Orange;
                lblStatus.Text = "Aviso: sem DLSS/FSR - o mod provavelmente NAO vai funcionar.";
            }
            else
            {
                lblStatus.ForeColor = Theme.Green;
                lblStatus.Text = "Pronto para instalar.";
            }
            btnInstall.Enabled = true;
        }

        string DetectTarget()
        {
            List<string> ship = allFiles.Where(delegate(string f)
            {
                string l = f.ToLowerInvariant();
                return l.EndsWith("-shipping.exe") && !IsExcluded(f) && !Path.GetFileName(l).Contains("trial");
            }).ToList();
            if (ship.Count == 0)
                ship = allFiles.Where(f => f.ToLowerInvariant().EndsWith("-shipping.exe") && !IsExcluded(f)).ToList();
            if (ship.Count > 0)
                return Path.GetDirectoryName(ship.OrderByDescending(f => Len(f)).First());

            List<string> w64 = allFiles.Where(delegate(string f)
            {
                string l = f.ToLowerInvariant();
                return l.EndsWith(".exe") && l.Contains("\\binaries\\win64\\") && !IsExcluded(f)
                    && !Path.GetFileName(l).Contains("trial");
            }).ToList();
            if (w64.Count > 0)
                return Path.GetDirectoryName(w64.OrderByDescending(f => Len(f)).First());

            return selectedRoot;
        }

        static long Len(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

        static bool IsExcluded(string path)
        {
            string p = path.ToLowerInvariant();
            return p.Contains("\\_crack\\") || p.Contains("\\_original files\\")
                || p.Contains("\\_original\\") || p.Contains("\\__installer\\")
                || p.Contains("\\_dlss_backup") || p.Contains("\\_optiscaler_backup")
                || p.Contains("\\_fsr4_backup") || p.Contains("\\backup\\")
                || p.Contains("\\engine\\binaries\\thirdparty\\")
                || p.Contains("\\redist\\");
        }

        static List<string> SafeWalk(string root)
        {
            List<string> res = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try { foreach (string f in Directory.GetFiles(dir)) res.Add(f); }
                catch { }
                try { foreach (string d in Directory.GetDirectories(dir)) stack.Push(d); }
                catch { }
            }
            return res;
        }

        void Install_Click(object sender, EventArgs e)
        {
            if (antiCheatFound) return;
            if (!hasUpscaler)
            {
                DialogResult r = MessageBox.Show(
                    "Nenhum DLSS/FSR/XeSS foi detectado neste jogo.\n" +
                    "O OptiScaler provavelmente NAO vai funcionar aqui.\n\nInstalar mesmo assim?",
                    "Aviso", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            btnInstall.Enabled = false; btnBrowse.Enabled = false;
            try { DoInstall(); }
            catch (Exception ex)
            {
                Log("ERRO: " + ex.Message);
                if (ex is UnauthorizedAccessException)
                    Log("Dica: feche o jogo e rode este instalador como Administrador.");
                MessageBox.Show("Erro: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { btnBrowse.Enabled = true; btnInstall.Enabled = true; }
        }

        void DoInstall()
        {
            Log("");
            Log("=== INSTALANDO em: " + targetDir + " ===");
            Directory.CreateDirectory(targetDir);

            CopyFile("dxgi.dll", null);
            CopyFile("dlssg_to_fsr3_amd_is_better.dll", null);
            CopyFile("fakenvapi.dll", null);
            CopyFile("fakenvapi.ini", null);
            CopyFile("amd_fidelityfx_dx12.dll", null);
            CopyFile("D3D12_Optiscaler\\D3D12Core.dll", "D3D12_Optiscaler\\D3D12Core.dll");

            string patcherSrc = File.Exists(Path.Combine(srcDir, "OptiPatcher.asi"))
                ? "OptiPatcher.asi" : "optpatcher\\OptiPatcher.asi";
            CopyFile(patcherSrc, "plugins\\OptiPatcher.asi");

            string iniDst = Path.Combine(targetDir, "OptiScaler.ini");
            bool freshIni = !File.Exists(iniDst);
            if (!freshIni)
            {
                Log("OptiScaler.ini ja existe -> mantido.");
            }
            else
            {
                File.Copy(Path.Combine(srcDir, "OptiScaler.ini"), iniDst, false);
                Log("copiado: OptiScaler.ini");
                SetIniValue(iniDst, "LoadAsiPlugins", "true");
                SetIniValue(iniDst, "Dx12Upscaler", "dlss");
                SetIniValue(iniDst, "FGInput", "dlssg");
                SetIniValue(iniDst, "FGOutput", "nukems");
                SetIniValue(iniDst, "ShortcutKey", "0xBB");
                if (cardForce.Checked) SetIniValue(iniDst, "Enabled", "true");
                Log("config: DLSS, FGInput=dlssg, FGOutput=nukems, menu=tecla '='"
                    + (cardForce.Checked ? ", Enabled=true" : ""));
            }

            try
            {
                string licSrc = Path.Combine(srcDir, "Licenses");
                if (Directory.Exists(licSrc))
                {
                    string licDst = Path.Combine(targetDir, "Licenses");
                    Directory.CreateDirectory(licDst);
                    foreach (string lf in Directory.GetFiles(licSrc))
                        File.Copy(lf, Path.Combine(licDst, Path.GetFileName(lf)), true);
                }
            }
            catch { }

            if (cardDlss.Checked) UpgradeDlss();
            if (cardFsr4.Checked) InstallFsr4(iniDst);

            Log("");
            Log("=== CONCLUIDO ===");
            Log("No jogo: escolha o upscaler e ligue DLSS Frame Generation.");
            Log("Tecla = abre o overlay do OptiScaler.");
            Log("Desinstalar: rode 'Remove OptiScaler.bat' ou apague os arquivos.");
            lblStatus.ForeColor = Theme.Green;
            lblStatus.Text = "Instalacao concluida!";
            MessageBox.Show("Instalacao concluida!", "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void CopyFile(string srcRel, string dstRel)
        {
            if (dstRel == null) dstRel = srcRel;
            string s = Path.Combine(srcDir, srcRel);
            string d = Path.Combine(targetDir, dstRel);
            if (!File.Exists(s)) { Log("fonte faltando (pulado): " + srcRel); return; }
            string dd = Path.GetDirectoryName(d);
            if (!Directory.Exists(dd)) Directory.CreateDirectory(dd);
            File.Copy(s, d, true);
            Log("copiado: " + dstRel);
        }

        void UpgradeDlss()
        {
            Log("");
            Log("--- DLSS 310.6 ---");
            string dlssSrcDir = Path.Combine(srcDir, "DLSS 310.6");
            if (!Directory.Exists(dlssSrcDir)) { Log("pasta 'DLSS 310.6' nao encontrada, pulando."); return; }
            string[] names = new string[] { "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll" };
            string bakRoot = Path.Combine(selectedRoot, "_dlss_backup");
            int count = 0;
            foreach (string nm in names)
            {
                string srcF = Path.Combine(dlssSrcDir, nm);
                if (!File.Exists(srcF)) continue;
                List<string> targets = allFiles.Where(f =>
                    Path.GetFileName(f).Equals(nm, StringComparison.OrdinalIgnoreCase) && !IsExcluded(f)).ToList();
                foreach (string tf in targets)
                {
                    try
                    {
                        string rel = tf.Substring(selectedRoot.Length).TrimStart('\\');
                        string bak = Path.Combine(bakRoot, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(bak));
                        if (!File.Exists(bak)) File.Copy(tf, bak, false);
                        File.Copy(srcF, tf, true);
                        Log("DLSS atualizado: " + rel);
                        count++;
                    }
                    catch (Exception ex) { Log("falha em " + nm + ": " + ex.Message); }
                }
            }
            if (count == 0) Log("nenhum nvngx_*.dll encontrado para atualizar.");
            else Log("backups em: " + bakRoot);
        }

        void InstallFsr4(string iniDst)
        {
            Log("");
            Log("--- FSR4 (INT8) ---");
            string fsrSrc = Path.Combine(srcDir, "FSR4_INT8_4.0.2c\\amd_fidelityfx_upscaler_dx12.dll");
            if (!File.Exists(fsrSrc)) { Log("FSR4 nao encontrado, pulando."); return; }
            string dst = Path.Combine(targetDir, "amd_fidelityfx_upscaler_dx12.dll");
            if (File.Exists(dst))
            {
                string bak = Path.Combine(selectedRoot, "_fsr4_backup");
                Directory.CreateDirectory(bak);
                string bf = Path.Combine(bak, "amd_fidelityfx_upscaler_dx12.dll");
                if (!File.Exists(bf)) File.Copy(dst, bf, false);
                Log("backup do upscaler nativo -> _fsr4_backup");
            }
            File.Copy(fsrSrc, dst, true);
            Log("FSR4 instalado: amd_fidelityfx_upscaler_dx12.dll");
            if (File.Exists(iniDst))
            {
                SetIniValue(iniDst, "Dx12Upscaler", "fsr31");
                Log("Dx12Upscaler=fsr31 -> no jogo escolha FSR como upscaler.");
            }
        }

        static void SetIniValue(string iniPath, string key, string value)
        {
            string[] lines = File.ReadAllLines(iniPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.TrimStart().StartsWith(";")) continue;
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(ln.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + " = " + value;
                    File.WriteAllLines(iniPath, lines);
                    return;
                }
            }
        }
    }
}
