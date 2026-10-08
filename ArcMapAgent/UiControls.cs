using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArcMapAgent
{
    public enum BtnStyle { Primary, Ghost, Danger, Success, Subtle }

    /// <summary>
    /// 鼠标穿透 Label（WM_NCHITTEST → HTTRANSPARENT）。
    /// v0.4.3 起 FlatButton 已不再使用它（改为直接自绘标题，少一个 HWND）。
    /// 保留此类仅供其它需要「文字不吃鼠标」的场景使用。
    /// </summary>
    public class PassthroughLabel : Label
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 扁平圆角按钮：Primary / Ghost / Danger / Success / Subtle 五种风格，自带 hover / press / disabled 三态。
    ///
    /// **标题必须直接画在「窗口 DC」上，不能画进 OnPaint 的双缓冲位图**（v0.4.3 的核心修正）。
    ///
    /// 背景：本机给 ArcMap.exe 设了 `GDIDPISCALING DPIUNAWARE` 兼容标志 —— ArcMap 是 DPI 不感知进程，
    /// 桌面在 225%，于是整个窗口被放大 2.25x。该模式下 Windows 会**拦截直接画到窗口 DC 的 GDI 文字、
    /// 按真实 DPI 重新渲染**（所以字是"真"分辨率、清晰的）；而**先画进离屏位图再贴图的内容不加拦截，
    /// 只能被位图放大 → 糊**。
    ///
    /// 实测（`_blurtest2`，白字压 teal 底，同一字号，8x 放大 + 逐像素统计）：
    ///   出字方式                                   笔画实体度   彩边占比
    ///   · Label（GDI / GDI+）                        0.12~0.13    0.32
    ///   · 透明子 Label（v0.4.2 的做法）               0.12         0.32
    ///   · **双缓冲 OnPaint 里 TextRenderer**          0.12         0.33   ← 本类改之前就是这样
    ///   · **base.WndProc 之后画在窗口 DC 上**         **0.23**     **0.26**   ← 改成了这个
    ///   · 不双缓冲 OnPaint 里 TextRenderer            0.23         0.26
    /// 结论：**只要文字先落进双缓冲位图，就必然发糊**；GDI 还是 GDI+、透明还是纯色都不是关键。
    /// 所以这里保留双缓冲（避免自绘圆角在 hover/缩放时闪烁），但把标题挪到
    /// <see cref="WndProc"/> 里、等 base 把底色画完后再补画到窗口 DC 上。
    ///
    /// 顺带：对话框截图（`Control.DrawToBitmap`）走的是 WM_PRINT / WM_PRINTCLIENT，不经过 WM_PAINT，
    /// 因此那里仍由 <see cref="OnPaint"/> 出字，保证抓图工具看到的按钮标题不缺字。
    /// </summary>
    public class FlatButton : Button
    {
        public BtnStyle Style { get; set; }
        private bool _hover, _down;

        private const int WM_PAINT = 0x000F;
        private const int WM_PRINT = 0x0317;
        private const int WM_PRINTCLIENT = 0x0318;

        /// <summary>正在响应 WM_PRINT / WM_PRINTCLIENT（对话框抓图）——此时才在 OnPaint 里出字。</summary>
        private bool _printing;

        private const TextFormatFlags CaptionFlags =
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis;

        public FlatButton(string text, BtnStyle style = BtnStyle.Ghost)
        {
            Style = style;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            AutoSize = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = UiTheme.Body;
            Height = UiTheme.S(26);
            // 统一默认外边距：**顶距必须为 0**。
            // FlowLayoutPanel 是认 Control.Margin 的，而 Control.Margin 的默认值是 (3,3,3,3)。
            // 一排按钮里若有的用默认、有的写了 Margin=(6,0,0,0)，顶距就是 3 vs 0，
            // 整排按钮会错开 3px（GDIDPISCALING 下放大成 7px，肉眼一眼可见）。
            // 这里把默认顶距钉成 0，右侧留 6px 作为按钮间距；调用方要用更大间距就自己给
            // Margin = new Padding(left, 0, ...)，只要 top 保持 0 就不会再错位。
            Margin = new Padding(0, 0, UiTheme.S(6), 0);
            Cursor = Cursors.Hand;
            TabStop = false;

            base.Text = text ?? "";
        }

        /// <summary>
        /// 不变量：按钮标题的字体行高绝不能超过按钮高度，否则字形上下被裁掉、笔画缺头少尾，
        /// 看上去就是「字发糊」。这是本项目最隐蔽的一个 bug 类别，所以放在最底层拦一道，
        /// 任何调用方给再小的高度也不会把字裁坏。
        /// </summary>
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (height > 0)
            {
                int min = UiTheme.TextRowHeight(CaptionFont, 4);
                if (height < min) height = min;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        /// <summary>UIA / 自动化从 Text 读名字，直接用基类的即可。</summary>
        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value ?? ""; Invalidate(); }
        }

        /// <summary>标题字体（页签/胶囊按钮用不同字号）。就是 <see cref="Control.Font"/> 的别名。</summary>
        public Font CaptionFont
        {
            get { return Font; }
            set { if (value != null) Font = value; }
        }

        /// <summary>重绘按钮（hover / 风格切换后调用）。</summary>
        public void RefreshVisual()
        {
            Invalidate();
        }

        public Color CaptionColor(bool enabled)
        {
            switch (Style)
            {
                case BtnStyle.Primary:
                case BtnStyle.Success:
                case BtnStyle.Danger:
                    return enabled ? Color.White : Color.FromArgb(226, 232, 240);
                default:
                    return enabled ? UiTheme.AccentDark : UiTheme.TextFaint;
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            RefreshVisual();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; RefreshVisual(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; RefreshVisual(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { _down = true; RefreshVisual(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { _down = false; RefreshVisual(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parentBg = Parent != null ? Parent.BackColor : UiTheme.Card;
            g.Clear(parentBg);

            Color bg, border;
            switch (Style)
            {
                case BtnStyle.Primary:
                    bg = !Enabled ? Color.FromArgb(203, 213, 225) : ((_hover || _down) ? UiTheme.AccentDark : UiTheme.Accent);
                    border = Color.Empty; break;
                case BtnStyle.Success:
                    bg = !Enabled ? Color.FromArgb(203, 213, 225) : (_hover ? Color.FromArgb(21, 128, 61) : UiTheme.Success);
                    border = Color.Empty; break;
                case BtnStyle.Danger:
                    bg = !Enabled ? Color.FromArgb(203, 213, 225) : (_hover ? Color.FromArgb(185, 28, 28) : UiTheme.Danger);
                    border = Color.Empty; break;
                case BtnStyle.Subtle:
                    bg = _down ? UiTheme.Border : (_hover ? UiTheme.Hover : Color.Empty);
                    border = Color.Empty; break;
                default: // Ghost
                    bg = _down ? UiTheme.Border : (_hover ? UiTheme.Hover : Color.Empty);
                    border = UiTheme.BorderStrong; break;
            }

            var r = new Rectangle(0, 0, Width, Height);
            if (bg != Color.Empty) UiTheme.FillRounded(g, r, UiTheme.Radius, bg);
            if (border != Color.Empty) UiTheme.DrawRounded(g, r, UiTheme.Radius, border);

            // 只有抓图（WM_PRINT/WM_PRINTCLIENT）时才在双缓冲里出字，正常屏幕绘制见 WndProc。
            if (_printing && !string.IsNullOrEmpty(Text))
                DrawCaption(g, ClientRectangle);
        }

        /// <summary>
        /// 把标题直接画到**窗口 DC** 上，绕开双缓冲位图。
        /// 这是全项目唯一一处「绕开缓冲」的文字：GDIDPISCALING 只对直接落到窗口 DC 的 GDI 文字
        /// 做高分辨率重绘，进了位图的内容只会被拉伸 —— 实测笔画实体度 0.12 → 0.23、彩边 0.33 → 0.26。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            bool print = m.Msg == WM_PRINT || m.Msg == WM_PRINTCLIENT;
            if (print) _printing = true;
            base.WndProc(ref m);
            if (print) _printing = false;

            if (m.Msg == WM_PAINT && !string.IsNullOrEmpty(Text))
            {
                try
                {
                    using (var g = Graphics.FromHwnd(Handle))
                        DrawCaption(g, ClientRectangle);
                }
                catch { }   // 句柄正在销毁时忽略
            }
        }

        /// <summary>出标题。颜色随风格/禁用态现算，字体与对齐由字段统一。</summary>
        private void DrawCaption(Graphics g, Rectangle bounds)
        {
            TextRenderer.DrawText(g, Text, CaptionFont, bounds, CaptionColor(Enabled), CaptionFlags);
        }
    }

    /// <summary>
    /// 卡片容器：白底 + 1px 边框 + 圆角 + 顶部强调线 + 标题/说明。
    /// 标题与说明用原生 Label 渲染（锐利）；正文区是 <see cref="Content"/>，往里面加控件即可。
    /// </summary>
    public class CardPanel : Panel
    {
        public string Title { get; set; }
        public string Hint { get; set; }
        public Panel Content { get; private set; }

        private readonly Label _lblTitle;
        private readonly Label _lblHint;

        private readonly int _padX = UiTheme.S(12);
        private readonly int _accentH = UiTheme.S(2);
        private readonly int _titleH;
        private readonly int _hintH;
        private readonly int _headerHeight;

        public CardPanel(string title, string hint)
        {
            Title = title ?? "";
            Hint = hint ?? "";
            BackColor = UiTheme.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // 标题/说明的行高按字体算，不写死 18/26 —— 216dpi 下 Header 行高 39px，
            // 写死 18 会把「当前对话」四个字上下各裁掉一半，看着就是「糊」。
            _titleH = UiTheme.TextRowHeight(UiTheme.Header, 2);
            _hintH = string.IsNullOrEmpty(Hint) ? 0 : UiTheme.TextRowHeight(UiTheme.Small, 8);
            _headerHeight = _accentH + UiTheme.S(4) + _titleH + _hintH + UiTheme.S(4);

            Content = new Panel
            {
                BackColor = UiTheme.Card,
                Padding = new Padding(_padX, UiTheme.S(2), _padX, UiTheme.S(8))
            };

            _lblTitle = new Label
            {
                Text = Title,
                Font = UiTheme.Header,
                ForeColor = UiTheme.Text,
                AutoSize = false,
                BackColor = UiTheme.Card,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblHint = new Label
            {
                Text = Hint,
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSub,
                AutoSize = false,
                BackColor = UiTheme.Card,
                TextAlign = ContentAlignment.TopLeft,
                Visible = !string.IsNullOrEmpty(Hint)
            };

            Controls.Add(Content);
            Controls.Add(_lblTitle);
            Controls.Add(_lblHint);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 构造期间（BackColor/Size 赋值）可能先触发 OnResize，此时子控件尚未建好
            if (Content == null || _lblTitle == null || _lblHint == null) return;
            UiTheme.ApplyRoundedRegion(this, UiTheme.Radius);
            int w = Math.Max(20, Width - _padX * 2);
            int y = _accentH + UiTheme.S(4);
            _lblTitle.SetBounds(_padX, y, w, _titleH);
            y += _titleH;
            if (!string.IsNullOrEmpty(Hint))
                _lblHint.SetBounds(_padX, y, w, _hintH);
            Content.SetBounds(1, _headerHeight, Math.Max(1, Width - 2), Math.Max(1, Height - _headerHeight - 1));
            _lblTitle.BringToFront();
            _lblHint.BringToFront();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(0, 0, Width, Height);
            using (var b = new SolidBrush(UiTheme.Card)) g.FillRectangle(b, r);
            using (var b = new SolidBrush(UiTheme.AccentLight))
                g.FillRectangle(b, new Rectangle(0, 0, Width, _accentH));
            UiTheme.DrawRounded(g, r, UiTheme.Radius, UiTheme.Border);
        }
    }

    /// <summary>
    /// 自绘页签宿主：顶部一行可换行的胶囊按钮 + 内容区，只显示当前页。
    /// 不用 TabControl 的原因：TabControl 在窄停靠面板里无法换行、系统主题下页签样式不可控，
    /// 且无法保证「非当前页的控件不出现在 UI Automation 树里」（自动化回归测试依赖这一点）。
    /// 页签按钮自身是 FlatButton，文字由原生 Label 渲染。
    /// </summary>
    public class TabHost : UserControl
    {
        private class TabItem { public FlatButton Btn; public Control Page; }

        private readonly FlowLayoutPanel _strip;
        private readonly Panel _host;
        private readonly List<TabItem> _items = new List<TabItem>();
        private readonly ToolTip _tip = new ToolTip();
        private int _index = -1;

        public event EventHandler<int> TabChanged;

        public TabHost()
        {
            BackColor = UiTheme.Bg;
            Padding = new Padding(0);

            _host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Bg, Padding = new Padding(UiTheme.S(5), 0, UiTheme.S(5), UiTheme.S(5)) };
            _strip = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = UiTheme.Bg,
                Padding = new Padding(UiTheme.S(5), UiTheme.S(6), UiTheme.S(5), UiTheme.S(2))
            };

            Controls.Add(_host);
            Controls.Add(_strip);
        }

        public void AddTab(string text, string tooltip, Control page)
        {
            var btn = new FlatButton(text, BtnStyle.Ghost)
            {
                Width = Math.Max(UiTheme.S(46), TextRenderer.MeasureText(text, UiTheme.Body).Width + UiTheme.S(22)),
                Height = UiTheme.S(26),
                // top 必须为 0，否则页签之间也会错位（Margin 默认值陷阱，见 FlatButton 注释）
                Margin = new Padding(UiTheme.S(3), 0, 0, UiTheme.S(3))
            };
            int idx = _items.Count;
            btn.Click += (s, e) => Select(idx);
            if (!string.IsNullOrEmpty(tooltip)) _tip.SetToolTip(btn, tooltip);

            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _host.Controls.Add(page);

            _strip.Controls.Add(btn);
            _items.Add(new TabItem { Btn = btn, Page = page });

            if (_index < 0) Select(0);
        }

        public int SelectedIndex { get { return _index; } }

        public void Select(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            _index = i;
            for (int k = 0; k < _items.Count; k++)
            {
                var it = _items[k];
                bool on = k == i;
                it.Btn.Style = on ? BtnStyle.Primary : BtnStyle.Ghost;
                // 标题颜色由 FlatButton 在 OnPaint 里按 Style 现算，这里只需重绘
                it.Btn.RefreshVisual();
                it.Page.Visible = on;
            }
            var h = TabChanged;
            if (h != null) h(this, i);
        }
    }

    /// <summary>
    /// 页面通用基类：浅灰底，内容纵向堆叠，窄面板下自动换行 + 纵向滚动。
    /// 往 <see cref="Stack"/> 里加控件（推荐用 AddCard/AddTitle/AddHint 辅助方法）。
    /// </summary>
    public class PagePanel : UserControl
    {
        public AgentControl Shell { get; private set; }
        protected readonly FlowLayoutPanel Stack;

        public PagePanel(AgentControl shell)
        {
            Shell = shell;
            BackColor = UiTheme.Bg;
            Dock = DockStyle.Fill;

            Stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = UiTheme.Bg,
                Padding = new Padding(UiTheme.S(6), UiTheme.S(6), UiTheme.S(6), UiTheme.S(10)),
                AutoSize = false
            };
            Stack.Resize += (s, e) => FitChildren();
            Controls.Add(Stack);
        }

        /// <summary>FlowLayoutPanel 不会把子控件横向拉满，这里手工统一宽度（并对自动换行标签设最大宽度）。</summary>
        protected void FitChildren()
        {
            // 构造期间 Dock/Size 赋值会先触发一次 OnResize，此时 Stack 还没有被创建
            if (Stack == null) return;
            int w = Stack.ClientSize.Width - Stack.Padding.Horizontal
                    - (Stack.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            if (w < 60) w = 60;
            foreach (Control c in Stack.Controls)
            {
                if (c is Label lbl && lbl.AutoSize)
                {
                    int mw = Math.Max(80, w - lbl.Margin.Horizontal);
                    if (lbl.MaximumSize.Width != mw) lbl.MaximumSize = new Size(mw, 0);
                    continue;
                }
                int target = Math.Max(60, w - c.Margin.Horizontal);
                if (Math.Abs(c.Width - target) > 1) c.Width = target;
            }
        }

        public Label AddTitle(string text)
        {
            var lb = new Label
            {
                Text = text,
                Font = UiTheme.Title,
                ForeColor = UiTheme.Text,
                AutoSize = true,
                BackColor = UiTheme.Bg,
                Margin = new Padding(UiTheme.S(2), UiTheme.S(8), UiTheme.S(2), UiTheme.S(2))
            };
            Stack.Controls.Add(lb);
            return lb;
        }

        public Label AddHint(string text)
        {
            var lb = new Label
            {
                Text = text,
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSub,
                AutoSize = true,
                BackColor = UiTheme.Bg,
                MaximumSize = new Size(UiTheme.S(560), 0),
                Margin = new Padding(UiTheme.S(2), 0, UiTheme.S(2), UiTheme.S(6))
            };
            Stack.Controls.Add(lb);
            return lb;
        }

        /// <summary>
        /// 追加一张固定高度的卡片（FlowLayoutPanel 不会自动撑高卡片，因此高度必须显式给出）。
        /// </summary>
        public CardPanel AddCard(string title, string hint, int height = 140)
        {
            var card = new CardPanel(title, hint)
            {
                Height = Math.Max(UiTheme.S(70), UiTheme.S(height)),
                Margin = new Padding(0, 0, 0, UiTheme.S(8))
            };
            Stack.Controls.Add(card);
            return card;
        }

        /// <summary>
        /// 字段行：左标签 + 右控件。
        /// 行高由最高的子控件决定，所有子控件**垂直居中**——以前写死 Top=3，
        /// 行内放高矮不同的控件（22 高的标签 + 26 高的按钮）时基线就对不齐。
        /// </summary>
        protected static Panel Row(params Control[] controls)
        {
            int h = UiTheme.S(32);
            foreach (var c in controls) h = Math.Max(h, c.Height + UiTheme.S(6));
            var p = new Panel { Dock = DockStyle.Top, Height = h, BackColor = UiTheme.Card };
            int x = 0;
            foreach (var c in controls)
            {
                c.Top = Math.Max(0, (h - c.Height) / 2);
                c.Left = x;
                p.Controls.Add(c);
                x += c.Width + UiTheme.S(6);
            }
            return p;
        }

        protected static Label FieldLabel(string text, int width = 62)
        {
            return new Label
            {
                Text = text,
                Width = UiTheme.S(width),
                Height = UiTheme.TextRowHeight(UiTheme.Body, 2),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = UiTheme.Body,
                ForeColor = UiTheme.TextSub,
                BackColor = UiTheme.Card
            };
        }
    }
}
