using System;
using System.Drawing;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「历史」页：把当前对话保存到本地、或从本地载入历史对话。
    /// 对应 QGIS Agent 的「历史」页，ArcMap 侧做成轻量版本（保存 / 载入 / 删除）。
    /// </summary>
    public class HistoryPanel : PagePanel
    {
        private ListView _list;
        private Label _currentInfo;

        public HistoryPanel(AgentControl shell) : base(shell)
        {
            AddTitle("对话历史");
            AddHint("对话以 JSON 文件保存在 %APPDATA%\\ArcMapAgent\\conversations\\，可随时载入继续。");

            var card = AddCard("当前对话", "把当前会话（含工具调用结果）保存为一条历史记录。", 126);
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            // 注意：这里**不要**再给后续按钮写 Margin = new Padding(6,0,0,0)。
            // 3px 的默认顶距与 0 顶距混用，正是整排按钮错开 3px（高 DPI 下 7px）的原因。
            // FlatButton 的默认外边距已经是 (0,0,6,0)，间距由它统一提供。
            var save = new FlatButton("保存当前对话", BtnStyle.Primary) { Width = UiTheme.S(110), Height = UiTheme.S(26) };
            save.Click += (s, e) => SaveCurrent();
            var clear = new FlatButton("清空当前对话", BtnStyle.Ghost) { Width = UiTheme.S(106), Height = UiTheme.S(26) };
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show("清空当前对话上下文与记录？", "ArcMap Agent", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                Shell.Controller.ClearHistory();
                Shell.Chat.ClearTranscript();
                RefreshInfo();
                Shell.SetStatus("已清空当前对话");
            };
            row.Controls.AddRange(new Control[] { save, clear });
            _currentInfo = new Label { Dock = DockStyle.Top, Height = UiTheme.TextRowHeight(UiTheme.Small, 4), Font = UiTheme.Small, ForeColor = UiTheme.TextSub };
            card.Content.Controls.Add(_currentInfo);
            card.Content.Controls.Add(row);

            var card2 = AddCard("已保存的对话", "双击一行即可载入。", 250);
            _list = new ListView
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(150),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiTheme.Body,
                BackColor = Color.White
            };
            _list.Columns.Add("标题", UiTheme.S(150));
            _list.Columns.Add("时间", UiTheme.S(130));
            _list.Columns.Add("消息数", UiTheme.S(60));
            _list.DoubleClick += (s, e) => LoadSelected();

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(34), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            var load = new FlatButton("载入", BtnStyle.Primary) { Width = UiTheme.S(60), Height = UiTheme.S(26) };
            load.Click += (s, e) => LoadSelected();
            var del = new FlatButton("删除", BtnStyle.Danger) { Width = UiTheme.S(60), Height = UiTheme.S(26) };
            del.Click += (s, e) => DeleteSelected();
            var refresh = new FlatButton("刷新", BtnStyle.Ghost) { Width = UiTheme.S(60), Height = UiTheme.S(26) };
            refresh.Click += (s, e) => ReloadList();
            row2.Controls.AddRange(new Control[] { load, del, refresh });
            card2.Content.Controls.Add(row2);
            card2.Content.Controls.Add(_list);

            ReloadList();
            RefreshInfo();
            FitChildren();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChildren(); }

        public void ReloadList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var rec in ConversationStore.List())
            {
                var it = new ListViewItem(rec.Title ?? "");
                it.SubItems.Add(rec.Created ?? "");
                it.SubItems.Add((rec.MessageCount).ToString());
                it.Tag = rec;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            RefreshInfo();
        }

        private void RefreshInfo()
        {
            int n = Shell.Controller.History.Count;
            int userMsgs = 0;
            foreach (var m in Shell.Controller.History) if (m.role == "user") userMsgs++;
            _currentInfo.Text = "当前上下文消息 " + n + " 条（其中用户提问 " + userMsgs + " 条）";
        }

        private void SaveCurrent()
        {
            var msgs = Shell.Controller.History;
            if (msgs.Count <= 1) { MessageBox.Show("当前对话还没有内容。", "ArcMap Agent"); return; }
            string title = "ArcMap 对话 " + DateTime.Now.ToString("MM-dd HH:mm");
            foreach (var m in msgs) if (m.role == "user" && !string.IsNullOrWhiteSpace(m.content))
                { title = m.content.Trim(); if (title.Length > 24) title = title.Substring(0, 24) + "…"; break; }
            string file = ConversationStore.Save(title, msgs);
            if (file == null) { MessageBox.Show("保存失败。", "ArcMap Agent"); return; }
            ReloadList();
            Shell.SetStatus("已保存对话：" + System.IO.Path.GetFileName(file));
        }

        private ConversationRecord Selected()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as ConversationRecord;
        }

        private void LoadSelected()
        {
            var rec = Selected();
            if (rec == null) { MessageBox.Show("请先选中一行。", "ArcMap Agent"); return; }
            Shell.Controller.LoadHistory(rec.Messages);
            Shell.Chat.RenderHistory(rec.Messages);
            RefreshInfo();
            Shell.GotoTab(0);
            Shell.SetStatus("已载入对话：" + rec.Title);
        }

        private void DeleteSelected()
        {
            var rec = Selected();
            if (rec == null) { MessageBox.Show("请先选中一行。", "ArcMap Agent"); return; }
            if (MessageBox.Show("删除这条历史记录？", "ArcMap Agent", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            try
            {
                // 找到对应文件并删除（按标题+时间匹配）
                foreach (var f in System.IO.Directory.GetFiles(ConversationStore.DirPath, "*.json"))
                {
                    try
                    {
                        var txt = System.IO.File.ReadAllText(f);
                        if (txt.IndexOf("\"" + rec.Created + "\"", StringComparison.Ordinal) >= 0)
                        { System.IO.File.Delete(f); break; }
                    }
                    catch { }
                }
            }
            catch { }
            ReloadList();
            Shell.SetStatus("已删除历史记录");
        }
    }
}
