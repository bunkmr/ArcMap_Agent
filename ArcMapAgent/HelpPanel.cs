using System;
using System.Drawing;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>「帮助」页：插件功能、页签说明与使用步骤。</summary>
    public class HelpPanel : PagePanel
    {
        private RichTextBox _rt;

        public HelpPanel(AgentControl shell) : base(shell)
        {
            AddTitle("帮助");
            AddHint("ArcMap Agent 把大模型与 ArcObjects 连起来：用自然语言或 / 命令直接操作 ArcMap。");

            var card = AddCard("使用说明", "v" + McpBridge.Version + " · C# .NET Add-In · 基于 ArcObjects 10.x", 430);
            _rt = new RichTextBox
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(350),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Card,
                ForeColor = UiTheme.Text,
                Font = UiTheme.Body,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            card.Content.Controls.Add(_rt);
            BuildDoc();
            FitChildren();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChildren(); }

        private void H(string text)
        {
            _rt.SelectionFont = UiTheme.UI(13f, FontStyle.Bold);
            _rt.SelectionColor = UiTheme.AccentDark;
            _rt.AppendText(text + "\n");
        }

        private void P(string text)
        {
            _rt.SelectionFont = UiTheme.Body;
            _rt.SelectionColor = UiTheme.Text;
            _rt.AppendText(text + "\n");
        }

        private void M(string text)
        {
            _rt.SelectionFont = UiTheme.Mono(12f);
            _rt.SelectionColor = UiTheme.Info;
            _rt.AppendText(text + "\n");
        }

        private void BuildDoc()
        {
            _rt.Clear();
            H("功能概览");
            P("7 个页签对应插件的全部能力，页签文字随停靠面板宽度自动换行。");

            H("① 对话");
            P("与当前模型对话。模型可通过 function calling 自动调用 ArcObjects 工具；");
            M("直接输入 /layers、/add <路径>、/count 1、/zoom 1 等命令可跳过模型立即执行。");
            P("底部可选择模型、调节温度、勾选「跳过确认」。");

            H("② 历史");
            P("把当前会话保存到本地（%APPDATA%\\ArcMapAgent\\conversations），或载入历史会话继续对话。");

            H("③ 模型");
            P("管理多个 OpenAI 兼容端点（本地 vLLM / Ollama / 云端网关）。支持厂商预设一键填入、连接测试。");

            H("④ MCP");
            P("把 ArcObjects 工具通过本地 127.0.0.1 HTTP + JSON-RPC 暴露给 Claude Desktop / Cursor 等外部 Agent。");
            P("强制校验访问令牌；「添加/移除图层、保存文档」等写操作默认不对内暴露，需显式开启特权。");

            H("⑤ 地图（ArcMap 特色）");
            P("查看当前地图文档的数据框与图层，直接执行缩放 / 显隐 / 移除 / 字段查看 / 添加 shp / 打开 MXD。");

            H("⑥ 工具");
            P("列出全部已注册工具及其说明，并提供 slash 命令速查表。");

            H("⑦ 帮助");
            P("本页。");

            H("常见问题");
            P("· 面板打不开：菜单「自定义 → 工具条」勾选 ArcMap Agent，再点击工具条上的机器人图标。");
            P("· 模型无响应：到「模型」页点「测试当前模型」，确认端点与密钥可用。");
            P("· 打开面板后工具提示无地图文档：请先让 ArcMap 主窗口获得焦点（面板依赖当前应用实例）。");
            P("· 配置与日志：%APPDATA%\\ArcMapAgent\\ （config.json / panel.log / conversations\\）。");
        }
    }
}
