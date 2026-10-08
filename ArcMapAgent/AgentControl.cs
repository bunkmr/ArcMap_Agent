using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 插件主面板（WinForms UserControl），由 AgentDockableWindow.OnCreateChild() 托管。
    ///
    /// 结构：头部（图标 + 标题 + 状态灯） → 页签（对话/历史/模型/MCP/地图/工具/帮助） → 底部状态栏。
    /// 所有 LLM / ArcObjects 工作都通过 AgentController 在后台线程进行，UI 只负责展示；
    /// 跨线程调用统一走这里封装好的 Chat* / Write 方法。
    ///
    /// 文字全部交给原生 Label / RichTextBox / TextBox 渲染：自绘文字在双缓冲位图上会丢掉
    /// ClearType 伽马标定，笔画发虚并带彩色虚边（见 UiControls.FlatButton 注释）。
    /// </summary>
    public class AgentControl : UserControl
    {
        private readonly AppConfig _cfg;
        private readonly AgentController _controller;

        private TabHost _tabs;
        private ChatPanel _chat;
        private HistoryPanel _history;
        private ModelPanel _model;
        private McpPanel _mcp;
        private MapPanel _map;
        private ToolsPanel _tools;
        private HelpPanel _help;

        private HeaderBar _header;
        private Label _lblFooter;

        public AgentControl()
        {
            _cfg = AppConfig.Load();
            _controller = new AgentController(this);

            InitializeComponent();

            _controller.ConfigReloaded += OnConfigReloaded;
        }

        public AgentController Controller { get { return _controller; } }
        public AppConfig Config { get { return _cfg; } }
        public ChatPanel Chat { get { return _chat; } }

        /// <summary>配置在「模型」页被修改后，刷新依赖配置的页面。</summary>
        private void OnConfigReloaded(object sender, EventArgs e)
        {
            if (_chat != null) _chat.ReloadFromConfig();
            if (_mcp != null) _mcp.ReloadFromConfig();
        }

        private void InitializeComponent()
        {
            SuspendLayout();
            BackColor = UiTheme.Bg;
            Size = new Size(UiTheme.S(440), UiTheme.S(600));
            MinimumSize = new Size(UiTheme.S(360), UiTheme.S(440));
            Font = UiTheme.Body;
            // 禁止按父容器字体/DPI 自动缩放：UserControl 默认 AutoScaleMode=Inherit，
            // 被 ArcMap 停靠容器接管时会按宿主字体做一次比例换算，子控件落到半像素上 →
            // 原生 Label 也会被拉虚。字体尺寸已经由 UiTheme 按 DPI 算好，这里必须关掉。
            AutoScaleMode = AutoScaleMode.None;

            _header = new HeaderBar();
            _header.Dock = DockStyle.Top;

            _lblFooter = new Label
            {
                Dock = DockStyle.Bottom,
                Height = UiTheme.TextRowHeight(UiTheme.Small, 4),
                Text = "就绪",
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSub,
                BackColor = Color.FromArgb(238, 242, 246),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(UiTheme.S(10), 0, UiTheme.S(6), 0)
            };

            _tabs = new TabHost { Dock = DockStyle.Fill };

            // 页签顺序对齐 QGIS Agent：对话 / 历史 / 模型 / MCP /（ArcMap 特色）地图 / 工具 / 帮助
            _chat = new ChatPanel(this);
            _history = new HistoryPanel(this);
            _model = new ModelPanel(this);
            _mcp = new McpPanel(this);
            _map = new MapPanel(this);
            _tools = new ToolsPanel(this);
            _help = new HelpPanel(this);

            _tabs.AddTab("对话", "与大模型对话，用自然语言操控 ArcMap", _chat);
            _tabs.AddTab("历史", "保存与载入本地对话记录", _history);
            _tabs.AddTab("模型", "配置大模型端点、密钥与生成参数", _model);
            _tabs.AddTab("MCP", "把 ArcObjects 工具暴露给外部 Agent", _mcp);
            _tabs.AddTab("地图", "查看当前数据框/图层并执行常用操作（ArcMap 特色）", _map);
            _tabs.AddTab("工具", "ArcObjects 工具目录与 slash 命令速查", _tools);
            _tabs.AddTab("帮助", "插件功能与使用说明", _help);

            _tabs.TabChanged += (s, i) =>
            {
                if (i == 4 && _map != null) _map.RefreshMap();
                if (i == 2 && _model != null) _model.ReloadFromConfig();
                if (i == 0 && _chat != null) _chat.ShowWelcomeIfNeeded();
            };

            Controls.Add(_tabs);
            Controls.Add(_lblFooter);
            Controls.Add(_header);

            ResumeLayout(false);
        }

        // ---- 线程调度小工具 ----

        private void Ui(Action a)
        {
            if (a == null) return;
            try
            {
                if (InvokeRequired) Invoke(a); else a();
            }
            catch { }
        }

        /// <summary>流式增量用 BeginInvoke：不等 UI 线程，避免逐字 Invoke 拖慢读取循环。</summary>
        private void UiAsync(Action a)
        {
            if (a == null) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch { }
        }

        // ---- 对话输出（线程安全）----

        public void ChatBeginUser(string text) { Ui(() => _chat.ChatBeginUser(text)); }
        public void ChatBeginAssistant() { UiAsync(() => _chat.ChatBeginAssistant()); }
        public void ChatAppendAssistant(string delta) { UiAsync(() => _chat.ChatAppendAssistant(delta)); }
        public void ChatEndAssistant() { Ui(() => _chat.ChatEndAssistant()); }
        public void ChatToolCall(string name, string argsJson) { Ui(() => _chat.ChatToolCall(name, argsJson)); }
        public void ChatToolResult(string text) { Ui(() => _chat.ChatToolResult(text)); }
        public void ChatError(string text) { Ui(() => _chat.ChatError(text)); }

        /// <summary>纯文本输出（slash 命令结果、MCP 通知、帮助等），走 Markdown 渲染。</summary>
        public void Write(string text)
        {
            if (text == null) return;
            Ui(() => _chat.ChatSystem(text));
            AgentLog.Append(text);
        }

        /// <summary>写底部状态栏。</summary>
        public void SetStatus(string text)
        {
            if (text == null) return;
            if (InvokeRequired) { Invoke((Action)(() => SetStatus(text))); return; }
            _lblFooter.Text = text;
            _header.SetStatus(text, StatusColorFor(text));
        }

        /// <summary>
        /// 头部状态位是「浅底小字号」，语义色要取 700 档才够实（见 UiTheme 的 *Deep 注释）。
        /// 顺带按关键字上色：失败/异常标红，停止/取消标黄。
        /// </summary>
        private static Color StatusColorFor(string text)
        {
            if (string.IsNullOrEmpty(text)) return UiTheme.SuccessDeep;
            if (text.IndexOf("失败", StringComparison.Ordinal) >= 0
                || text.IndexOf("错误", StringComparison.Ordinal) >= 0
                || text.IndexOf("异常", StringComparison.Ordinal) >= 0)
                return UiTheme.DangerDeep;
            if (text.IndexOf("停止", StringComparison.Ordinal) >= 0
                || text.IndexOf("取消", StringComparison.Ordinal) >= 0)
                return UiTheme.WarningDeep;
            return UiTheme.SuccessDeep;
        }

        /// <summary>切换页签（供工具页/帮助页跳转使用）。</summary>
        public void GotoTab(int index)
        {
            if (_tabs != null) _tabs.Select(index);
        }

        /// <summary>MCP 桥接服务调用工具的统一入口：在 UI 线程执行 ArcObjects 操作。</summary>
        public string InvokeTool(string name, Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();
            string result = "";
            Action act = () => { result = _controller.ExecuteToolByName(name, args); };
            if (InvokeRequired) Invoke(act); else act();
            try { Write("[MCP] " + name + " → " + (result ?? "").Trim() + "\n"); } catch { }
            return result ?? "";
        }

        /// <summary>面板 Logo（内嵌的 icon.png，尺寸与 QGIS Agent 图标一致）。</summary>
        internal static Image LoadLogo()
        {
            try
            {
                var asm = typeof(AgentControl).Assembly;
                using (var s = asm.GetManifestResourceStream("ArcMapAgent.icon.png"))
                {
                    if (s == null) return null;
                    using (var tmp = Image.FromStream(s))
                        return new Bitmap(tmp);
                }
            }
            catch { return null; }
        }

        // ---- 头部 ----
        /// <summary>
        /// 顶部信息条：Logo + 标题 + 副标题 + 右侧状态文字与指示点。
        ///
        /// 关于「文字发虚」，v0.4.1 最终定稿的实测结论（对着截图逐像素比对得出）：
        ///  0) **深底白字的小字号必然发糊，浅底深字才锐利。**
        ///     ClearType 的次像素抗锯齿是为「深字浅底」设计的，为文字笔画分配 R/G/B 子像素；
        ///     白字压深底时，同一机制会在笔画边缘留下红蓝彩边，笔画又取不满对比度，
        ///     于是同一台机器上「对话区（黑字白底）锐利、头部（白字青底）发糊」。
        ///     实测：对话区文字行 12 行的笔画几乎全是满对比度像素，
        ///     头部副标题 14 行里大量像素只到一半对比度 —— 这就是发糊的量化表现。
        ///     所以头部改为 **teal-50 浅底 + slate-900/teal-700 深字**，与对话区用同一套渲染条件。
        ///  1) 底色不用渐变。GDI+ 的 LinearGradientBrush 会在位图上做抖动（dither），
        ///     文字压在有噪点的底上，笔画边缘会显得毛糙（v0.4.0 的亮青渐变就是这个问题）。
        ///  2) 标签一律给**纯色背景**而不是 Color.Transparent：透明 Label 需要先把父控件背景
        ///     画进自己再出字，多一次合成；而且父控件若在自绘，透明 Label 往往拿不到正确底色，
        ///     会在字周围留一块色差方块（v0.4.0 的「就绪」就带着这么一块）。
        ///  3) 行距与头部高度**按 Font.Height 算**，不写死 px：写死 60px 时，一旦字体比预期高，
        ///     标题的降部（g/p）就会直接压到副标题字头上，看起来像两行字叠在一起。
        /// </summary>
        private class HeaderBar : Panel
        {
            private readonly Label _lblTitle;
            private readonly Label _lblSub;
            private readonly Label _lblStatus;
            private readonly PictureBox _logo;
            private Color _statusColor = UiTheme.SuccessDeep;
            private readonly int _dot = UiTheme.S(9);
            private bool _ready;

            private static readonly Color HeadBg = UiTheme.AccentSoft;                 // teal-50
            private static readonly Color TitleFg = Color.FromArgb(15, 23, 42);        // slate-900（与对话区正文同色）
            private static readonly Color SubFg = UiTheme.AccentDark;                  // teal-700
            private static readonly Color Rule = UiTheme.Accent;                       // 底部 3px 主色线

            private readonly int _lineGap;

            public HeaderBar()
            {
                Dock = DockStyle.Top;
                BackColor = HeadBg;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

                // 标题与副标题用原生 Label：ClearType 由系统标定，比自绘文字锐利得多
                _lblTitle = new Label
                {
                    Text = "ArcMap Agent",
                    Font = UiTheme.UI(16f, FontStyle.Bold),
                    ForeColor = TitleFg,
                    BackColor = HeadBg,
                    AutoSize = false,
                    AutoEllipsis = true,
                    UseMnemonic = false,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                _lblSub = new Label
                {
                    Text = SubTextLong,
                    Font = UiTheme.UI(12f),
                    ForeColor = SubFg,
                    BackColor = HeadBg,
                    AutoSize = false,
                    AutoEllipsis = true,
                    UseMnemonic = false,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                // 关键：AutoSize 必须为 false。AutoSize 的 Label 会把自己的高宽改回
                // PreferredSize，于是 SetBounds(x, 0, w, Height) 里的 Height 被丢弃，
                // 文字跑到顶部、和垂直居中的指示点错位（v0.4.0 的实际表现）。
                _lblStatus = new Label
                {
                    Text = "就绪",
                    Font = UiTheme.UI(12f),
                    ForeColor = UiTheme.SuccessDeep,
                    BackColor = HeadBg,
                    AutoSize = false,
                    AutoEllipsis = true,
                    UseMnemonic = false,
                    TextAlign = ContentAlignment.MiddleRight
                };

                _logo = new PictureBox
                {
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = HeadBg
                };
                try
                {
                    var img = LoadLogo();
                    if (img != null) _logo.Image = img;
                    else _logo.Visible = false;
                }
                catch { _logo.Visible = false; }

                // 行距取「副标题字高」的 1/4（最小 2px），随字体缩放，不写死像素
                _lineGap = Math.Max(2, _lblSub.Font.Height / 4);

                // 头部高度按字体实际度量算出来：两块文字 + 上下留白。
                // 写死 60px 时，一旦字体比预期大，两行就会贴在一起（标题的降部压到副标题字头）。
                Height = _lblTitle.Font.Height + _lineGap + _lblSub.Font.Height + 2 * PadV;

                Controls.Add(_logo);
                Controls.Add(_lblTitle);
                Controls.Add(_lblSub);
                Controls.Add(_lblStatus);

                // 子控件齐了才开始布局（Height 赋值会提前触发一次 OnResize）
                _ready = true;
                LayoutStatus();
            }

            /// <summary>头部上下留白（按 DPI 缩放）。</summary>
            private static readonly int PadV = UiTheme.S(7);

            private const string SubTextLong = "基于 ArcObjects 的自然语言 GIS 助手  v" + McpBridge.Version;
            private const string SubTextShort = "自然语言 GIS 助手  v" + McpBridge.Version;

            public void SetStatus(string s, Color c)
            {
                _statusColor = c;
                if (!_ready || _lblStatus == null) return;
                _statusFull = s ?? "";
                string shortText = ShortStatus(_statusFull);
                if (_lblStatus.Text != shortText)
                {
                    _lblStatus.Text = shortText;
                    _lblStatus.ForeColor = c;
                }
                LayoutStatus();
                Invalidate();
            }

            private string _statusFull = "就绪";

            /// <summary>
            /// 头部状态位只放一个短状态词，完整信息在底部状态栏。
            /// 头部宽度有限（尤其停靠面板），把「已切换模型: local → qwen3.6-35b」这种长句
            /// 塞进去会把标题/副标题挤掉——之前副标题被截断就是这个原因。
            /// </summary>
            private static string ShortStatus(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "就绪";
                s = s.Trim();
                int cut = s.Length;
                char[] seps = { ' ', ':', '：', '·', '，', ',', '—', '→' };
                foreach (char c in seps)
                {
                    int i = s.IndexOf(c);
                    if (i > 0 && i < cut) cut = i;
                }
                string head = s.Substring(0, cut);
                if (head.Length > 6) head = head.Substring(0, 6) + "…";
                return head;
            }

            private void LayoutStatus()
            {
                if (!_ready || _lblTitle == null || _lblSub == null || _lblStatus == null || _logo == null) return;

                int pad = UiTheme.S(12);
                int titleH = _lblTitle.Font.Height;
                int subH = _lblSub.Font.Height;
                int blockH = titleH + _lineGap + subH;
                int top = Math.Max(1, (Height - blockH) / 2);

                // 图标与文字块等高、垂直居中；文字起点由图标实际宽度推出，不写死
                int logoSize = Math.Max(UiTheme.S(16), Math.Min(blockH, Height - 2 * PadV));
                _logo.Size = new Size(logoSize, logoSize);
                _logo.Location = new Point(pad, (Height - logoSize) / 2);
                int left = _logo.Visible ? pad + logoSize + UiTheme.S(10) : pad;

                int right = pad + _dot + UiTheme.S(12);              // 指示点 + 右侧留白
                int room = Math.Max(0, Width - left - right);

                // 状态文字：显式宽高 + MiddleRight → 与指示点同一水平线（AutoSize=false 是关键）
                int need = TextRenderer.MeasureText(_lblStatus.Text ?? "", _lblStatus.Font).Width + UiTheme.S(4);
                int statusW = Math.Min(need, Math.Max(0, room * 45 / 100));
                _lblStatus.Visible = statusW >= UiTheme.S(22) && Width >= UiTheme.S(190);
                if (_lblStatus.Visible) _lblStatus.SetBounds(Width - right - statusW, 0, statusW, Height);

                int titleW = Math.Max(UiTheme.S(24), Width - left - (_lblStatus.Visible ? right + statusW + UiTheme.S(12) : pad));
                _lblTitle.SetBounds(left, top, titleW, titleH);
                _lblSub.SetBounds(left, top + titleH + _lineGap, titleW, subH);

                // 副标题按实际可用宽度选长/短文案，仍然放不下就靠 AutoEllipsis 收尾
                int longW = TextRenderer.MeasureText(SubTextLong, _lblSub.Font).Width;
                string want = titleW >= longW ? SubTextLong : SubTextShort;
                if (_lblSub.Text != want) _lblSub.Text = want;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                LayoutStatus();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var b = new SolidBrush(HeadBg))
                    g.FillRectangle(b, new Rectangle(0, 0, Width, Height));
                // 底部一条主色细线：既做品牌色，也给浅色头部一个明确下边界
                int rule = UiTheme.S(3);
                using (var b = new SolidBrush(Rule))
                    g.FillRectangle(b, new Rectangle(0, Height - rule, Width, rule));

                g.SmoothingMode = SmoothingMode.AntiAlias;
                int dy = Height / 2 - _dot / 2;
                int dx = Width - UiTheme.S(14) - _dot;
                using (var b = new SolidBrush(_statusColor)) g.FillEllipse(b, new Rectangle(dx, dy, _dot, _dot));
            }
        }
    }
}
