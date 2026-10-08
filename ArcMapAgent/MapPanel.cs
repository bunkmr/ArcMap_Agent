using System;
using System.Drawing;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 「地图」页（ArcMap 特色）：直接查看当前地图文档的数据框与图层，并对选中图层执行
    /// 缩放 / 显隐 / 移除 / 字段查看 / 添加 shp 等 ArcObjects 操作。
    /// QGIS 侧对应的是「工作流」页——ArcMap 里更有价值的是对地图文档本身的即时操作面板。
    /// </summary>
    public class MapPanel : PagePanel
    {
        private ListView _layers;
        private ListBox _frames;
        private Label _activeFrame;

        public MapPanel(AgentControl shell) : base(shell)
        {
            AddTitle("地图与图层");
            AddHint("实时查看当前 ArcMap 地图文档的数据框与图层，并对选中图层执行常用操作。");

            // ---- 数据框 ----
            var card = AddCard("数据框", "当前地图文档中的所有数据框（Map）。活跃数据框即「对话」页工具的作用对象。", 184);
            _activeFrame = new Label { Dock = DockStyle.Top, Height = UiTheme.TextRowHeight(UiTheme.BodyBold, 2), Font = UiTheme.BodyBold, ForeColor = UiTheme.AccentDark };
            _frames = new ListBox { Dock = DockStyle.Top, Height = UiTheme.S(60), Font = UiTheme.Body, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, IntegralHeight = false };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(32), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            var refresh = new FlatButton("刷新", BtnStyle.Primary) { Width = UiTheme.S(60), Height = UiTheme.S(26) };
            refresh.Click += (s, e) => { RefreshMap(); Shell.SetStatus("已刷新地图信息"); };
            var open = new FlatButton("打开 MXD…", BtnStyle.Ghost) { Width = UiTheme.S(88), Height = UiTheme.S(26) };
            open.Click += (s, e) => OpenMxd();
            row.Controls.AddRange(new Control[] { refresh, open });
            card.Content.Controls.Add(row);
            card.Content.Controls.Add(_frames);
            card.Content.Controls.Add(_activeFrame);

            // ---- 图层 ----
            var card2 = AddCard("图层", "选中一行后执行操作。图层序号与对话里的 /count、/zoom 等命令一致。", 330);
            _layers = new ListView
            {
                Dock = DockStyle.Top,
                Height = UiTheme.S(160),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = UiTheme.Body,
                BackColor = Color.White
            };
            _layers.Columns.Add("序号", UiTheme.S(44));
            _layers.Columns.Add("名称", UiTheme.S(130));
            _layers.Columns.Add("类型", UiTheme.S(90));
            _layers.Columns.Add("要素数", UiTheme.S(64));
            _layers.Columns.Add("可见", UiTheme.S(46));

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiTheme.S(72), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = UiTheme.Card, Padding = new Padding(0, UiTheme.S(4), 0, 0) };
            Btn(row2, "刷新", BtnStyle.Primary, (s, e) => { RefreshMap(); });
            Btn(row2, "缩放", BtnStyle.Ghost, (s, e) => Run("zoom_to_layer", "缩放"));
            Btn(row2, "显示", BtnStyle.Ghost, (s, e) => SetVisible(true));
            Btn(row2, "隐藏", BtnStyle.Ghost, (s, e) => SetVisible(false));
            Btn(row2, "字段", BtnStyle.Ghost, (s, e) => ShowFields());
            Btn(row2, "统计", BtnStyle.Ghost, (s, e) => Run("feature_count", "统计要素"));
            Btn(row2, "移除", BtnStyle.Danger, (s, e) => Remove());
            Btn(row2, "添加 shp…", BtnStyle.Success, (s, e) => AddShp());
            card2.Content.Controls.Add(row2);
            card2.Content.Controls.Add(_layers);

            FitChildren();
        }

        private void Btn(Control parent, string text, BtnStyle style, EventHandler h)
        {
            // Margin 的 top 保持 0；换行排列靠 bottom 撑出第二行
            var b = new FlatButton(text, style)
            {
                Width = TextRenderer.MeasureText(text, UiTheme.Body).Width + UiTheme.S(22),
                Height = UiTheme.S(26),
                Margin = new Padding(0, 0, UiTheme.S(6), UiTheme.S(4))
            };
            b.Click += h;
            parent.Controls.Add(b);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChildren(); }

        /// <summary>刷新数据框与图层信息。</summary>
        public void RefreshMap()
        {
            _activeFrame.Text = "活跃数据框：" + ArcMapTools.ActiveDataFrameName(Shell);
            _frames.BeginUpdate();
            _frames.Items.Clear();
            foreach (var f in ArcMapTools.GetDataFrames(Shell)) _frames.Items.Add(f);
            _frames.EndUpdate();

            _layers.BeginUpdate();
            _layers.Items.Clear();
            foreach (var li in ArcMapTools.GetLayers(Shell))
            {
                var it = new ListViewItem(li.Index.ToString());
                it.SubItems.Add(li.Name);
                it.SubItems.Add(li.TypeName);
                it.SubItems.Add(li.FeatureCount >= 0 ? li.FeatureCount.ToString() : "—");
                it.SubItems.Add(li.Visible ? "是" : "否");
                it.Tag = li.Index;
                if (!li.Visible) it.ForeColor = UiTheme.TextFaint;
                _layers.Items.Add(it);
            }
            _layers.EndUpdate();
        }

        private int SelectedIndex()
        {
            if (_layers.SelectedItems.Count == 0) return -1;
            return (int)_layers.SelectedItems[0].Tag;
        }

        private void Run(string tool, string verb)
        {
            int idx = SelectedIndex();
            if (idx < 0) { MessageBox.Show("请先在图层列表中选择一行。", "ArcMap Agent"); return; }
            string r = Shell.InvokeTool(tool, new System.Collections.Generic.Dictionary<string, object> { { "layer", idx.ToString() } });
            RefreshMap();
            MessageBox.Show(r, "ArcMap Agent · " + verb);
        }

        private void SetVisible(bool visible)
        {
            int idx = SelectedIndex();
            if (idx < 0) { MessageBox.Show("请先在图层列表中选择一行。", "ArcMap Agent"); return; }
            Shell.InvokeTool("set_layer_visibility", new System.Collections.Generic.Dictionary<string, object>
            { { "layer", idx.ToString() }, { "visible", visible } });
            RefreshMap();
        }

        private void Remove()
        {
            int idx = SelectedIndex();
            if (idx < 0) { MessageBox.Show("请先在图层列表中选择一行。", "ArcMap Agent"); return; }
            string name = _layers.SelectedItems[0].SubItems[1].Text;
            if (MessageBox.Show("确定移除图层「" + name + "」？", "ArcMap Agent",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Shell.InvokeTool("remove_layer", new System.Collections.Generic.Dictionary<string, object> { { "layer", idx.ToString() } });
            RefreshMap();
        }

        private void ShowFields()
        {
            int idx = SelectedIndex();
            if (idx < 0) { MessageBox.Show("请先在图层列表中选择一行。", "ArcMap Agent"); return; }
            string r = Shell.InvokeTool("list_fields", new System.Collections.Generic.Dictionary<string, object> { { "layer", idx.ToString() } });
            MessageBox.Show(r, "ArcMap Agent · 字段");
        }

        private void AddShp()
        {
            using (var dlg = new OpenFileDialog { Filter = "Shapefile (*.shp)|*.shp|所有文件 (*.*)|*.*", Title = "选择要添加的 Shapefile" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                string r = Shell.InvokeTool("add_shapefile", new System.Collections.Generic.Dictionary<string, object> { { "path", dlg.FileName } });
                RefreshMap();
                MessageBox.Show(r, "ArcMap Agent · 添加图层");
            }
        }

        private void OpenMxd()
        {
            using (var dlg = new OpenFileDialog { Filter = "ArcMap 文档 (*.mxd)|*.mxd", Title = "打开 MXD" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    if (AppContext.Application == null) { MessageBox.Show("无法获取 ArcMap 应用实例。", "ArcMap Agent"); return; }
                    AppContext.Application.OpenDocument(dlg.FileName);
                    RefreshMap();
                    Shell.SetStatus("已打开：" + dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, "ArcMap Agent"); }
            }
        }
    }
}
