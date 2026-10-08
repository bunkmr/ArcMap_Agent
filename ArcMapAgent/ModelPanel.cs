using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>模型配置的编辑对话框（名称 / 端点 / 模型 / 密钥）。</summary>
    internal class ModelEditDialog : Form
    {
        private readonly TextBox _name = new TextBox { Font = UiTheme.Body };
        private readonly TextBox _endpoint = new TextBox { Font = UiTheme.Body };
        private readonly TextBox _model = new TextBox { Font = UiTheme.Body };
        private readonly TextBox _key = new TextBox { Font = UiTheme.Body, UseSystemPasswordChar = true };

        public ModelProfile Result { get; private set; }

        public ModelEditDialog(ModelProfile src, bool isNew)
        {
            Text = isNew ? "添加模型" : "编辑模型";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(UiTheme.S(430), UiTheme.S(214));
            BackColor = UiTheme.Card;
            Font = UiTheme.Body;

            var lblHint = new Label
            {
                Text = "支持任意 OpenAI 兼容接口。端点通常以 /v1 结尾，插件会调用其 /chat/completions。",
                Font = UiTheme.Small, ForeColor = UiTheme.TextSub,
                Dock = DockStyle.Top,
                Height = UiTheme.TextRowHeight(UiTheme.Small, 6) * 2,   // 该文案会折行，给足两行
                Padding = new Padding(UiTheme.S(12), UiTheme.S(6), UiTheme.S(12), 0)
            };

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(UiTheme.S(12), UiTheme.S(4), UiTheme.S(12), UiTheme.S(8)) };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiTheme.S(74)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.S(32)));
            table.Controls.Add(MkLabel("名称"), 0, 0);
            table.Controls.Add(_name, 1, 0);
            table.Controls.Add(MkLabel("API 端点"), 0, 1);
            table.Controls.Add(_endpoint, 1, 1);
            table.Controls.Add(MkLabel("模型名"), 0, 2);
            table.Controls.Add(_model, 1, 2);
            table.Controls.Add(MkLabel("API Key"), 0, 3);
            table.Controls.Add(_key, 1, 3);
            foreach (Control c in new Control[] { _name, _endpoint, _model, _key }) { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, UiTheme.S(4), 0, UiTheme.S(4)); }
            host.Controls.Add(table);

            var bar = new Panel { Dock = DockStyle.Bottom, Height = UiTheme.S(44), Padding = new Padding(UiTheme.S(12), UiTheme.S(6), UiTheme.S(12), UiTheme.S(8)) };
            var ok = new FlatButton("确定", BtnStyle.Primary) { Width = UiTheme.S(78), Dock = DockStyle.Right, Margin = new Padding(UiTheme.S(6), 0, 0, 0) };
            var cancel = new FlatButton("取消", BtnStyle.Ghost) { Width = UiTheme.S(78), Dock = DockStyle.Right, Margin = new Padding(UiTheme.S(6), 0, 0, 0) };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show("请填写名称。", "ArcMap Agent"); return; }
                Result = new ModelProfile(_name.Text.Trim(), _endpoint.Text.Trim(), _model.Text.Trim(), _key.Text.Trim());
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bar.Controls.Add(cancel);
            bar.Controls.Add(ok);

            Controls.Add(host);
            Controls.Add(lblHint);
            Controls.Add(bar);

            if (src != null)
            {
                _name.Text = src.Name; _endpoint.Text = src.Endpoint; _model.Text = src.Model; _key.Text = src.ApiKey;
            }
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static Label MkLabel(string t)
        {
            return new Label { Text = t, Font = UiTheme.Body, ForeColor = UiTheme.TextSub, TextAlign = ContentAlignment.MiddleLeft };
        }
    }

    /// <summary>
    /// 「模型」页：多模型配置表 + 厂商预设 + 生成参数 + 连接测试。
    /// 对齐 QGIS Agent 的模型配置页，并将「跳过确认」等与对话页联动。
    /// </summary>
    public class ModelPanel : PagePanel
    {
        private ListView _list;
        private ComboBox _preset;
        private Label _testResult;
        private TrackBar _temp;
        private Label _tempVal;
        private CheckBox _skip;
        private bool _suppressTemp;

        private static readonly string[,] Presets = new string[,]
        {
            { "OpenAI",          "https://api.openai.com/v1",                        "gpt-4o-mini" },
            { "DeepSeek",        "https://api.deepseek.com/v1",                      "deepseek-chat" },
            { "智谱 GLM",        "https://open.bigmodel.cn/api/paas/v4",             "glm-4-flash" },
            { "通义千问",        "https://dashscope.aliyuncs.com/compatible-mode/v1","qwen-plus" },
            { "本地 vLLM",       "http://localhost:8000/v1",                          "Qwen2.5-7B-Instruct" },
            { "Ollama",          "http://localhost:11434/v1",                        "qwen2.5:7b" },
            { "自定义",          "",                                                  "" },
        };

        public ModelPanel(AgentControl shell) : base(shell)
        {
            AddTitle("大模型配置");
            AddHint("管理 API 端点与密钥。支持任意 OpenAI 兼容接口（本地 vLLM / Ollama / 云端网关均可）。");

            // ---- 模型列表 ----
            var card = AddCard("模型列表", "选中一行后可编辑、删除或设为当前模型。", 260);
            _list = new ListView
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(120),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiTheme.Body,
                BackColor = Color.White
            };
            _list.Columns.Add("名称", UiTheme.S(96));
            _list.Columns.Add("API 端点", UiTheme.S(190));
            _list.Columns.Add("模型", UiTheme.S(140));
            _list.Columns.Add("密钥", UiTheme.S(70));
            _list.DoubleClick += (s, e) => EditSelected();

            var btns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(70), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            AddBtn(btns, "＋ 添加", BtnStyle.Success, (s, e) => AddModel());
            AddBtn(btns, "编辑", BtnStyle.Ghost, (s, e) => EditSelected());
            AddBtn(btns, "删除", BtnStyle.Danger, (s, e) => DeleteSelected());
            AddBtn(btns, "设为当前", BtnStyle.Primary, (s, e) => SetActive());
            AddBtn(btns, "测试连接", BtnStyle.Ghost, (s, e) => TestConnection());

            // Dock 顺序：后加的在上 —— 先按钮后列表保证列表贴顶
            card.Content.Controls.Add(btns);
            card.Content.Controls.Add(_list);

            // ---- 厂商预设 ----
            var card2 = AddCard("快速添加：厂商预设", "选择厂商后点「填入」，会预先填好端点与模型名（密钥需自行填写）。", 106);
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card };
            _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiTheme.S(150), Font = UiTheme.Body, FlatStyle = FlatStyle.Flat };
            for (int i = 0; i < Presets.GetLength(0); i++) _preset.Items.Add(Presets[i, 0]);
            _preset.SelectedIndex = 0;
            row.Controls.Add(_preset);
            AddBtn(row, "填入", BtnStyle.Primary, (s, e) => ApplyPreset());
            card2.Content.Controls.Add(row);

            // ---- 生成参数 ----
            var card3 = AddCard("生成参数", "温度越低越精确，越高越有创造性。参数改动即时生效，无需重启。", 102);
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card };
            row2.Controls.Add(new Label { Text = "温度", Width = UiTheme.S(34), Height = UiTheme.TextRowHeight(UiTheme.Body, 2), TextAlign = ContentAlignment.MiddleLeft, Font = UiTheme.Body, ForeColor = UiTheme.TextSub });
            _temp = new TrackBar { Minimum = 0, Maximum = 20, TickStyle = TickStyle.None, Width = UiTheme.S(150), SmallChange = 1 };
            _tempVal = new Label { Text = "0.0", Width = UiTheme.S(30), Height = UiTheme.TextRowHeight(UiTheme.Body, 2), TextAlign = ContentAlignment.MiddleLeft, Font = UiTheme.Body, ForeColor = UiTheme.TextSub };
            _skip = new CheckBox { Text = "跳过工具执行确认", Width = UiTheme.S(160), Height = UiTheme.TextRowHeight(UiTheme.Body, 2), Font = UiTheme.Body, ForeColor = UiTheme.TextSub, TextAlign = ContentAlignment.MiddleLeft };
            row2.Controls.AddRange(new Control[] { _temp, _tempVal, _skip });
            card3.Content.Controls.Add(row2);
            _temp.ValueChanged += (s, e) =>
            {
                if (_suppressTemp) return;
                double v = _temp.Value / 10.0;
                _tempVal.Text = v.ToString("0.0");
                Shell.Config.Temperature = v;
            };
            _temp.MouseUp += (s, e) => { Shell.Config.Save(); Shell.Controller.ReloadConfig(); Shell.SetStatus("温度已设为 " + Shell.Config.Temperature.ToString("0.0")); };
            _skip.CheckedChanged += (s, e) => { Shell.Config.SkipConfirm = _skip.Checked; Shell.Config.Save(); Shell.Controller.ReloadConfig(); };

            // ---- 连接测试 ----
            var card4 = AddCard("连接测试", "向当前模型的 /chat/completions 发送一条最小请求，验证端点与密钥是否可用。", 150);
            var row3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card };
            AddBtn(row3, "▶ 测试当前模型", BtnStyle.Success, (s, e) => TestConnection());
            // Dock=Top 且「后加的靠上」：先加结果标签、后加按钮行，结果就落在按钮行下方
            // （原来结果标签没设 Dock，被按钮行压在下面重叠了）
            _testResult = new Label
            {
                Text = "",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = UiTheme.TextRowHeight(UiTheme.Small, 4) * 2,
                Font = UiTheme.Small,
                ForeColor = UiTheme.TextSub
            };
            card4.Content.Controls.Add(_testResult);
            card4.Content.Controls.Add(row3);

            ReloadFromConfig();
            FitChildren();
        }

        private void AddBtn(Control parent, string text, BtnStyle style, EventHandler onClick)
        {
            // Margin 的 top 保持 0（否则同排按钮错位，见 FlatButton 注释）；bottom 留给换行时的行距
            var b = new FlatButton(text, style)
            {
                Width = TextRenderer.MeasureText(text, UiTheme.Body).Width + UiTheme.S(26),
                Height = UiTheme.S(26),
                Margin = new Padding(0, 0, UiTheme.S(6), UiTheme.S(4))
            };
            b.Click += onClick;
            parent.Controls.Add(b);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitChildren();
        }

        public void ReloadFromConfig()
        {
            var cfg = Shell.Config;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var m in cfg.Models)
            {
                var it = new ListViewItem(m.Name);
                it.SubItems.Add(m.Endpoint);
                it.SubItems.Add(m.Model);
                it.SubItems.Add(string.IsNullOrEmpty(m.ApiKey) ? "—" : "已设置");
                it.Tag = m;
                if (string.Equals(m.Name, cfg.ActiveName, StringComparison.OrdinalIgnoreCase))
                {
                    it.Font = UiTheme.BodyBold;
                    it.ForeColor = UiTheme.AccentDark;
                    it.Text = "✔ " + m.Name;
                }
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            _suppressTemp = true;
            _temp.Value = (int)Math.Round(Math.Max(0, Math.Min(2, cfg.Temperature)) * 10);
            _tempVal.Text = cfg.Temperature.ToString("0.0");
            _skip.Checked = cfg.SkipConfirm;
            _suppressTemp = false;
            Shell.SetStatus("当前模型：" + cfg.Active.Name + "  ·  " + cfg.Active.Model);
        }

        private ModelProfile Selected()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as ModelProfile;
        }

        private void AddModel()
        {
            using (var dlg = new ModelEditDialog(null, true))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                Shell.Config.Models.Add(dlg.Result);
                if (Shell.Config.Models.Count == 1) Shell.Config.ActiveName = dlg.Result.Name;
                Shell.Config.Save();
                Shell.Controller.ReloadConfig();
                ReloadFromConfig();
            }
        }

        private void EditSelected()
        {
            var m = Selected();
            if (m == null) { MessageBox.Show("请先选中一行。", "ArcMap Agent"); return; }
            using (var dlg = new ModelEditDialog(m, false))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                bool wasActive = string.Equals(m.Name, Shell.Config.ActiveName, StringComparison.OrdinalIgnoreCase);
                m.Name = dlg.Result.Name; m.Endpoint = dlg.Result.Endpoint;
                m.Model = dlg.Result.Model; m.ApiKey = dlg.Result.ApiKey;
                if (wasActive) Shell.Config.ActiveName = m.Name;
                Shell.Config.Save();
                Shell.Controller.ReloadConfig();
                ReloadFromConfig();
            }
        }

        private void DeleteSelected()
        {
            var m = Selected();
            if (m == null) { MessageBox.Show("请先选中一行。", "ArcMap Agent"); return; }
            if (Shell.Config.Models.Count <= 1) { MessageBox.Show("至少保留一个模型配置。", "ArcMap Agent"); return; }
            if (MessageBox.Show("确定删除模型「" + m.Name + "」？", "ArcMap Agent",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Shell.Config.Models.Remove(m);
            if (string.Equals(m.Name, Shell.Config.ActiveName, StringComparison.OrdinalIgnoreCase))
                Shell.Config.ActiveName = Shell.Config.Models[0].Name;
            Shell.Config.Save();
            Shell.Controller.ReloadConfig();
            ReloadFromConfig();
        }

        private void SetActive()
        {
            var m = Selected();
            if (m == null) { MessageBox.Show("请先选中一行。", "ArcMap Agent"); return; }
            Shell.Config.ActiveName = m.Name;
            Shell.Config.Save();
            Shell.Controller.ReloadConfig();
            ReloadFromConfig();
        }

        private void ApplyPreset()
        {
            int i = _preset.SelectedIndex;
            if (i < 0) return;
            string name = Presets[i, 0], ep = Presets[i, 1], md = Presets[i, 2];
            using (var dlg = new ModelEditDialog(new ModelProfile(name, ep, md, ""), true))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                Shell.Config.Models.Add(dlg.Result);
                Shell.Config.ActiveName = dlg.Result.Name;
                Shell.Config.Save();
                Shell.Controller.ReloadConfig();
                ReloadFromConfig();
            }
        }

        private async void TestConnection()
        {
            var a = Shell.Config.Active;
            _testResult.ForeColor = UiTheme.TextSub;
            _testResult.Text = "测试中…  " + a.Endpoint;
            Shell.SetStatus("正在测试模型连接…");
            string error = null;
            try
            {
                var llm = new LlmClient();
                await Task.Run(() => llm.PingAsync(Shell.Config).Wait());
            }
            catch (Exception ex) { error = ex.Message; }
            if (string.IsNullOrEmpty(error))
            {
                _testResult.ForeColor = UiTheme.Success;
                _testResult.Text = "✔ 连接成功：" + a.Model + " @ " + a.Endpoint;
                Shell.SetStatus("模型连接测试成功");
            }
            else
            {
                _testResult.ForeColor = UiTheme.Danger;
                _testResult.Text = "✘ 失败：" + error;
                Shell.SetStatus("模型连接测试失败");
            }
        }
    }
}
