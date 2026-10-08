using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 统一的视觉规范：配色、字体、圆角与阴影绘制工具。
    /// 所有面板都从这里取色取字，避免各处硬编码导致风格漂移。
    /// 配色沿用 QGIS Agent 的青蓝主色调（teal / cyan），与本项目复用的图标一致。
    /// </summary>
    internal static class UiTheme
    {
        // ---- 主色（与 icon.png 的青色一致）----
        public static readonly Color Accent = Color.FromArgb(13, 148, 136);       // teal-600
        public static readonly Color AccentDark = Color.FromArgb(15, 118, 110);   // teal-700
        public static readonly Color AccentDeep = Color.FromArgb(13, 100, 95);    // teal-800（深色强调块，非头部底色）
        public static readonly Color AccentLight = Color.FromArgb(204, 251, 241); // teal-100
        public static readonly Color AccentSoft = Color.FromArgb(240, 253, 250);  // teal-50
        public static readonly Color Cyan = Color.FromArgb(8, 145, 178);          // cyan-600

        // ---- 语义色 ----
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);
        public static readonly Color Danger = Color.FromArgb(220, 38, 38);
        public static readonly Color Info = Color.FromArgb(37, 99, 235);

        // ---- 语义色的「深色版」：给浅底上的小字号文字用 ----
        // 600 档的语义色（如 green-600）配 12px 字压在浅底上，笔画取不满对比度、
        // 会显得发虚；700 档在同样字号下笔画更实。深底白字的场景请继续用上面的 600 档。
        public static readonly Color SuccessDeep = Color.FromArgb(21, 128, 61);   // green-700
        public static readonly Color WarningDeep = Color.FromArgb(180, 83, 9);    // amber-700
        public static readonly Color DangerDeep = Color.FromArgb(185, 28, 28);    // red-700

        // ---- 中性色 ----
        public static readonly Color Bg = Color.FromArgb(244, 246, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color BorderStrong = Color.FromArgb(203, 213, 225);
        public static readonly Color Text = Color.FromArgb(30, 41, 59);
        public static readonly Color TextSub = Color.FromArgb(100, 116, 139);
        public static readonly Color TextFaint = Color.FromArgb(148, 163, 184);
        public static readonly Color Hover = Color.FromArgb(241, 245, 249);

        /// <summary>
        /// 把「96dpi 下的设计长度」换算成当前 DPI 的设备像素。
        ///
        /// 为什么必须走这里（v0.4.3 的核心修正）：
        /// 字体由 <see cref="UI"/> 按 DPI 放大（225% 时 Body=12px → 27px，行高 35px），
        /// 但布局常量以前全是 96dpi 的字面量（按钮高 26、标签高 18/24、行高 34…）。
        /// 两者不匹配时，**字形比容器还高 → 上下被硬裁掉**，笔画缺头少尾，看上去就是「糊」。
        /// 实测（216 DPI）：FlatButton Height=26 而 CaptionLabel 字体行高 35 → 各裁 4.5px。
        /// 而 TextBox 之所以一直锐利，只是因为它的高度会自适应字体行高、永远不裁。
        ///
        /// 注意：这里**不依赖 WinForms 的 AutoScaleMode**。本项目所有控件都是运行时 new 出来的，
        /// AutoScaleDimensions 恒为 {0,0}，PerformAutoScale 是空操作（已在 _probe2 里实测确认），
        /// 所以缩放必须由我们自己算，否则半像素落点又会带来新的模糊。
        /// </summary>
        public static int S(int px)
        {
            return (int)Math.Round(px * _scale);
        }

        /// <summary>圆角半径（按 DPI 缩放）。</summary>
        public static int Radius { get { return S(6); } }

        /// <summary>
        /// 给「一行文字」算容器高度：字体行高 + 上下留白，永不裁字。
        /// 所有承载文字的控件高度都应由此得出，而不是写死像素。
        /// </summary>
        public static int TextRowHeight(Font f, int extraPad = 4)
        {
            if (f == null) return S(26);
            return Math.Max(S(20), f.Height + Math.Max(2, S(extraPad)));
        }

        // ---- 字体 ----
        private static string _family;

        private static string Family
        {
            get
            {
                if (_family != null) return _family;
                string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun" };
                using (var ifc = new InstalledFontCollection())
                {
                    var have = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in ifc.Families) have.Add(f.Name);
                    foreach (var c in candidates)
                    {
                        if (have.Contains(c)) { _family = c; break; }
                    }
                }
                if (_family == null) _family = SystemFonts.MessageBoxFont.FontFamily.Name;
                return _family;
            }
        }

        // ---- 字体尺寸（单位：像素，不用磅）----
        // 用 GraphicsUnit.Pixel 而不是 Point：磅制在 96dpi 下会换算出 16.67px 这类非整数高度，
        // GDI 只能按取整后的栅格尺寸出字，笔画会被拉虚。像素制可直接落在真实像素网格上。
        private static readonly float _scale = ComputeScale();

        private static float ComputeScale()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { return 1f; }
        }

        /// <summary>界面字体。参数为「96dpi 下的像素高度」，会按当前 DPI 缩放。</summary>
        public static Font UI(float px, FontStyle style = FontStyle.Regular)
        {
            float h = Math.Max(6f, px * _scale);
            // 尽量落在整像素上，避免半像素高度带来的栅格化模糊
            h = (float)Math.Round(h);
            return new Font(Family, h, style, GraphicsUnit.Pixel);
        }

        public static Font Mono(float px, FontStyle style = FontStyle.Regular)
        {
            string fam = "Consolas";
            try
            {
                using (var f = new Font(fam, px * _scale, style, GraphicsUnit.Pixel))
                {
                    if (!string.Equals(f.FontFamily.Name, fam, StringComparison.OrdinalIgnoreCase))
                        fam = "Courier New";
                }
            }
            catch { fam = "Courier New"; }
            return new Font(fam, Math.Max(6f, (float)Math.Round(px * _scale)), style, GraphicsUnit.Pixel);
        }

        // 12px ≈ 9pt、11px ≈ 8.25pt，与原设计尺寸基本一致但更锐利
        public static Font Title { get { return UI(16f, FontStyle.Bold); } }
        public static Font Header { get { return UI(13.5f, FontStyle.Bold); } }
        public static Font Body { get { return UI(12f); } }
        public static Font BodyBold { get { return UI(12f, FontStyle.Bold); } }
        public static Font Small { get { return UI(11f); } }
        public static Font SmallBold { get { return UI(11f, FontStyle.Bold); } }

        // ---- 绘制工具 ----
        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
        {
            using (var path = Rounded(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void DrawRounded(Graphics g, Rectangle r, int radius, Color color, float width = 1f)
        {
            var rr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var path = Rounded(rr, radius))
            using (var p = new Pen(color, width))
                g.DrawPath(p, path);
        }

        /// <summary>把控件裁剪成圆角（用于按钮/卡片）。</summary>
        public static void ApplyRoundedRegion(Control c, int radius)
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using (var path = Rounded(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                var old = c.Region;
                c.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
    }
}
