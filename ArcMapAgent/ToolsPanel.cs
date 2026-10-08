using System;
using System.Drawing;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「工具」页（ArcMap 特色）：列出插件注册给模型/外部 Agent 的 ArcObjects 工具，
    /// 并给出 slash 命令速查——无需模型即可验证 ArcObjects 链路。
    /// QGIS 侧的「报告」页在 ArcMap 里改成工具目录，更贴合 ArcMap 用户的使用方式。
    /// </summary>
    public class ToolsPanel : PagePanel
    {
        private ListView _list;

        public ToolsPanel(AgentControl shell) : base(shell)
        {
            AddTitle("工具与命令");
            AddHint("这些工具既可由大模型通过 function calling 调用，也可由外部 Agent 经 MCP 调用；"
                  + "在「对话」页直接输入 / 命令可跳过模型直接执行。");

            var card = AddCard("ArcObjects 工具目录", "工具名即 MCP tools/call 的 name，也是模型 function calling 的函数名。", 320);
            _list = new ListView
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(220),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiTheme.Body,
                BackColor = Color.White
            };
            _list.Columns.Add("工具", UiTheme.S(130));
            _list.Columns.Add("说明", UiTheme.S(320));

            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(32), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            var refresh = new FlatButton("刷新", BtnStyle.Primary) { Width = UiTheme.S(60), Height = UiTheme.S(26) };
            refresh.Click += (s, e) => { ReloadTools(); Shell.SetStatus("工具列表已刷新"); };
            var tryBtn = new FlatButton("在对话页试用 /layers", BtnStyle.Ghost) { Width = UiTheme.S(148), Height = UiTheme.S(26) };
            tryBtn.Click += (s, e) => Shell.GotoTab(0);
            row.Controls.AddRange(new Control[] { refresh, tryBtn });
            card.Content.Controls.Add(row);
            card.Content.Controls.Add(_list);

            var card2 = AddCard("slash 命令速查", "在对话页输入框直接输入即可执行，无需模型能力。", 300);
            var box = new TextBox
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(210),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiTheme.Mono(12f),
                BackColor = Color.White,
                ForeColor = UiTheme.Text,
                Text = BuildSlashText()
            };
            card2.Content.Controls.Add(box);

            ReloadTools();
            FitChildren();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChildren(); }

        public void ReloadTools()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var t in Shell.Controller.Tools)
            {
                var it = new ListViewItem(t.name);
                it.SubItems.Add(t.description ?? "");
                _list.Items.Add(it);
            }
            _list.EndUpdate();
        }

        private static string BuildSlashText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var row in AgentController.SlashCommands)
                sb.AppendLine(row[0].PadRight(22) + row[1]);
            return sb.ToString();
        }
    }
}
