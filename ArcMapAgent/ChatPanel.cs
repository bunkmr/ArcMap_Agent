using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「对话」页：一问一答的对话记录 + 输入区 + 模型/温度/跳过确认。
    ///
    /// 自动化兼容：输入框保持**单行 TextBox**（UI Automation 的 ValuePattern 只对单行生效），
    /// 发送按钮保持 Name=AgentSend（可 Invoke），输出容器保持 Name=AgentOutput，
    /// 且所有输出同时写入 panel.log，供回归测试取证。
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
        private TextBox _input;
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
            _input = new TextBox
            {
                Name = "AgentInput",
                AccessibleName = "AgentInput",
                BorderStyle = BorderStyle.None,
                Font = UiTheme.Body,
                BackColor = Color.White
            };
            NativeHelpers.SetCue(_input, "输入指令…  例：/layers   或   按分公司字段给图层上色");
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
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    Send();
                }
            };
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

            Resize += (s, e) => LayoutChildren();
            _ready = true;
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
            _input.SetBounds(UiTheme.S(8), (_inputArea.Height - _input.Height) / 2,
                             Math.Max(UiTheme.S(40), _inputArea.Width - UiTheme.S(84)), _input.Height);

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

        private void Send()
        {
            string text = _input.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            _input.Text = "";
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
