using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ArcMapAgent
{
    /// <summary>
    /// 对话 / Agent 调度：管理历史、调用 LLM、派发 ArcObjects 工具。
    ///
    /// SendAsync 必须在后台线程执行（UI 线程会触发 Invoke 死锁），UI 发送按钮用 Task.Run 调用它。
    /// 配置统一从 <see cref="AgentControl.Config"/> 读取（同一个 AppConfig 实例），
    /// 因此「模型」页改动配置后无需重新加载即可生效。
    ///
    /// 工具调用是**多轮循环**：模型可以连续调用多个工具（也可以一轮里并行调多个）直到给出最终答复。
    /// 之前只跑一轮、且第二轮不带 tools，导致稍微复杂一点的任务（先看字段再着色）根本走不完。
    /// </summary>
    public class AgentController
    {
        private readonly AgentControl _ui;
        private readonly LlmClient _llm = new LlmClient();
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly List<ToolDef> _tools;
        private CancellationTokenSource _cts;

        /// <summary>单轮对话内允许的最大「模型 ⇄ 工具」往返次数，防止模型陷入死循环。</summary>
        private const int MaxToolRounds = 8;

        /// <summary>配置发生变化（模型/温度/MCP）时触发，供各页刷新显示。</summary>
        public event EventHandler ConfigReloaded;

        public AgentController(AgentControl ui)
        {
            _ui = ui;
            _tools = BuildTools();
            _history.Add(new ChatMessage { role = "system", content = SystemPrompt });
        }

        public List<ToolDef> Tools { get { return _tools; } }
        public List<ChatMessage> History { get { return _history; } }
        public AppConfig Config { get { return _ui.Config; } }

        /// <summary>
        /// 系统提示词。要点：① 必须真的调用工具而不是用文字描述；② 严禁伪造工具结果；
        /// ③ 严禁自导自演 "You:/Agent:" 假对话（这是之前输出里出现角色扮演的直接原因之一）。
        /// </summary>
        internal const string SystemPrompt =
            "你是嵌入 ArcMap 桌面端（ArcObjects / ArcMap 10.x）的 GIS 助手，可以直接读写用户当前打开的地图文档。\n" +
            "\n" +
            "【工具使用规则 —— 必须严格遵守】\n" +
            "1. 只要任务涉及读取或修改地图，就必须通过函数调用（tool call）去做，不要用文字写「我将调用 xxx」。\n" +
            "2. 严禁自己编造工具的执行结果。工具的返回结果只能来自系统，你无法预测它。\n" +
            "3. 严禁模拟用户发言，也不要输出 You: / User: / Agent: / 【调用工具：…】 这类标记。你只输出要对用户说的那段话。\n" +
            "4. 一次可以调用多个工具；如果一次调用不够，就继续调用下一个，直到任务完成。\n" +
            "5. 缺少必要参数时，先用查询类工具（list_layers / list_fields / unique_values / layer_info）弄清实际情况；\n" +
            "   例如用户说「按分公司上色」但没说图层和字段，应先列出图层与字段，再选最匹配的字段执行。\n" +
            "6. 工具报错时，读懂错误信息并换一种方式重试（比如改用图层序号、或先查可用字段），不要重复同样的调用。\n" +
            "7. **不要说「我做不到」**。下面这些 ArcMap 能力都已经有对应工具，遇到相关需求直接用：\n" +
            "   标注显示 set_labels（用户说「标注/把名称显示在图上」时用它，不要说只能手动设置）；\n" +
            "   取消标注 clear_labels；定义查询 set_definition_query（按条件只显示部分要素，不删数据）；\n" +
            "   分类着色 set_unique_value_renderer（文本/类别字段）；分级着色 set_graduated_colors（数值字段）；\n" +
            "   按位置选择 select_by_location；按属性选择 select_by_attribute；查看选中要素 get_selected_features；\n" +
            "   图层顺序 move_layer；书签 add_bookmark / goto_bookmark / list_bookmarks；\n" +
            "   视图控制 zoom_to_layer / zoom_to_full_extent / zoom_to_selected / zoom_to_scale / set_map_extent / get_map_extent；\n" +
            "   导出地图 export_map（png/jpg/tif/pdf）。\n" +
            "8. 超出上述工具范围的 GIS 分析（缓冲区、裁剪、相交、投影、空间连接、字段计算、要素转点、溶解、栅格分析、" +
            "栅格转矢量、Excel 互转……），一律用 run_gp 调用 ArcToolbox 工具完成；不确定工具名与参数顺序时先调 gp_tools 查。\n" +
            "   常用：Buffer_analysis（缓冲）、Clip_analysis（裁剪）、Intersect_analysis（相交）、Project_management（投影）、" +
            "SpatialJoin_analysis（空间连接）、CalculateField_management（字段计算）、FeatureToPoint_management（要素转点）。\n" +
            "   只有连 run_gp 也无法完成时，才说明限制并给出用户在 ArcMap 界面里的手工步骤。\n" +
            "\n" +
            "【回答风格】\n" +
            "- 用简体中文，Markdown 排版（小标题、列表、**加粗**、表格、`代码`）。\n" +
            "- 简洁准确：用一两句话说明「做了什么、结果如何、下一步建议」，不要复述工具返回的原始全文。\n" +
            "- 图层名、字段名、数值必须与工具返回保持一致，不要臆造。";

        /// <summary>供「模型」页在保存设置后调用，通知各页刷新。</summary>
        public void ReloadConfig()
        {
            var h = ConfigReloaded;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        /// <summary>请求中断当前对话（「停止」按钮）。</summary>
        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
        }

        // ---- 历史管理 ----

        public void ClearHistory()
        {
            _history.Clear();
            _history.Add(new ChatMessage { role = "system", content = SystemPrompt });
        }

        public void LoadHistory(List<ChatMessage> messages)
        {
            _history.Clear();
            if (messages == null || messages.Count == 0)
            {
                _history.Add(new ChatMessage { role = "system", content = SystemPrompt });
                return;
            }
            _history.AddRange(messages);
        }

        // ---- 工具定义 ----

        /// <summary>slash 命令速查（单一真源，供「工具」页与 /help 使用）。</summary>
        public static readonly List<string[]> SlashCommands = new List<string[]>
        {
            new[] { "/layers",              "列出当前数据框的全部图层" },
            new[] { "/add <路径>",          "添加 shapefile 图层" },
            new[] { "/info <图层>",         "查看图层详情（类型/要素数/范围/数据源）" },
            new[] { "/fields <图层>",       "列出图层字段（序号或名称）" },
            new[] { "/count <图层>",        "统计图层要素数量" },
            new[] { "/unique <图层> <字段>", "列出字段的唯一值及数量" },
            new[] { "/color <图层> <字段>", "按字段唯一值分类着色" },
            new[] { "/breaks <图层> <字段>", "按数值字段分级着色（默认 5 级分位数）" },
            new[] { "/label <图层> <字段>",  "显示标注（自动 10 磅字）" },
            new[] { "/unlabel <图层>",      "关闭标注" },
            new[] { "/where <图层> <条件>",  "设置定义查询，如 /where 支局名 = '官渡支局'" },
            new[] { "/stats <图层> <字段>", "字段统计（计数/最值/均值/总和）" },
            new[] { "/query <图层> <条件>", "按条件查询要素，如 分公司 = '官渡'" },
            new[] { "/sel <图层> <条件>",   "按条件选择要素（高亮）" },
            new[] { "/selwhere <源> <目标>", "按位置选择（源→目标，默认相交）" },
            new[] { "/extent",              "查看当前视图范围与比例尺" },
            new[] { "/bookmarks",           "列出书签" },
            new[] { "/export <路径>",       "导出地图（png/jpg/tif/pdf）" },
            new[] { "/gptools [关键词]",    "查询常用 ArcToolbox 工具" },
            new[] { "/zoom <图层>",         "缩放到图层范围" },
            new[] { "/full",                "缩放到全图" },
            new[] { "/clear",               "清除选择" },
            new[] { "/refresh",             "刷新地图视图" },
            new[] { "/remove <图层>",       "从地图移除图层" },
            new[] { "/show <图层>",         "显示图层" },
            new[] { "/hide <图层>",         "隐藏图层" },
            new[] { "/frames",              "列出所有数据框" },
            new[] { "/save [路径]",         "保存 / 另存为 MXD" },
            new[] { "/help",                "显示帮助与工具清单" },
        };

        private List<ToolDef> BuildTools()
        {
            var list = new List<ToolDef>();

            // ---- 读取 ----
            list.Add(new ToolDef
            {
                name = "list_layers",
                description = "列出当前 ArcMap 地图文档激活数据框中的所有图层（序号、名称、类型）。这是做任何图层操作前的第一步。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(StrTool("layer_info", "查看某个图层的详细信息：类型、要素数、几何类型、字段数、范围、坐标系、数据源路径。", "layer",
                "图层引用：图层序号（从 1 开始）或图层名称（可模糊匹配）。"));

            list.Add(StrTool("list_fields", "列出某要素图层的字段（序号、名称、类型、长度）。做符号化或查询前用它确认字段名。", "layer",
                "图层引用：图层序号（从 1 开始）或图层名称（可模糊匹配）。"));

            list.Add(StrTool("feature_count", "统计某要素图层的要素数量。", "layer",
                "图层引用：图层序号（从 1 开始）或图层名称（可模糊匹配）。"));

            list.Add(new ToolDef
            {
                name = "field_statistics",
                description = "对某字段做统计：计数、最小值、最大值、平均值、总和、标准差。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "field", "string", "字段名（需为数值字段才有完整统计）。"),
                    "layer", "field")
            });

            list.Add(new ToolDef
            {
                name = "unique_values",
                description = "列出某字段的所有唯一值及各值对应的要素数量。按字段分类着色之前必须先调用它。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "field", "string", "字段名。",
                    "max_values", "integer", "最多返回多少个值，默认 30。"),
                    "layer", "field")
            });

            // ---- 符号化 ----
            list.Add(new ToolDef
            {
                name = "set_unique_value_renderer",
                description = "按某字段的唯一值对图层做分类着色（每个类别一种颜色），并在内容列表更新图例。" +
                              "用户说「按某字段上色/分类显示/不同颜色区分」时用这个。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "field", "string", "用于分类的字段名。",
                    "max_classes", "integer", "最多显示多少个独立类别（默认 12，过多的值会并入「(其他)」）。"),
                    "layer", "field")
            });

            list.Add(new ToolDef
            {
                name = "set_simple_renderer",
                description = "把整个图层设置成单一符号（同一种颜色）。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "color", "string", "颜色：十六进制如 #16A34A，或中文名如 红/绿/蓝/青/橙/紫/灰。"),
                    "layer")
            });

            list.Add(new ToolDef
            {
                name = "set_layer_transparency",
                description = "设置图层透明度（0=不透明，100=全透明）。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "percent", "integer", "透明度百分比 0-100。"),
                    "layer", "percent")
            });

            // ---- 查询与选择 ----
            list.Add(new ToolDef
            {
                name = "query_features",
                description = "按 SQL 条件查询某图层的要素并返回若干行的属性值，用于回答「有哪些…」「…有多少」这类问题。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "where", "string", "SQL 条件，例如 分公司 = '官渡' ，或 population > 10000。留空表示全部要素。",
                    "max_rows", "integer", "最多返回多少行，默认 20。"),
                    "layer")
            });

            list.Add(new ToolDef
            {
                name = "select_by_attribute",
                description = "按 SQL 条件在地图上选中要素（选择集会高亮显示）。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "where", "string", "SQL 条件，例如 分公司 = '官渡'。"),
                    "layer", "where")
            });

            list.Add(new ToolDef
            {
                name = "clear_selection",
                description = "清除当前地图上所有图层的选择集。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            // ---- 图层编辑 ----
            list.Add(StrTool("add_shapefile", "向当前地图添加一个 shapefile 图层。", "path",
                "shapefile 的完整路径，例如 C:\\data\\roads.shp"));

            list.Add(StrTool("remove_layer", "从当前地图移除某图层。", "layer",
                "图层引用：图层序号（从 1 开始）或图层名称（可模糊匹配）。"));

            list.Add(new ToolDef
            {
                name = "set_layer_visibility",
                description = "设置某图层的可见性（显示/隐藏）。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "visible", "boolean", "true 显示，false 隐藏。"),
                    "layer", "visible")
            });

            list.Add(new ToolDef
            {
                name = "rename_layer",
                description = "重命名地图中的某个图层。",
                parameters = MakeParams(Props(
                    "layer", "string", "要重命名的图层（序号或名称）。",
                    "new_name", "string", "新的图层名。"),
                    "layer", "new_name")
            });

            // ---- 视图与文档 ----
            list.Add(StrTool("zoom_to_layer", "将地图缩放到某图层的范围。", "layer",
                "图层引用：图层序号（从 1 开始）或图层名称（可模糊匹配）。"));

            list.Add(new ToolDef
            {
                name = "zoom_to_full_extent",
                description = "把地图缩放到当前数据框的完整范围（全图）。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(new ToolDef
            {
                name = "list_data_frames",
                description = "列出当前地图文档中的所有数据框（Map）。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(new ToolDef
            {
                name = "refresh_view",
                description = "刷新地图视图与内容列表（通常在改完符号或数据后调用）。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(StrTool("save_document", "保存当前地图文档；若提供 path 则另存为 MXD。", "path",
                "可选，MXD 完整路径；留空则保存到当前文档。"));

            // ---- 标注（Labeling）----
            list.Add(new ToolDef
            {
                name = "set_labels",
                description = "为图层打开标注（等价于 ArcMap「图层属性 → Labels」）。用户说「标注」「把名称显示在地图上」" +
                              "「显示出某某字段」时**必须**用这个工具，不要说做不到。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "field", "string", "要标注的字段名，会自动包成 [字段]；若给 expression 可留空。",
                    "size", "number", "字号（磅），默认 10。",
                    "color", "string", "文字颜色：#RRGGBB 或 红/蓝/黑/白/灰 等中文色名。",
                    "bold", "boolean", "是否加粗。",
                    "expression", "string", "自定义标注表达式（可选），如 [名称] & \"(\" & [类型] & \")\"。给了就优先于 field。",
                    "halo", "boolean", "是否加白色晕圈（底图较花时建议 true，标注更清晰）。",
                    "which", "string", "标注哪些要素：all（默认）/ visible / selected。"),
                    "layer")
            });

            list.Add(StrTool("clear_labels", "关闭某图层的标注。", "layer", "图层引用：序号或名称。"));

            list.Add(StrTool("get_labels", "查看某图层当前的标注设置（是否开启、表达式、字号、范围）。", "layer",
                "图层引用：序号或名称。"));

            // ---- 定义查询 ----
            list.Add(new ToolDef
            {
                name = "set_definition_query",
                description = "给图层设置定义查询（等价于图层属性 → Definition Query），只显示满足条件的要素，不删除数据。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "where", "string", "SQL 条件，例如 分公司 = '官渡' 或 人口 > 50000。"),
                    "layer", "where")
            });

            list.Add(StrTool("clear_definition_query", "清除某图层的定义查询，恢复显示全部要素。", "layer",
                "图层引用：序号或名称。"));

            // ---- 分级着色 ----
            list.Add(new ToolDef
            {
                name = "set_graduated_colors",
                description = "按数值字段做分级着色（等价于 Symbology → Graduated colors），用于展示密度/强度。" +
                              "类别字段请改用 set_unique_value_renderer。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "field", "string", "数值字段名。",
                    "classes", "integer", "分级数量，默认 5。",
                    "method", "string", "分级方法：quantile（分位数，默认）/ equal（等间距）/ natural（自然断点）。",
                    "ramp", "string", "色带：红 / 绿 / 蓝 / 橙 / 紫 / 灰，默认浅青→深青。"),
                    "layer", "field")
            });

            // ---- 空间选择与选中要素 ----
            list.Add(new ToolDef
            {
                name = "select_by_location",
                description = "按位置选择（等价于「选择 → 按位置选择」）。源图层若已有选中要素则只用选中要素。",
                parameters = MakeParams(Props(
                    "source_layer", "string", "源图层（选择条件来自它）。",
                    "target_layer", "string", "目标图层（在它上面产生选择集）。",
                    "relation", "string", "空间关系：intersect（相交，默认）/ within（位于其内）/ contains（包含）/ touches / overlaps / crosses。"),
                    "source_layer", "target_layer")
            });

            list.Add(new ToolDef
            {
                name = "get_selected_features",
                description = "读取某图层当前选中要素的属性值。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "max_rows", "integer", "最多返回多少行，默认 20。"),
                    "layer")
            });

            list.Add(StrTool("zoom_to_selected", "缩放到某图层当前选中的要素。", "layer", "图层引用：序号或名称。"));

            // ---- 图层顺序 ----
            list.Add(new ToolDef
            {
                name = "move_layer",
                description = "调整图层在内容列表中的叠放顺序（越大越靠上）。",
                parameters = MakeParams(Props(
                    "layer", "string", "图层引用：序号或名称。",
                    "position", "string", "up（上移）/ down（下移）/ top（置顶）/ bottom（置底），也可直接给目标序号。"),
                    "layer", "position")
            });

            // ---- 书签与视图 ----
            list.Add(new ToolDef
            {
                name = "add_bookmark",
                description = "把当前视图范围保存为书签。",
                parameters = MakeParams(Props("name", "string", "书签名称。"), "name")
            });

            list.Add(new ToolDef
            {
                name = "goto_bookmark",
                description = "跳转到指定书签（名称可模糊匹配）。",
                parameters = MakeParams(Props("name", "string", "书签名称。"), "name")
            });

            list.Add(new ToolDef
            {
                name = "list_bookmarks",
                description = "列出当前数据框的所有空间书签。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(new ToolDef
            {
                name = "get_map_extent",
                description = "获取当前视图的范围、比例尺与坐标系。",
                parameters = MakeParams(new Dictionary<string, object>())
            });

            list.Add(new ToolDef
            {
                name = "set_map_extent",
                description = "把视图范围设置为指定坐标矩形。",
                parameters = MakeParams(Props(
                    "xmin", "number", "最小 X（左）。",
                    "ymin", "number", "最小 Y（下）。",
                    "xmax", "number", "最大 X（右）。",
                    "ymax", "number", "最大 Y（上）。"),
                    "xmin", "ymin", "xmax", "ymax")
            });

            list.Add(new ToolDef
            {
                name = "zoom_to_scale",
                description = "按比例尺缩放（保持当前中心点）。",
                parameters = MakeParams(Props("scale", "number", "比例尺分母，例如 50000 表示 1:50000。"), "scale")
            });

            // ---- 地图导出 ----
            list.Add(new ToolDef
            {
                name = "export_map",
                description = "把地图导出为图片或 PDF（等价于「文件 → 导出地图」）。",
                parameters = MakeParams(Props(
                    "path", "string", "输出文件完整路径，扩展名决定格式：.png/.jpg/.tif/.pdf/.emf。",
                    "width", "integer", "像素宽度，默认 1600。",
                    "height", "integer", "像素高度，默认 1200。",
                    "dpi", "number", "分辨率，默认 150（PDF 建议 300）。",
                    "target", "string", "data（当前数据框视图，默认）/ layout（页面布局）。"),
                    "path")
            });

            // ---- Geoprocessing（ArcToolbox 全量）----
            list.Add(new ToolDef
            {
                name = "run_gp",
                description = "执行任意 ArcToolbox 地理处理工具（Geoprocessor）。缓冲区、裁剪、相交、联合、投影、" +
                              "空间连接、字段计算、要素转点、溶解、栅格分析等几千个工具都通过它调用。" +
                              "工具名必须带工具箱别名后缀，例如 Buffer_analysis、Clip_analysis、CalculateField_management。" +
                              "参数按位置顺序传字符串数组。",
                parameters = MakeParams(new Dictionary<string, object>
                {
                    ["tool"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "工具名（含别名后缀），如 Buffer_analysis。"
                    },
                    ["params"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                        ["description"] = "按位置的参数列表（字符串），例如 [\"道路\", \"D:\\\\out\\\\buf.shp\", \"100 Meters\"]。图层用内容列表里的名称。"
                    }
                }, "tool", "params")
            });

            list.Add(StrTool("gp_tools", "按关键词查询常用 ArcToolbox 工具名与参数说明（如 缓冲、裁剪、投影、统计）。", "keyword",
                "关键词，如 缓冲 / 裁剪 / 字段 / 栅格；留空列出全部常用工具。"));

            return list;
        }

        private static Dictionary<string, object> Props(params string[] triplets)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 2 < triplets.Length; i += 3)
            {
                d[triplets[i]] = new Dictionary<string, object>
                {
                    ["type"] = triplets[i + 1],
                    ["description"] = triplets[i + 2]
                };
            }
            return d;
        }

        private ToolDef StrTool(string name, string desc, string pname, string pdesc)
        {
            var props = new Dictionary<string, object>();
            props[pname] = new Dictionary<string, object> { ["type"] = "string", ["description"] = pdesc };
            return new ToolDef { name = name, description = desc, parameters = MakeParams(props, pname) };
        }

        private object MakeParams(Dictionary<string, object> props, params string[] required)
        {
            var p = new Dictionary<string, object>();
            p["type"] = "object";
            p["properties"] = props;
            var req = new List<object>();
            foreach (var r in required) req.Add(r);
            p["required"] = req;
            return p;
        }

        // ---- 对话 ----

        public async Task SendAsync(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return;

            string t = userText.Trim();

            // slash 命令：直接执行工具，无需模型能力即可验证 ArcObjects 链路
            string slash = TrySlash(t);
            if (slash != null)
            {
                _ui.Write(slash);
                return;
            }

            _history.Add(new ChatMessage { role = "user", content = userText });
            _ui.ChatBeginUser(userText);
            AgentLog.Append("You: " + userText);

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            bool finished = false;

            try
            {
                for (int round = 0; round < MaxToolRounds; round++)
                {
                    ct.ThrowIfCancellationRequested();

                    _ui.ChatBeginAssistant();
                    var result = await _llm.ChatAsync(
                        _history, _tools, d => _ui.ChatAppendAssistant(d), Config, ct);

                    if (result.HasToolCalls)
                    {
                        // 1) assistant 消息必须记下本轮发起的 tool_calls（含 id）
                        var asst = new ChatMessage
                        {
                            role = "assistant",
                            content = result.content ?? "",
                            tool_calls = new List<ToolCallRef>()
                        };
                        foreach (var tc in result.toolCalls)
                        {
                            asst.tool_calls.Add(new ToolCallRef
                            {
                                id = string.IsNullOrEmpty(tc.id)
                                        ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 12)
                                        : tc.id,
                                name = tc.name ?? "",
                                argumentsJson = string.IsNullOrEmpty(tc.argumentsJson) ? "{}" : tc.argumentsJson
                            });
                        }
                        _history.Add(asst);
                        _ui.ChatEndAssistant();

                        // 2) 逐个执行，并把 tool 消息用同一个 id 绑回去
                        foreach (var tc in asst.tool_calls)
                        {
                            ct.ThrowIfCancellationRequested();
                            _ui.ChatToolCall(tc.name, tc.argumentsJson);
                            AgentLog.Append("[调用工具] " + tc.name + "  " + tc.argumentsJson);

                            string toolResult = ExecuteToolGuarded(tc);
                            _ui.ChatToolResult(toolResult);
                            AgentLog.Append("[工具结果] " + toolResult);

                            _history.Add(new ChatMessage
                            {
                                role = "tool",
                                content = toolResult,
                                name = tc.name,
                                tool_call_id = tc.id
                            });
                        }

                        _ui.SetStatus("工具执行完成，正在继续…");
                        continue;   // 带上工具结果继续下一轮（仍然带 tools）
                    }

                    // 没有工具调用 → 本轮就是最终答复
                    _ui.ChatEndAssistant();
                    _history.Add(new ChatMessage { role = "assistant", content = result.content ?? "" });
                    AgentLog.Append("Agent: " + (result.content ?? ""));
                    finished = true;
                    break;
                }

                if (!finished)
                {
                    _ui.ChatError("已经连续调用工具 " + MaxToolRounds + " 轮仍未结束，为避免死循环先停下。请把任务拆小一点再试。");
                }
            }
            catch (OperationCanceledException)
            {
                try { _ui.ChatEndAssistant(); } catch { }
                _ui.ChatError("已停止。");
            }
            catch (Exception ex)
            {
                try { _ui.ChatEndAssistant(); } catch { }
                _ui.ChatError(ex.Message);
                AgentLog.Append("[错误] " + ex.Message);
            }
            finally
            {
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
        }

        /// <summary>处理 slash 命令；不是命令则返回 null。</summary>
        private string TrySlash(string t)
        {
            if (t.Equals("/layers", StringComparison.OrdinalIgnoreCase))
                return ">> 列出图层…\n" + ArcMapTools.ListLayers(_ui) + "\n";
            if (t.Equals("/frames", StringComparison.OrdinalIgnoreCase))
                return ">> 数据框…\n" + ArcMapTools.ListDataFrames(_ui) + "\n";
            if (t.Equals("/full", StringComparison.OrdinalIgnoreCase))
                return ">> 缩放到全图…\n" + ArcMapTools.ZoomToFullExtent(_ui) + "\n";
            if (t.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                return ">> 清除选择…\n" + ArcMapTools.ClearSelection(_ui) + "\n";
            if (t.Equals("/refresh", StringComparison.OrdinalIgnoreCase))
                return ">> 刷新视图…\n" + ArcMapTools.RefreshView(_ui) + "\n";

            // 单参数命令：命令后的整串都是图层引用（图层名可能含空格）
            string one = OneArg(t, "/add");
            if (one != null) return ">> 添加图层: " + one + "\n" + ArcMapTools.AddShapefile(_ui, Unquote(one)) + "\n";
            one = OneArg(t, "/info");
            if (one != null) return ">> 图层详情: " + one + "\n" + ArcMapTools.LayerInfo(_ui, one) + "\n";
            one = OneArg(t, "/fields");
            if (one != null) return ">> 字段: " + one + "\n" + ArcMapTools.ListFields(_ui, one) + "\n";
            one = OneArg(t, "/count");
            if (one != null) return ">> 要素数: " + one + "\n" + ArcMapTools.FeatureCount(_ui, one) + "\n";
            one = OneArg(t, "/zoom");
            if (one != null) return ">> 缩放: " + one + "\n" + ArcMapTools.ZoomToLayer(_ui, one) + "\n";
            one = OneArg(t, "/remove");
            if (one != null) return ">> 移除: " + one + "\n" + ArcMapTools.RemoveLayer(_ui, one) + "\n";
            one = OneArg(t, "/show");
            if (one != null) return ">> 显示: " + one + "\n" + ArcMapTools.SetLayerVisibility(_ui, one, true) + "\n";
            one = OneArg(t, "/hide");
            if (one != null) return ">> 隐藏: " + one + "\n" + ArcMapTools.SetLayerVisibility(_ui, one, false) + "\n";

            // 双参数命令：最后一个 token 是字段/条件，前面的是图层引用
            string layer, arg;
            if (TwoArg(t, "/unique", out layer, out arg))
                return ">> 唯一值: " + layer + " / " + arg + "\n" + ArcMapTools.UniqueValues(_ui, layer, arg, 30) + "\n";
            if (TwoArg(t, "/color", out layer, out arg))
                return ">> 分类着色: " + layer + " / " + arg + "\n" + ArcMapTools.SetUniqueValueRenderer(_ui, layer, arg, 12) + "\n";
            if (TwoArg(t, "/stats", out layer, out arg))
                return ">> 字段统计: " + layer + " / " + arg + "\n" + ArcMapTools.FieldStatistics(_ui, layer, arg) + "\n";
            if (TwoArg(t, "/query", out layer, out arg))
                return ">> 查询: " + layer + " / " + arg + "\n" + ArcMapTools.QueryFeatures(_ui, layer, arg, 20) + "\n";
            if (TwoArg(t, "/sel", out layer, out arg))
                return ">> 按属性选择: " + layer + " / " + arg + "\n" + ArcMapTools.SelectByAttribute(_ui, layer, arg) + "\n";
            if (TwoArg(t, "/label", out layer, out arg))
                return ">> 显示标注: " + layer + " / " + arg + "\n"
                       + ArcMapTools.SetLabels(_ui, layer, arg, 10, "", false, null, true, "all") + "\n";
            if (TwoArg(t, "/breaks", out layer, out arg))
                return ">> 分级着色: " + layer + " / " + arg + "\n"
                       + ArcMapTools.SetGraduatedColors(_ui, layer, arg, 5, "quantile", "") + "\n";
            if (TwoArg(t, "/where", out layer, out arg))
                return ">> 定义查询: " + layer + " / " + arg + "\n"
                       + ArcMapTools.SetDefinitionQuery(_ui, layer, arg) + "\n";
            if (TwoArg(t, "/selwhere", out layer, out arg))
                return ">> 按位置选择: " + layer + " → " + arg + "\n"
                       + ArcMapTools.SelectByLocation(_ui, layer, arg, "intersect") + "\n";

            one = OneArg(t, "/unlabel");
            if (one != null) return ">> 关闭标注: " + one + "\n" + ArcMapTools.ClearLabels(_ui, one) + "\n";
            one = OneArg(t, "/export");
            if (one != null) return ">> 导出地图: " + one + "\n"
                                    + ArcMapTools.ExportMap(_ui, Unquote(one), 1600, 1200, 150, "data") + "\n";

            if (t.Equals("/extent", StringComparison.OrdinalIgnoreCase))
                return ">> 视图范围…\n" + ArcMapTools.GetMapExtent(_ui) + "\n";
            if (t.Equals("/bookmarks", StringComparison.OrdinalIgnoreCase))
                return ">> 书签…\n" + ArcMapTools.ListBookmarks(_ui) + "\n";
            if (t.Equals("/gptools", StringComparison.OrdinalIgnoreCase))
                return ">> 常用 GP 工具…\n" + ArcMapTools.SearchGpTools(_ui, "") + "\n";
            if (t.StartsWith("/gptools ", StringComparison.OrdinalIgnoreCase))
                return ">> GP 工具查询: " + t.Substring(8).Trim() + "\n"
                       + ArcMapTools.SearchGpTools(_ui, t.Substring(8).Trim()) + "\n";

            if (t.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.AppendLine(">> 可用工具（模型可自动调用）：");
                foreach (var tool in _tools) sb.AppendLine("   · `" + tool.name + "` — " + tool.description);
                sb.AppendLine();
                sb.AppendLine(">> slash 命令（手动直连 ArcObjects，不经模型）：");
                foreach (var row in SlashCommands) sb.AppendLine("   `" + row[0] + "`  " + row[1]);
                sb.AppendLine();
                sb.AppendLine("提示：在「模型」页配置端点后可自然语言对话；「MCP」页可把工具暴露给外部 Agent。");
                return sb.ToString();
            }

            if (t.StartsWith("/save", StringComparison.OrdinalIgnoreCase))
            {
                string p = t.Length > 5 ? t.Substring(5).Trim() : "";
                return ">> 保存…\n" + ArcMapTools.SaveDocument(_ui, Unquote(p)) + "\n";
            }
            return null;
        }

        private static string Unquote(string s)
        {
            return string.IsNullOrEmpty(s) ? s : s.Trim().Trim('"');
        }

        /// <summary>形如 "/cmd xxx" 的单参数命令；不匹配返回 null。</summary>
        private static string OneArg(string t, string cmd)
        {
            if (!t.StartsWith(cmd + " ", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = t.Substring(cmd.Length + 1).Trim();
            return rest.Length == 0 ? null : rest;
        }

        /// <summary>形如 "/cmd &lt;图层&gt; &lt;字段&gt;" 的双参数命令：最后一段是字段，其余是图层。</summary>
        private static bool TwoArg(string t, string cmd, out string layer, out string arg)
        {
            layer = null; arg = null;
            if (!t.StartsWith(cmd + " ", StringComparison.OrdinalIgnoreCase)) return false;
            string rest = t.Substring(cmd.Length + 1).Trim();
            if (rest.Length == 0) return false;
            int sp = rest.LastIndexOf(' ');
            if (sp <= 0) { layer = ""; arg = rest; }
            else { layer = rest.Substring(0, sp).Trim(); arg = rest.Substring(sp + 1).Trim(); }
            return true;
        }

        /// <summary>带写操作确认 + 异常兜底的工具执行。</summary>
        private string ExecuteToolGuarded(ToolCallRef tc)
        {
            string name = tc.name ?? "";
            string args = string.IsNullOrEmpty(tc.argumentsJson) ? "{}" : tc.argumentsJson;
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(args);
                if (d == null) d = new Dictionary<string, object>();

                if (IsWriteTool(name) && !Config.SkipConfirm)
                {
                    bool ok = false;
                    _ui.Invoke((Action)(() =>
                    {
                        ok = System.Windows.Forms.MessageBox.Show(
                            "模型请求执行会修改地图的操作：" + name + "\n参数：" + DescribeArgs(d) +
                            "\n\n是否允许？",
                            "ArcMap Agent", System.Windows.Forms.MessageBoxButtons.OKCancel,
                            System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.OK;
                    }));
                    if (!ok) return "用户拒绝了工具 " + name + " 的执行。";
                }
                return ExecuteToolByName(name, d);
            }
            catch (Exception ex)
            {
                return "工具执行异常: " + ex.Message;
            }
        }

        private static string DescribeArgs(Dictionary<string, object> d)
        {
            if (d == null || d.Count == 0) return "(无)";
            var sb = new StringBuilder();
            foreach (var kv in d)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>会改动地图内容、值得让用户确认的工具。</summary>
        private static bool IsWriteTool(string name)
        {
            switch (name)
            {
                case "add_shapefile":
                case "remove_layer":
                case "save_document":
                case "rename_layer":
                case "set_unique_value_renderer":
                case "set_simple_renderer":
                case "set_graduated_colors":
                case "set_layer_transparency":
                case "set_labels":
                case "clear_labels":
                case "set_definition_query":
                case "clear_definition_query":
                case "select_by_attribute":
                case "select_by_location":
                case "clear_selection":
                case "move_layer":
                case "add_bookmark":
                case "set_map_extent":
                case "zoom_to_scale":
                case "export_map":
                case "run_gp":            // GP 可能写出新数据 / 覆盖已有数据，属于写操作
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>把工具参数里的字符串数组取出来（模型可能给 JSON 数组，也可能给逗号分隔的字符串）。</summary>
        private static string[] StrArray(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return new string[0];
            var v = d[key];
            var list = new List<string>();
            var en = v as System.Collections.IEnumerable;
            if (en != null && !(v is string))
            {
                foreach (var o in en) list.Add(o == null ? "" : Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture));
                return list.ToArray();
            }
            // 退化情形：模型把数组写成了一个 JSON 字符串或分号/逗号分隔的串
            string s = v.ToString().Trim();
            if (s.Length == 0) return new string[0];
            if (s.StartsWith("["))
            {
                try
                {
                    var arr = new JavaScriptSerializer().Deserialize<object[]>(s);
                    if (arr != null)
                    {
                        foreach (var o in arr) list.Add(o == null ? "" : o.ToString());
                        return list.ToArray();
                    }
                }
                catch { }
            }
            foreach (var part in s.Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(part.Trim());
            return list.ToArray();
        }

        /// <summary>按名称执行工具（对话、地图页、MCP 桥接共用同一入口）。</summary>
        public string ExecuteToolByName(string name, Dictionary<string, object> d)
        {
            if (d == null) d = new Dictionary<string, object>();
            try
            {
                string S(string key)
                {
                    if (!d.ContainsKey(key) || d[key] == null) return "";
                    return d[key].ToString();
                }
                int I(string key, int fallback)
                {
                    int v;
                    if (d.ContainsKey(key) && d[key] != null && int.TryParse(d[key].ToString(), out v)) return v;
                    return fallback;
                }
                bool B(string key, bool fallback)
                {
                    if (!d.ContainsKey(key) || d[key] == null) return fallback;
                    var v = d[key];
                    if (v is bool) return (bool)v;
                    string s = v.ToString().Trim();
                    return s.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                        || s.Equals("是", StringComparison.OrdinalIgnoreCase);
                }
                double D(string key, double fallback)
                {
                    double v;
                    if (d.ContainsKey(key) && d[key] != null
                        && double.TryParse(Convert.ToString(d[key], System.Globalization.CultureInfo.InvariantCulture),
                                           System.Globalization.NumberStyles.Any,
                                           System.Globalization.CultureInfo.InvariantCulture, out v))
                        return v;
                    return fallback;
                }

                switch (name)
                {
                    // 读取
                    case "list_layers": return ArcMapTools.ListLayers(_ui);
                    case "layer_info": return ArcMapTools.LayerInfo(_ui, S("layer"));
                    case "list_fields": return ArcMapTools.ListFields(_ui, S("layer"));
                    case "feature_count": return ArcMapTools.FeatureCount(_ui, S("layer"));
                    case "field_statistics": return ArcMapTools.FieldStatistics(_ui, S("layer"), S("field"));
                    case "unique_values": return ArcMapTools.UniqueValues(_ui, S("layer"), S("field"), I("max_values", 30));

                    // 符号化
                    case "set_unique_value_renderer":
                        return ArcMapTools.SetUniqueValueRenderer(_ui, S("layer"), S("field"), I("max_classes", 12));
                    case "set_simple_renderer":
                        return ArcMapTools.SetSimpleRenderer(_ui, S("layer"), S("color"));
                    case "set_layer_transparency":
                        return ArcMapTools.SetLayerTransparency(_ui, S("layer"), I("percent", 30));

                    // 查询与选择
                    case "query_features":
                        return ArcMapTools.QueryFeatures(_ui, S("layer"), S("where"), I("max_rows", 20));
                    case "select_by_attribute":
                        return ArcMapTools.SelectByAttribute(_ui, S("layer"), S("where"));
                    case "clear_selection": return ArcMapTools.ClearSelection(_ui);

                    // 图层编辑
                    case "add_shapefile": return ArcMapTools.AddShapefile(_ui, S("path"));
                    case "remove_layer": return ArcMapTools.RemoveLayer(_ui, S("layer"));
                    case "set_layer_visibility":
                        return ArcMapTools.SetLayerVisibility(_ui, S("layer"), B("visible", true));
                    case "rename_layer": return ArcMapTools.RenameLayer(_ui, S("layer"), S("new_name"));

                    // 视图与文档
                    case "zoom_to_layer": return ArcMapTools.ZoomToLayer(_ui, S("layer"));
                    case "zoom_to_full_extent": return ArcMapTools.ZoomToFullExtent(_ui);
                    case "zoom_to_selected": return ArcMapTools.ZoomToSelected(_ui, S("layer"));
                    case "list_data_frames": return ArcMapTools.ListDataFrames(_ui);
                    case "refresh_view": return ArcMapTools.RefreshView(_ui);
                    case "save_document": return ArcMapTools.SaveDocument(_ui, S("path"));

                    // 标注
                    case "set_labels":
                        return ArcMapTools.SetLabels(_ui, S("layer"), S("field"),
                            D("size", 10), S("color"), B("bold", false), S("expression"),
                            B("halo", false), S("which"));
                    case "clear_labels": return ArcMapTools.ClearLabels(_ui, S("layer"));
                    case "get_labels": return ArcMapTools.GetLabels(_ui, S("layer"));

                    // 定义查询
                    case "set_definition_query":
                        return ArcMapTools.SetDefinitionQuery(_ui, S("layer"), S("where"));
                    case "clear_definition_query":
                        return ArcMapTools.ClearDefinitionQuery(_ui, S("layer"));

                    // 分级着色
                    case "set_graduated_colors":
                        return ArcMapTools.SetGraduatedColors(_ui, S("layer"), S("field"),
                            I("classes", 5), S("method"), S("ramp"));

                    // 空间选择
                    case "select_by_location":
                        return ArcMapTools.SelectByLocation(_ui, S("source_layer"), S("target_layer"), S("relation"));
                    case "get_selected_features":
                        return ArcMapTools.GetSelected(_ui, S("layer"), I("max_rows", 20));

                    // 图层顺序 / 书签 / 视图
                    case "move_layer": return ArcMapTools.MoveLayer(_ui, S("layer"), S("position"));
                    case "add_bookmark": return ArcMapTools.AddBookmark(_ui, S("name"));
                    case "goto_bookmark": return ArcMapTools.GotoBookmark(_ui, S("name"));
                    case "list_bookmarks": return ArcMapTools.ListBookmarks(_ui);
                    case "get_map_extent": return ArcMapTools.GetMapExtent(_ui);
                    case "set_map_extent":
                        return ArcMapTools.SetMapExtent(_ui, D("xmin", 0), D("ymin", 0), D("xmax", 0), D("ymax", 0));
                    case "zoom_to_scale": return ArcMapTools.ZoomToScale(_ui, D("scale", 0));

                    // 导出
                    case "export_map":
                        return ArcMapTools.ExportMap(_ui, S("path"), I("width", 1600), I("height", 1200),
                            I("dpi", 150), S("target"));

                    // Geoprocessing
                    case "run_gp": return ArcMapTools.RunGp(_ui, S("tool"), StrArray(d, "params"));
                    case "gp_tools": return ArcMapTools.SearchGpTools(_ui, S("keyword"));

                    default: return "未知工具: " + name;
                }
            }
            catch (Exception ex)
            {
                return "工具执行异常: " + ex.Message;
            }
        }
    }
}
