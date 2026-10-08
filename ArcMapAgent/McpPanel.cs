using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「MCP」页：把插件的 ArcObjects 工具暴露给 Claude Desktop / Cursor 等外部 Agent。
    /// 功能对齐 QGIS Agent 的 MCP 页（自启开关 / 端口 / 令牌 / 特权工具 / 状态 / 客户端配置 / 连通性自检），
    /// 底层由 <see cref="McpBridge"/> 提供的本地 127.0.0.1 HTTP + JSON-RPC 端点实现。
    /// </summary>
    public class McpPanel : PagePanel
    {
        private CheckBox _autostart;
        private NumericUpDown _port;
        private FlatButton _toggle;
        private Label _status;

        private TextBox _token;
        private FlatButton _reveal;
        private bool _suppress;

        private CheckBox _dangerous;

        public McpPanel(AgentControl shell) : base(shell)
        {
            AddTitle("MCP 服务");
            AddHint("把本插件的 ArcMap 工具暴露给外部 Agent 调用。服务只监听 127.0.0.1，且强制校验访问令牌，"
                  + "局域网内其他机器无法连接。");

            // ---- 服务开关 ----
            var card = AddCard("服务开关", "开关只影响「下次启动插件时是否自动拉起服务」，改动立即写入设置。", 148);
            _autostart = new CheckBox { Text = "随插件启动时自动运行 MCP 服务", Dock = DockStyle.Top, Height = UiTheme.TextRowHeight(UiTheme.Body, 4), Font = UiTheme.Body, ForeColor = UiTheme.Text };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            row.Controls.Add(new Label { Text = "监听端口", Width = UiTheme.S(60), Height = UiTheme.TextRowHeight(UiTheme.Body, 2), TextAlign = ContentAlignment.MiddleLeft, Font = UiTheme.Body, ForeColor = UiTheme.TextSub });
            _port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Width = UiTheme.S(84), Font = UiTheme.Body, BorderStyle = BorderStyle.FixedSingle };
            row.Controls.Add(_port);
            _toggle = new FlatButton("启动服务", BtnStyle.Success) { Width = UiTheme.S(84), Height = UiTheme.S(26) };
            _toggle.Click += (s, e) => ToggleService();
            row.Controls.Add(_toggle);
            _status = new Label { Dock = DockStyle.Top, Height = UiTheme.TextRowHeight(UiTheme.Small, 4), Font = UiTheme.Small, ForeColor = UiTheme.TextSub, Text = "状态：未运行" };

            card.Content.Controls.Add(_status);
            card.Content.Controls.Add(row);
            card.Content.Controls.Add(_autostart);

            // ---- 访问令牌 ----
            var card2 = AddCard("访问令牌", "外部客户端必须携带该令牌才能调用工具；修改后立即生效，服务无需重启。默认以星号隐藏，避免截图泄露。", 132);
            _token = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, Font = UiTheme.Mono(12f), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            _reveal = new FlatButton("显示", BtnStyle.Ghost) { Width = UiTheme.S(56), Height = UiTheme.S(26) };
            _reveal.Click += (s, e) => { _token.UseSystemPasswordChar = !_token.UseSystemPasswordChar; _reveal.Text = _token.UseSystemPasswordChar ? "显示" : "隐藏"; };
            var regen = new FlatButton("重新生成", BtnStyle.Ghost) { Width = UiTheme.S(76), Height = UiTheme.S(26) };
            regen.Click += (s, e) =>
            {
                if (MessageBox.Show("重新生成令牌会使旧令牌立即失效，确定继续？", "ArcMap Agent",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                Shell.Config.McpToken = AppConfig.NewToken();
                Shell.Config.Save();
                _token.Text = Shell.Config.McpToken;
                _token.SelectionStart = 0;
                McpBridge.Get().UpdatePolicy(Shell.Config.McpAllowDangerous, Shell.Config.McpToken);
                Shell.SetStatus("MCP 令牌已重新生成");
            };
            var copy = new FlatButton("复制令牌", BtnStyle.Ghost) { Width = UiTheme.S(76), Height = UiTheme.S(26) };
            copy.Click += (s, e) => { try { Clipboard.SetText(Shell.Config.McpToken ?? ""); Shell.SetStatus("令牌已复制到剪贴板"); } catch { } };
            row2.Controls.AddRange(new Control[] { _reveal, regen, copy });
            card2.Content.Controls.Add(row2);
            card2.Content.Controls.Add(_token);

            // ---- 特权工具 ----
            var card3 = AddCard("特权工具", "默认关闭：添加图层 / 移除图层 / 保存文档等会改变地图文档或写盘的工具不对外暴露。开启后外部 Agent 才可请求它们。", 106);
            _dangerous = new CheckBox
            {
                Text = "允许外部 Agent 调用特权工具（添加 / 移除图层、保存文档）",
                Dock = DockStyle.Top, Height = UiTheme.TextRowHeight(UiTheme.Body, 6), Font = UiTheme.Body, ForeColor = UiTheme.Text
            };
            _dangerous.CheckedChanged += (s, e) =>
            {
                if (_suppress) return;
                Shell.Config.McpAllowDangerous = _dangerous.Checked;
                Shell.Config.Save();
                McpBridge.Get().UpdatePolicy(_dangerous.Checked, Shell.Config.McpToken);
                Shell.SetStatus("MCP 特权工具：" + (_dangerous.Checked ? "已允许" : "已禁止"));
            };
            card3.Content.Controls.Add(_dangerous);

            // ---- 客户端配置 ----
            var card4 = AddCard("客户端配置", "把下面的配置粘贴进支持 HTTP(streamable) 传输的 MCP 客户端；仅 stdio 的客户端需自备 HTTP 转发。", 128);
            var row4 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            var copyCfg = new FlatButton("复制客户端配置", BtnStyle.Primary) { Width = UiTheme.S(118), Height = UiTheme.S(26) };
            copyCfg.Click += (s, e) => CopyClientConfig();
            var check = new FlatButton("测试连通性", BtnStyle.Ghost) { Width = UiTheme.S(88), Height = UiTheme.S(26) };
            check.Click += (s, e) => SelfCheck();
            row4.Controls.AddRange(new Control[] { copyCfg, check });
            card4.Content.Controls.Add(row4);

            _autostart.CheckedChanged += (s, e) =>
            {
                if (_suppress) return;
                Shell.Config.McpAutostart = _autostart.Checked;
                Shell.Config.Save();
            };
            _port.ValueChanged += (s, e) =>
            {
                if (_suppress) return;
                Shell.Config.McpPort = (int)_port.Value;
                Shell.Config.Save();
            };

            McpBridge.Get().StatusChanged += msg =>
            {
                try { BeginInvoke((Action)(() => RefreshStatus())); } catch { }
            };

            ReloadFromConfig();
            FitChildren();

            // 自动启动
            if (Shell.Config.McpAutostart) Q();
        }

        private void Q()
        {
            try { StartService(); } catch { }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChildren(); }

        public void ReloadFromConfig()
        {
            var cfg = Shell.Config;
            _suppress = true;
            try
            {
                _autostart.Checked = cfg.McpAutostart;
                _port.Value = Math.Max(1024, Math.Min(65535, cfg.McpPort));
                _token.Text = cfg.McpToken ?? "";
                _token.SelectionStart = 0;
                _dangerous.Checked = cfg.McpAllowDangerous;
            }
            finally { _suppress = false; }
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var b = McpBridge.Get();
            if (b.IsRunning)
            {
                _status.ForeColor = UiTheme.Success;
                _status.Text = "状态：运行中 · 监听 127.0.0.1:" + b.Port
                             + " · 特权工具：" + (b.AllowDangerous ? "允许" : "禁止");
                _toggle.Text = "停止服务";
                _toggle.Style = BtnStyle.Danger;
            }
            else
            {
                _status.ForeColor = UiTheme.TextSub;
                _status.Text = "状态：未运行";
                _toggle.Text = "启动服务";
                _toggle.Style = BtnStyle.Success;
            }
            _toggle.Invalidate();
        }

        private void ToggleService()
        {
            if (McpBridge.Get().IsRunning) { McpBridge.Get().Stop(); Shell.SetStatus("MCP 服务已停止"); }
            else StartService();
            RefreshStatus();
        }

        private void StartService()
        {
            var cfg = Shell.Config;
            try
            {
                McpBridge.Get().Start(
                    (int)_port.Value,
                    cfg.McpToken,
                    cfg.McpAllowDangerous,
                    () => Shell.Controller.Tools,
                    (name, args) => Shell.InvokeTool(name, args));
                Shell.SetStatus("MCP 服务已启动：127.0.0.1:" + _port.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show("MCP 服务启动失败：" + ex.Message, "ArcMap Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Shell.SetStatus("MCP 服务启动失败");
            }
        }

        private void CopyClientConfig()
        {
            var cfg = Shell.Config;
            int port = McpBridge.Get().IsRunning ? McpBridge.Get().Port : (int)_port.Value;
            string json =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"arcmap-agent\": {\n" +
                "      \"url\": \"http://127.0.0.1:" + port + "/mcp\",\n" +
                "      \"headers\": { \"X-Auth-Token\": \"" + (cfg.McpToken ?? "") + "\" }\n" +
                "    }\n" +
                "  }\n" +
                "}";
            try { Clipboard.SetText(json); Shell.SetStatus("客户端配置已复制到剪贴板"); }
            catch { MessageBox.Show(json, "MCP 客户端配置"); }
        }

        private async void SelfCheck()
        {
            var b = McpBridge.Get();
            if (!b.IsRunning)
            {
                MessageBox.Show("服务未运行，请先点「启动服务」。", "ArcMap Agent");
                return;
            }
            Shell.SetStatus("正在测试 MCP 连通性…");
            string result;
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    var r = await http.GetStringAsync("http://127.0.0.1:" + b.Port + "/health");
                    result = "✔ 连通成功：" + r;
                }
            }
            catch (Exception ex) { result = "✘ 连通失败：" + ex.Message; }
            Shell.SetStatus(result);
            MessageBox.Show(result, "MCP 连通性自检");
        }
    }
}
