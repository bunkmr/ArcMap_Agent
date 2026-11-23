using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「对话」页：一问一答的对话记录 + 输入区 + 模型/温度/跳过确认。
    ///
    /// 输入框是可多行的 <see cref="ChatInputBox"/>（v0.4.4 起）：Enter 发送、Shift+Enter 换行、
    /// 内容变多时自动长高（到 6 行封顶后出现滚动条）。用中文输入法打字时，回车用于上屏候选词，
    /// 不会被误当成发送——见 ChatInputBox 里对 WM_IME_* 的处理。
    ///
    /// 自动化兼容：输入框保持 Name=AgentInput（回归测试用「真实鼠标 + 真实键盘」驱动，
    /// 不依赖 UIA 的 ValuePattern，所以多行不影响测试），发送按钮 Name=AgentSend（可 Invoke），
    /// 输出容器 Name=AgentOutput，且所有输出同时写入 panel.log，供回归测试取证。
    /// </summary>
    public class ChatPanel : UserControl
    {
        private readonly AgentControl _shell;

        private Label _lblTitle;
        private FlatButton _btnClear;
        private Panel _chipsBar;
        private Panel _outputHost;
        private ChatTranscript _transcript;
        private Panel _inputArea;
        private ChatInputBox _input;
        private Label _placeholder;
        private FlatButton _send;
        private FlatButton _stop;
        private Panel _bottomBar;
        private ComboBox _model;
        private TrackBar _temp;
        private Label _tempVal;
        private CheckBox _skip;
        private bool _suppressModelEvent;
        private bool _welcomeShown;
        private bool _ready;

        public ChatPanel(AgentControl shell)
        {
            _shell = shell;
            Build();
            ReloadFromConfig();
            ShowWelcomeIfNeeded();
        }

        public ChatTranscript TranscriptView { get { return _transcript; } }

        private void Build()
        {
            BackColor = UiTheme.Bg;
            Dock = DockStyle.Fill;

            _lblTitle = new Label
            {
                Text = "新建对话",
                Font = UiTheme.Header,
                ForeColor = UiTheme.Text,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _btnClear = new FlatButton("清空", BtnStyle.Subtle) { Width = UiTheme.S(54) };

            _chipsBar = new Panel { BackColor = UiTheme.Bg };

            _transcript = new ChatTranscript { Dock = DockStyle.Fill };

            // 输出容器：Name=AgentOutput 供自动化定位（Panel 会出现在 UIA 树里）
            _outputHost = new Panel
            {
                Name = "AgentOutput",
                AccessibleName = "AgentOutput",
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(UiTheme.S(2))
            };
            _outputHost.Controls.Add(_transcript);

            _inputArea = new Panel { BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            _input = new ChatInputBox
            {
                Name = "AgentInput",
                AccessibleName = "AgentInput",
                Font = UiTheme.Body,
                ForeColor = UiTheme.Text,
                BackColor = Color.White
            };

            // 多行 EDIT 控件不支持 EM_SETCUEBANNER（只有单行才显示），所以占位提示改由一层同色
            // Label 承担。这里**必须**用不透明白底而不是 Color.Transparent：透明背景会让 WinForms
            // 退回 GDI+ 出字，在 ArcMap 的 GDIDPISCALING 环境下必然发糊（v0.4.3 查过的同一个坑）。
            _placeholder = new Label
            {
                Name = "AgentPlaceholder",
                Text = "输入指令…  例：/layers   或   按分公司字段给图层上色",
                Font = UiTheme.Body,
                ForeColor = UiTheme.TextSub,
                BackColor = Color.White,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.IBeam
            };
            _placeholder.Click += (s, e) =>
            {
                _input.Focus();
                _input.SelectionStart = _input.TextLength;
            };
            _send = new FlatButton("发送", BtnStyle.Primary) { Name = "AgentSend", AccessibleName = "AgentSend", Width = UiTheme.S(62), Height = UiTheme.S(30) };
            _stop = new FlatButton("停止", BtnStyle.Danger) { Width = UiTheme.S(62), Height = UiTheme.S(30), Visible = false };
            _send.CaptionFont = UiTheme.BodyBold;
            _stop.CaptionFont = UiTheme.Body;
            _send.Font = UiTheme.BodyBold;

            _bottomBar = new Panel { BackColor = UiTheme.Bg };
            var lblModel = new Label { Text = "模型", Font = UiTheme.Small, ForeColor = UiTheme.TextSub, BackColor = UiTheme.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            _model = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Body, FlatStyle = FlatStyle.Flat };
            var lblTemp = new Label { Text = "温度", Font = UiTheme.Small, ForeColor = UiTheme.TextSub, BackColor = UiTheme.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            _temp = new TrackBar { Minimum = 0, Maximum = 20, TickStyle = TickStyle.None, Value = 0, SmallChange = 1 };
            _tempVal = new Label { Text = "0.0", Font = UiTheme.Small, ForeColor = UiTheme.TextSub, BackColor = UiTheme.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            _skip = new CheckBox { Text = "跳过确认", Font = UiTheme.Small, ForeColor = UiTheme.TextSub, BackColor = UiTheme.Bg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };

            // 快捷指令（点击把命令填进输入框，用户再补上图层/字段名）
            AddChip("/layers", "列出当前图层", "/layers");
            AddChip("/label", "显示标注（填入后补上字段名）", "/label 1 ");
            AddChip("/breaks", "按数值字段分级着色", "/breaks 1 ");
            AddChip("/gptools", "查询 ArcToolbox 工具（缓冲、裁剪…）", "/gptools");
            AddChip("帮助", "查看全部 slash 命令与工具", "/help");

            _btnClear.Click += (s, e) =>
            {
                _transcript.ClearAll();
                _welcomeShown = false;
                ShowWelcomeIfNeeded();
                _shell.SetStatus("已清空对话记录");
            };
            _send.Click += (s, e) => Send();
            _stop.Click += (s, e) => { _shell.Controller.Stop(); SetBusy(false); _shell.SetStatus("已请求停止"); };
            _input.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;

                // 中文输入法组字期间，回车是「上屏候选词」，绝不能当成发送。
                // 否则用户打拼音按回车选字时，会把半截话发出去（这是多行改造最容易踩的坑）。
                if (_input.IsImeComposing) return;

                // Shift+Enter 走原生换行（ChatInputBox.AcceptsReturn = true），这里不拦。
                if (e.Shift) return;

                // 其余情况（Enter / Ctrl+Enter）都是发送；SuppressKeyPress 同时消掉换行与提示音。
                e.SuppressKeyPress = true;
                Send();
            };
            _input.TextChanged += (s, e) => { AutoGrowInput(); UpdatePlaceholder(); };
            _input.GotFocus += (s, e) => UpdatePlaceholder();
            _input.LostFocus += (s, e) => UpdatePlaceholder();
            _model.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressModelEvent) return;
                if (_model.SelectedItem == null) return;
                _shell.Config.ActiveName = _model.SelectedItem.ToString();
                _shell.Config.Save();
                _shell.Controller.ReloadConfig();
                var a = _shell.Config.Active;
                _shell.SetStatus("已切换模型: " + a.Name + "  →  " + a.Model);
            };
            _temp.ValueChanged += (s, e) =>
            {
                double v = _temp.Value / 10.0;
                _tempVal.Text = v.ToString("0.0");
                _shell.Config.Temperature = v;
            };
            _temp.MouseUp += (s, e) => { _shell.Config.Save(); _shell.Controller.ReloadConfig(); };
            _skip.CheckedChanged += (s, e) =>
            {
                _shell.Config.SkipConfirm = _skip.Checked;
                _shell.Config.Save();
                _shell.Controller.ReloadConfig();
            };

            _inputArea.Controls.Add(_input);
            _inputArea.Controls.Add(_send);
            _inputArea.Controls.Add(_stop);
            _inputArea.Controls.Add(_placeholder);
            _placeholder.BringToFront();   // 占位提示要压在输入框之上才看得见

            _bottomBar.Controls.Add(lblModel);
            _bottomBar.Controls.Add(_model);
            _bottomBar.Controls.Add(lblTemp);
            _bottomBar.Controls.Add(_temp);
            _bottomBar.Controls.Add(_tempVal);
            _bottomBar.Controls.Add(_skip);

            Controls.Add(_lblTitle);
            Controls.Add(_btnClear);
            Controls.Add(_chipsBar);
            Controls.Add(_outputHost);
            Controls.Add(_inputArea);
            Controls.Add(_bottomBar);

            Resize += (s, e) => { AutoGrowInput(); LayoutChildren(); };
            _ready = true;
            AutoGrowInput();        // 先按内容定高，再统一排版
            UpdatePlaceholder();
            LayoutChildren();
        }

        private void AddChip(string text, string tip, string command)
        {
            var b = new FlatButton(text, BtnStyle.Subtle)
            {
                Width = TextRenderer.MeasureText(text, UiTheme.Small).Width + UiTheme.S(18),
                Height = UiTheme.S(22),
                CaptionFont = UiTheme.Small,
                Font = UiTheme.Small
            };
            b.Click += (s, e) => { _input.Text = command; _input.Focus(); };
            var t = new ToolTip();
            t.SetToolTip(b, tip);
            _chipsBar.Controls.Add(b);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            // 构造期间 Dock 赋值会先触发 OnResize，此时子控件还没创建
            if (!_ready) return;
            int pad = UiTheme.S(8);
            int gap = UiTheme.S(6);
            int W = ClientSize.Width, H = ClientSize.Height;
            int innerW = Math.Max(UiTheme.S(60), W - pad * 2);

            // 所有行高一律由字体行高算出来，不写死 30/36/24/26。
            // 216dpi 下 Small 行高 32px、Body 行高 35px：写死 22/24 会把字形上下裁掉一半，
            // 视觉上就是「字发糊」——这正是本页按钮与标签发虚的原因（v0.4.3 修正）。
            int titleH = UiTheme.TextRowHeight(UiTheme.Header, 4);
            int chipsH = Math.Max(UiTheme.S(24), UiTheme.TextRowHeight(UiTheme.Small, 8));
            int inputH = Math.Max(UiTheme.S(36), _input.Height + UiTheme.S(12));
            int rowH = UiTheme.TextRowHeight(UiTheme.Small, 4);
            int bottomH = Math.Max(UiTheme.S(30), rowH);

            _lblTitle.SetBounds(pad, pad, Math.Max(UiTheme.S(30), innerW - UiTheme.S(60)), titleH);
            _btnClear.SetBounds(pad + innerW - UiTheme.S(56),
                                pad + Math.Max(0, (titleH - _btnClear.Height) / 2),
                                UiTheme.S(56), _btnClear.Height);

            int chipsTop = pad + titleH + UiTheme.S(2);
            _chipsBar.SetBounds(pad, chipsTop, innerW, chipsH);
            int cx = 0;
            foreach (Control c in _chipsBar.Controls)
            {
                c.Left = cx;
                c.Top = Math.Max(0, (chipsH - c.Height) / 2);
                cx += c.Width + UiTheme.S(4);
                c.Visible = cx <= innerW;
            }

            int bottomTop = H - pad - bottomH;
            _bottomBar.SetBounds(pad, bottomTop, innerW, bottomH);
            int lw = UiTheme.S(34);
            var lblModel = _bottomBar.Controls[0]; var cbModel = _bottomBar.Controls[1];
            var lblTemp = _bottomBar.Controls[2]; var trTemp = _bottomBar.Controls[3];
            var lblVal = _bottomBar.Controls[4]; var chkSkip = _bottomBar.Controls[5];
            int bx = 0;
            lblModel.SetBounds(bx, 0, lw, bottomH); bx += lw;
            int cbW = Math.Max(UiTheme.S(80), innerW - lw - UiTheme.S(34) - UiTheme.S(100) - UiTheme.S(84) - UiTheme.S(12));
            cbModel.SetBounds(bx, Math.Max(0, (bottomH - cbModel.Height) / 2), cbW, cbModel.Height); bx += cbW + UiTheme.S(8);
            lblTemp.SetBounds(bx, 0, lw, bottomH); bx += lw;
            trTemp.SetBounds(bx, 0, UiTheme.S(76), trTemp.Height); bx += UiTheme.S(80);
            lblVal.SetBounds(bx, 0, UiTheme.S(26), bottomH); bx += UiTheme.S(28);
            chkSkip.SetBounds(bx, 0, Math.Max(UiTheme.S(60), innerW - bx), bottomH);

            int inputTop = bottomTop - gap - inputH;
            _inputArea.SetBounds(pad, inputTop, innerW, inputH);
            _send.SetBounds(_inputArea.Width - UiTheme.S(70), Math.Max(0, (_inputArea.Height - _send.Height) / 2), UiTheme.S(64), _send.Height);
            _stop.SetBounds(_inputArea.Width - UiTheme.S(70), Math.Max(0, (_inputArea.Height - _stop.Height) / 2), UiTheme.S(64), _stop.Height);
            _input.SetBounds(UiTheme.S(8), Math.Max(0, (_inputArea.Height - _input.Height) / 2),
                             Math.Max(UiTheme.S(40), _inputArea.Width - UiTheme.S(84)), _input.Height);

            // 占位提示贴在输入框第一行的左上角（多行 EDIT 的首行文字就从这里开始）
            _placeholder.SetBounds(_input.Left + UiTheme.S(1),
                                   _input.Top + UiTheme.S(1),
                                   Math.Max(UiTheme.S(20), _input.Width - UiTheme.S(2)),
                                   Math.Max(UiTheme.S(16), _input.Font.Height));

            int trTop = chipsTop + chipsH + gap;
            int trH = inputTop - gap - trTop;
            _outputHost.SetBounds(pad, trTop, innerW, Math.Max(UiTheme.S(60), trH));
        }

        // ---- 对外：由 AgentControl 在 UI 线程调用 ----

        public void ShowWelcomeIfNeeded()
        {
            if (_welcomeShown || _transcript.TextLength > 0) return;
            _welcomeShown = true;
            _transcript.ShowWelcome(_shell.Config.Active.Model);
        }

        public void ChatBeginUser(string text)
        {
            ShowWelcomeIfNeeded();
            _transcript.AddUser(text);
        }

        public void ChatBeginAssistant()
        {
            _transcript.BeginAssistant("Agent");
        }

        public void ChatAppendAssistant(string delta)
        {
            _transcript.AppendDelta(delta);
        }

        public void ChatEndAssistant()
        {
            _transcript.EndAssistant();
        }

        public void ChatToolCall(string name, string argsJson)
        {
            _transcript.AddToolCall(name, argsJson);
        }

        public void ChatToolResult(string text)
        {
            _transcript.AddToolResult(text);
        }

        public void ChatSystem(string text)
        {
            ShowWelcomeIfNeeded();
            _transcript.AddSystem(text);
        }

        public void ChatError(string text)
        {
            _transcript.AddError(text);
        }

        public void ClearTranscript()
        {
            _transcript.ClearAll();
            _welcomeShown = false;
        }

        public string TranscriptText { get { return _transcript.Text; } }

        /// <summary>把一段历史消息渲染到对话记录（供「历史」页载入时调用）。</summary>
        public void RenderHistory(System.Collections.Generic.List<ChatMessage> messages)
        {
            ClearTranscript();
            if (messages == null || messages.Count == 0) return;
            _welcomeShown = true;
            foreach (var m in messages)
            {
                if (m == null) continue;
                if (m.role == "user") _transcript.AddUser(m.content ?? "");
                else if (m.role == "assistant")
                {
                    if (string.IsNullOrWhiteSpace(m.content)) continue;
                    _transcript.BeginAssistant("Agent");
                    _transcript.AppendDelta(m.content);
                    _transcript.EndAssistant();
                }
                else if (m.role == "tool") _transcript.AddToolResult("[" + (m.name ?? "") + "] " + (m.content ?? ""));
            }
        }

        public void SetBusy(bool busy)
        {
            _send.Visible = !busy;
            _stop.Visible = busy;
            _send.Enabled = !busy;
        }

        /// <summary>从配置刷新模型下拉 / 温度 / 跳过确认。</summary>
        public void ReloadFromConfig()
        {
            var cfg = _shell.Config;
            _suppressModelEvent = true;
            try
            {
                _model.Items.Clear();
                foreach (var m in cfg.Models) _model.Items.Add(m.Name);
                int idx = _model.Items.IndexOf(cfg.ActiveName);
                _model.SelectedIndex = idx >= 0 ? idx : 0;
                _temp.Value = (int)Math.Round(Math.Max(0, Math.Min(2, cfg.Temperature)) * 10);
                _tempVal.Text = cfg.Temperature.ToString("0.0");
                _skip.Checked = cfg.SkipConfirm;
            }
            finally { _suppressModelEvent = false; }
        }

        /// <summary>
        /// 按内容行数调整输入框高度：空的时候一行，最多 6 行，再高就出滚动条。
        /// 同时受面板高度约束（不超过 1/3），避免把上方的对话记录挤没。
        ///
        /// 行数用 EDIT 自己的 `EM_GETLINECOUNT`（含 WordWrap 折行），不再用
        /// `GetLineFromCharIndex(TextLength)`——后者在末尾越界索引上会少算一行，
        /// 粘贴长文本时会「明明三行、高度只长一行」。
        /// </summary>
        private void AutoGrowInput()
        {
            if (!_ready) return;

            int lineH = Math.Max(UiTheme.S(14), _input.Font.Height);
            int lines = _input.DisplayLineCount;

            int minH = lineH + UiTheme.S(8);
            int byLines = minH + 5 * lineH;                       // 1 行 + 再加 5 行 = 6 行封顶
            int byPanel = Math.Max(minH + lineH, ClientSize.Height / 3);
            int maxH = Math.Min(byLines, byPanel);

            int want = Math.Max(minH, Math.Min(maxH, lines * lineH + UiTheme.S(8)));

            // 到达上限才给滚动条，否则会出现「只有一行却顶着个滚动条」的怪样子。
            // 走 ApplyScrollBars：它会重建句柄，必须把光标/滚动位置接回去。
            ScrollBars bars = want >= maxH ? ScrollBars.Vertical : ScrollBars.None;
            _input.ApplyScrollBars(bars);

            if (_input.Height != want)
            {
                _input.Height = want;
                LayoutChildren();
            }
        }

        /// <summary>
        /// 占位提示只在「没有焦点、也没有内容」时显示。光标一进来就藏起来，
        /// 免得闪烁的光标压在提示文字上。
        /// </summary>
        private void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            bool show = _input.TextLength == 0 && !_input.Focused;
            if (_placeholder.Visible != show) _placeholder.Visible = show;
        }

        private void Send()
        {
            string text = _input.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            _input.Text = "";
            AutoGrowInput();        // 清空后立刻缩回一行
            _input.Focus();
            var a = _shell.Config.Active;
            _shell.SetStatus("正在与模型交互…（" + a.Model + "）");
            SetBusy(true);
            Task.Run(() =>
            {
                try { _shell.Controller.SendAsync(text).Wait(); }
                catch { }
                finally
                {
                    try { BeginInvoke((Action)(() => { SetBusy(false); _shell.SetStatus("就绪"); })); }
                    catch { }
                }
            });
        }
    }
}
