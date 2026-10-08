# ArcMap Agent 项目全貌

> 一份写给「半年后的自己」的项目现状说明。
> 覆盖：缘起 / 技术选型 / 能力清单 / 架构 / 验证证据 / 关键攻坚 / 部署流程 / 待办。
> 最后更新：2026-10-01（v0.4.3）

---

## 1. 一句话

在 **ArcGIS Desktop 10.4.1（ArcMap）** 里塞一个 **C# 写的 AI 助手插件**：用中文说需求，它自己决定调哪些 ArcObjects 工具去干活 ——
加图层、改符号化、打标注、跑 ArcToolbox 空间分析、导出地图。UI 与能力对齐同系列的 **QGIS Agent**。

- 形态：`.esriaddin` 包 + 停靠面板（DockableWindow）+ 工具栏按钮。
- 规模：约 **7,800 行 C#**（28 个源文件），**37 个内置工具**，**90 条常用 GP 工具目录**。
- 状态：**本地 ArcGIS 10.4.1 实测 6/6 PASS**，可日常使用；MCP 出口已通。

---

## 2. 缘起：为什么要做 ArcMap 版

起因是两句话：

1. 我自己的 QGIS Agent 用顺了，想把它搬到 ArcMap 上；
2. 更重要的 —— **ArcMap 还在国内大量单位的生产环境里跑。**

ArcGIS Desktop 10.x 早已停止功能更新，但因为历史数据、国土/规划行业的既有流程、各种定制工具链都绑在它上面，
**短期换不掉**。这批用户的处境挺尴尬：一边是被时代淘汰的 32 位桌面软件，一边是最新的大模型能力，中间没有桥。

现有做法（早期项目 `../arcmap/TiandituTools`，Python Add-In）我实际用过，卡顿感明显。根因不是代码写得差，是平台限制：

| 限制 | 后果 |
| --- | --- |
| Tkinter 不能进 ArcMap 进程（会 CRT abort） | 所有界面被迫丢到**独立子进程**，靠 JSON 来回传 → 多一层进程 + 序列化开销 |
| comtypes **晚绑定** COM | 每次 ArcObjects 调用都有反射开销，批量要素操作慢 |
| Python 2.7 + GIL | 没有原生 async，LLM 流式只能自己管 |

所以这次换了路线：**用 C# 重写，但只在必要的地方重写。**

---

## 3. 技术选型：为什么是 C#（.NET Add-In）

ArcGIS Desktop 的 Add-In 框架从 10.0 起就**同时支持** Python Add-In 和 .NET Add-In，C# 是一等公民，不是歪门邪道。

| 维度 | Python Add-In（旧） | C# / .NET Add-In（现在） |
| --- | --- | --- |
| 解释器 | Python 2.7 32 位，子进程冷启数百 ms~1s | 编译 IL + JIT，DLL 随 ArcMap 直接加载 |
| UI | Tkinter **必须丢子进程** | 原生 WinForms **DockableWindow**，与 ArcMap 同一 UI 线程 |
| ArcObjects | comtypes 晚绑定，反射开销大 | 直接引用 `ESRI.ArcGIS.*` **早绑定**互操作，调用近乎原生 |
| HTTP / LLM | `urllib2` 同步，流式自管 | `HttpClient` + **async/await**，原生流式，UI 不冻结 |
| 多线程 | GIL | 真并行 `Task`；STA + `Invoke` 回主线程 |
| 部署 | `.esriaddin`(ZIP) + py 脚本 | `.esriaddin`(ZIP) + DLL |

**要诚实的地方**：C# 不能让 AI「答得更快」—— LLM 的网络往返（几百 ms 到数秒）跟语言无关。
C# 解决的是**插件自身跟手**：界面不卡、工具调用即时、流式逐字显示、绘图不闪烁。
如果你的「慢」主要来自模型本身或网络，换语言治标不治本，得靠本地 vLLM / 模型选型 / 流式输出缓解。

**硬约束**（踩过才知道）：

- ArcMap 是 **32 位进程** → 插件 DLL 必须编译为 **x86**（不能 AnyCPU），否则加载失败。
- 宿主是 **.NET Framework**（本工程锁 4.8），不能用 .NET Core/5+ 专属 API。
- **STA**：UI 在主线程；网络放后台 `Task`；任何碰 ArcObjects 的操作必须 `Invoke` 回主线程。

---

## 4. 当前能力清单

### 4.1 界面：7 个页签

| 页签 | 做什么 |
| --- | --- |
| **对话** | 一问一答气泡（用户右对齐 / Agent 左对齐）+ Markdown 渲染；工具调用显示为徽章，工具结果等宽块展示；支持多轮 function calling |
| **历史** | 会话以 JSON 存于 `%APPDATA%\ArcMapAgent\conversations\`，可保存 / 载入 / 删除 |
| **模型** | 多模型配置表 + 厂商预设（OpenAI / DeepSeek / GLM / 通义 / 本地 vLLM / Ollama）+ 连接测试 |
| **MCP** | 本地 127.0.0.1 HTTP + JSON-RPC 端点，把 ArcObjects 工具暴露给外部 Agent；端口 / 令牌 / 特权工具 / 自检 |
| **地图** | （ArcMap 特色）当前数据框与图层列表，直接缩放 / 显隐 / 移除 / 看字段 / 统计 / 加 shp / 开 MXD |
| **工具** | ArcObjects 工具目录 + slash 命令速查 |
| **帮助** | 功能与使用说明 |

### 4.2 工具集：37 个

| 分类 | 工具 |
| --- | --- |
| 读取 | `list_layers`、`list_data_frames`、`layer_info`、`list_fields`、`feature_count`、`field_statistics`、`unique_values`、`query_features`、`get_labels`、`get_selected_features`、`get_map_extent`、`list_bookmarks` |
| 符号化 | `set_unique_value_renderer`、`set_graduated_colors`、`set_simple_renderer`、`set_layer_transparency` |
| 标注 | `set_labels`、`clear_labels` |
| 查询与选择 | `select_by_attribute`、`select_by_location`、`clear_selection`、`zoom_to_selected` |
| 过滤 | `set_definition_query`、`clear_definition_query` |
| 图层编辑 | `add_shapefile`、`remove_layer`、`rename_layer`、`set_layer_visibility`、`move_layer` |
| 视图 | `zoom_to_layer`、`zoom_to_full_extent`、`zoom_to_scale`、`set_map_extent`、`add_bookmark`、`goto_bookmark`、`refresh_view` |
| 输出 | `export_map`、`save_document` |
| **Geoprocessing** | `run_gp`（任意 ArcToolbox 工具）、`gp_tools`（90 条常用工具目录 + 参数说明） |

带 `IsWriteTool` 标记的（着色 / 标注 / 选择 / 定义查询 / 移动 / 书签 / 导出 / `run_gp`）默认**弹窗确认**，可在对话页勾选「跳过确认」关闭。

### 4.3 一条关键能力：`run_gp` 把整个 ArcToolbox 打通了

`run_gp(tool, params[])` 调 `Geoprocessor.Execute()`，把任意 ArcToolbox 工具交给 ArcMap 自己跑
（`OverwriteOutput=true`、`AddOutputsToMap=true`，并用 `MessageCount/GetMessage/GetSeverity` 回读执行消息）。

这一步基本关掉了「还有哪些 ArcMap 功能没对接」这个问题：

- 矢量分析：缓冲 / 裁剪 / 相交 / 联合 / 擦除 / 空间连接 / 近邻
- 数据管理：投影 / 合并 / 溶解 / 字段增删改 / 字段计算 / 排序 / 去重
- 转换：栅格⇄矢量、Excel/表互转、KML、CAD
- 部分 3D / Spatial Analyst：等值线 / 坡度 / 晕渲 / 插值 / 分区统计

两个必须记住的细节：

1. **工具名必须带工具箱别名后缀**：`Buffer_analysis`、`Clip_analysis`、`CalculateField_management`。
2. 参数一律**字符串数组按位置传**，GP 会自行转换（`"100 Meters"`、字段名、路径、`"SUM;MEAN"`）。

`gp_tools(keyword)` 内置一份 90 条常用目录，让模型不必猜参数顺序 —— 比动态 `ListTools` 实用得多（后者会吐几千个名字）。

### 4.4 MCP 出口

MCP 页点「启动服务」，把客户端配置粘到支持 HTTP(streamable) 的 MCP 客户端，外部 Agent（Claude Desktop / Cursor）就能驱动 ArcMap。
`tools/call` 的 `name` 与工具名一致。服务只监听 `127.0.0.1` + 令牌校验；**特权工具**（添加/移除图层、保存文档）默认不暴露。

---

## 5. 架构

```
ArcMap 主线程 (STA)
  DockableWindow (WinForms UserControl)
    └ 聊天面板 / 输入框 / 流式输出 / 7 页签
         │ 用户消息
         ▼
  后台 Task (线程池)
    AgentController.SendAsync  ← 多轮循环，MaxToolRounds = 8
      ├─ 带 tools + tool_choice:"auto" 调 LLM（HttpClient async 流式）
      ├─ 解析 delta.tool_calls（按 index 合并）
      ├─ 回灌 assistant(含 tool_calls) + tool(含 tool_call_id)
      └─ 直到模型给出不含工具调用的最终回答
         │ 决定调用工具
         ▼  Invoke 回主线程 (STA)
   ArcMapTools / ArcMapTools.Extended (ArcObjects)
      IMap / ILayer / IFeatureLayer / IGeoFeatureLayer
      IGeoProcessor (GP 工具) / IActiveView / IMapDocument
```

- **Agent 循环**：`MaxToolRounds = 8`，超限会要求模型直接作答。
- **线程安全**：网络/LLM 在工作线程；碰 ArcObjects 一律 `Invoke` 回主线程。
- **配置**：`%APPDATA%\ArcMapAgent\config.json`（多模型档 + ActiveName + Temperature + SkipConfirm + MCP 设置），旧扁平配置自动迁移。

---

## 6. 验证证据（不是「应该能用」）

自动化回归工具 `..\AddInTester\`：x86 测试程序，流程 = 启动 ArcMap → 等完全就绪（轮询 AppROT 直到支持 `ICommandBars`）
→ UIA 切到「对话」页 → 用 **SendInput 模拟真实鼠标键盘**驱动面板。

| 步骤 | 检查项 | 结果 |
| --- | --- | --- |
| [5] | 停靠窗口 UID 解析 | 仅作参考（外部进程取不到，见 §7.3） |
| [6] | 面板 UI 已构建 | **PASS** —— `可停靠窗口` |
| [6a] | `/layers` 工具链路 | **PASS** —— `panel.log` 出现 `>> 列出图层` |
| [6b] | `/add` 工具链路 | **PASS** —— `已添加图层: continent（要素数 8）`，图层数 1→2 |
| [6c] | **自然语言多轮工具调用** | **PASS** —— ≥1 次 `[调用工具]` 与 `[工具结果]`，无 `[错误]` |
| [6d] | **标注链路（`set_labels`）** | **PASS** —— 结果含 `已为图层` + `标注` |

[6c] 实测对话原文（证明多轮 function calling + Markdown 渲染都通了）：

```
You: 列出当前地图的所有图层，并统计 continent 图层的要素数量
[调用工具] list_layers  {}
[工具结果] 当前数据框【图层】共有 1 个图层： 1. continent [FeatureLayerClass]
[调用工具] feature_count  {"layer":"continent"}
[工具结果] 图层【continent】共有 8 个要素。
Agent: ## 当前地图图层信息 … - **continent 图层**：共有 **8 个要素**
```

另经 **1:1 像素比对**确认头部文字与对话区同样锐利（笔画实心率 91.4% vs 96.3%），
且 `set_labels` 在真实 ArcMap 视图里确实画出带白色晕圈的标注。

证据文件（本机产物，未入库）：`_test_result.txt`、`_harness_stdout.txt`、`_arcmap_test.png`、
`_render_chat_zoom.png`、`_layout_dump.txt`、`_probe2_tab*.png`。

---

## 7. 关键攻坚（都是真踩过的坑）

### 7.1 文字发糊：追到注册表里一个兼容标志

**用户现象**：用了样式的控件（Label / 按钮）字发糊，**但 TextBox 里的字是清楚的**。

这个「一半清楚一半糊」的对照是破案关键。逐层排查：

1. 怀疑 GDI vs GDI+ → 写复现台实测，**两者笔画实体度 0.13 / 0.12，基本无差别**，否定。
   （顺带发现 `Application.SetCompatibleTextRenderingDefault(false)` 在插件里**调不了** ——
   必须在创建第一个窗口之前调用，ArcMap 启动时窗口早就有了，调用抛 `InvalidOperationException`。）
2. 怀疑「透明背景 Label 回退 GDI+」→ 透明 Label 与普通 Label 实测**完全一致**，否定。
3. 查进程 DPI 感知：`shcore.GetProcessDpiAwareness` 实测 = **0 = `PROCESS_DPI_UNAWARE`**，
   而 exe 清单里明明写了 `<dpiAware>true</dpiAware>`。
4. 查注册表 `HKCU\...\AppCompatFlags\Layers`，找到真凶：
   ```
   C:\Program Files (x86)\ArcGIS\Desktop10.4\bin\ArcMap.exe = ~ GDIDPISCALING DPIUNAWARE
   ```
   它的语义是：**整个窗口按位图拉伸（2.25×），但拦截「直接画到窗口 DC 的 GDI 文字」并按真实 DPI 重绘。**

**于是结论只有一个**：

> **文字只要先落进「离屏位图缓冲」，就一定发糊。**

这解释了两个现象：`TextBox` / `RichTextBox` 是原生控件、自己直接画窗口，所以一直锐利；
开了 `OptimizedDoubleBuffer` 自绘的文字，先落进缓冲位图，所以糊。

**实测数据**（白字压 teal 底、同字号、挂同一兼容标志抓真实屏幕 1:1 像素；
`笔画实体度` = 近纯白像素 / 文字像素，越高越实）：

| 出字方式 | 笔画实体度 | 彩边占比 |
| --- | --- | --- |
| `Label`（GDI `TextRenderer`） | 0.13 | 0.32 |
| `Label`（GDI+ `DrawString`，默认值） | 0.12 | 0.32 |
| 透明背景子 `Label`（v0.4.2 的做法） | 0.12 | 0.32 |
| `OptimizedDoubleBuffer` 的 `OnPaint` 里出字（**改前**） | 0.12 | 0.33 |
| **`base.WndProc` 之后画到窗口 DC**（**v0.4.3 改成了这个**） | **0.23** | **0.26** |
| 不双缓冲的 `OnPaint` 里出字 | 0.23 | 0.26 |

**改法**：`FlatButton` 保留 `OptimizedDoubleBuffer`（自绘圆角在 hover / 缩放时不闪烁），
但把标题挪到 `WndProc` 里、`base.WndProc` 画完底色之后，用 `Graphics.FromHwnd(Handle)` **补画到窗口 DC**。
另外 `Control.DrawToBitmap` 走的是 `WM_PRINT`/`WM_PRINTCLIENT`（**不经过 `WM_PAINT`**），
所以 `OnPaint` 里保留 `if (_printing) DrawCaption(...)`，否则抓图工具里按钮是空的。

**沉淀下来的复现台**：`_blurtest2/` + `_blurcap2.py` —— 给自己的 exe 挂上同一兼容标志、
一行一种出字方式、抓屏打分 + 8× 放大。以后改渲染直接用。

### 7.2 按钮不水平对齐：`Control.Margin` 的默认值

用户反馈「按钮没有水平对齐，不只这一个页签」。

根因很朴素：**`FlowLayoutPanel` 是按子控件的 `Margin.Top` 摆放的，而 `Control.Margin` 默认是 `(3,3,3,3)`。**
同一排里有的用默认、有的手写了 `Margin = new Padding(6,0,0,0)` → 顶距 3 vs 0 → 错开 3px；
桌面在 225%，**实际错开 7px**（用户截图里量出来：`删除` 填充块顶边 y=1184、`载入` y=1190）。

**改法**：`FlatButton` 构造函数把默认 Margin 钉成 `(0, 0, S(6), 0)`（**top 恒为 0**），
删掉 `HistoryPanel` / `MapPanel` / `McpPanel` / `ModelPanel` / `ToolsPanel` 里所有 `(6,0,0,0)` 式覆盖。

**回归断言**（`_probe2`）：不换行的 `FlowLayoutPanel` 内所有 `Button` 的 `Top` 必须一致。
结果：**42 排按钮全部 Top 一致，错位行数 = 0**。

### 7.3 尺寸常量必须随 DPI 换算

`UiTheme.UI(px)` 会按 DPI 放大字体，而布局常量过去全是 96dpi 字面量（按钮高 26、标签高 18/24…）。
两者不匹配时字形比容器高 → **上下被硬裁**，看起来也像「糊」。

新增两个工具并全项目统一走它们：

```csharp
public static int S(int px) => (int)Math.Round(px * _scale);          // 长度换算
public static int TextRowHeight(Font f, int extraPad = 4);            // 按字体行高算容器高
```

`FlatButton.SetBoundsCore` 再兜一道不变量：高度小于 `TextRowHeight` 时自动抬高。

> 注意：在 ArcMap 里 `DpiX` 报 96（进程 DPI 不感知），所以 `S()` 在真机上是**恒等变换**。
> 这条修正主要是为了在 DPI 感知的宿主里也成立，以及在探针程序（`SetProcessDPIAware` 后 `DpiX=216`）里能被量化验证。

### 7.4 `<Extension>` 必须显式写 `autoLoad="true"`

**症状极像「加载项没加载」**：工具栏按钮正常实例化，但扩展日志一行都没有、
`AppContext.Application` 未注入、面板不自动显示。

**根因**：ESRI schema 对 `Extension` 的 `autoLoad` 说明是
「Specifies if the extension should automatically load at start up or just-in-time. **The default is false, just-in-time.**」
→ 只写 `id`/`class` 时，扩展**永远不会在启动时被实例化**。

**修法**：`<Extension id="..." class="..." autoLoad="true" />`。

**教训**：Add-In 里「按钮可见」≠「加载项完整注册」。为此加了独立诊断文件
`%APPDATA%\ArcMapAgent\ext.diag`（不依赖 `AppConfig`/`AgentLog`），才能断定「类型根本没被触碰」。**建议长期保留。**

### 7.5 Agent 调不动工具：插件侧协议不完整

v0.3.0 时「Agent 调不动 ArcMap 工具」，一度以为是端点问题 —— 用 `curl` 验证过
`finish_reason:"tool_calls"` 与 `tool_call_id` 回传都正常。问题在插件自己：

1. **只跑一轮**：拿到 `tool_calls` 执行完就结束，从不把结果回灌给模型，也不带 `tools` 再问一次。
2. **丢失协议字段**：assistant 消息没带 `tool_calls`、tool 消息没带 `tool_call_id`，多轮上下文无法闭合。
3. **提示词怂恿编造**：SystemPrompt 是散文式「你可以……」，模型倾向**直接编个答案**而不是调工具。

**修法**：`SendAsync` 改多轮循环；`LlmClient` 按 OpenAI 规范序列化 `tool_calls`/`tool_call_id`；
SystemPrompt 改为**强约束**（必须调工具取事实、不得编造、不得角色扮演）；工具集 9 → 37 个。

### 7.6 回归测试的两个反直觉坑

- **必须声明 DPI 感知**：`AddInTester` 原先未声明 → 跨进程 MSAA→UIA 桥返回的元素
  `BoundingRectangle` 被放大 2.25 倍、`IsEnabled`/`IsKeyboardFocusable` **一律误报 False**。
  曾因此误判「发送按钮被禁用」，导致三项测试全部跳过。修法：`Main` 最早期调 `SetProcessDpiAwareness(1)`。
- **不能靠 UIA 设值**：高 DPI 下 `ValuePattern.SetValue` 抛 `ElementNotEnabledException`（误报），
  改用 **SendInput 模拟真实用户**（点输入框 → Ctrl+A/Delete → 逐字符敲入 → Enter）。
  好处是不依赖无障碍状态，且顺带证明「面板对真实用户可交互」。

另一个自己挖的坑：`AgentLog.Append` 落盘时把 `\n` 替换成 `" | "`（让一条日志占一行），
测试脚本若**不把 `" | "` 还原成 `\n`** 就按行解析，会导致标注测试被**静默跳过**、看起来像「插件没有标注功能」。
现在 `PanelLines()` 统一还原。

---

## 8. 部署流程（每次改代码必须全走）

```bash
cd ArcMapAgent
dotnet build -c Release          # 0 警告 0 错误
python _pack.py                  # → ArcMapAgent.esriaddin
```

然后：

1. 复制 `.esriaddin` 到 `文档\ArcGIS\AddIns\Desktop10.4\{AddInID}\`
2. **清理旧解包 DLL**：`%LOCALAPPDATA%\ESRI\Desktop10.4\AssemblyCache\{AddInID}\`
   （被运行中的 ArcMap 占用时用**改名**代替删除）
3. 清空 `%APPDATA%\ArcMapAgent\panel.log` 与 `ext.diag`
4. 重启 ArcMap

> **不清缓存会用到旧 DLL** —— 曾因此误判「修复没生效」。

其它必须记住的：

- 同一 **AddInID 只能有一份安装**。若历史上用过自定义加载目录（注册表
  `HKCU\Software\ESRI\Desktop10.4\Settings\AddInFolders`），旧副本会造成「插件时好时坏」。
- 面板 UI 挂载靠覆写 `OnCreateChild()` 返回 `UserControl.Handle`；
  取 ArcMap 应用实例用基类 `Hook` 属性（10.4 没有静态 `ArcMap.Application`），写入静态 `AppContext.Application`。
- 停靠窗口 UID **只在 ArcMap 进程内可靠**；外部进程（经 AppROT）对同一 UID 会抛
  「值不在预期的范围内」。所以回归测试不要用 `GetDockableWindow` 判定面板是否创建，改用 UIA 找
  `AgentInput`/`AgentSend`/`AgentOutput`。

---

## 9. 界面视觉规范（v0.4.3 定稿）

- 配色：**teal 主色**（与图标同源），浅底深字。
- **头部底色不要用渐变**：GDI+ 的 `LinearGradientBrush` 会在位图上做 dither，
  文字压在有噪点的底上，笔画边缘就会显得毛糙。头部改**纯色**。
- **头部「浅底深字」而非「深底白字」**：ClearType 的次像素抗锯齿是为深字浅底设计的。
  反过来白字压深底时，笔画边缘留一圈红蓝彩边、笔画又取不满对比度。
  量化证据（同机同字体）：

  | 区域 | 笔画实心率 |
  | --- | --- |
  | v0.4.1 头部副标题（白字 / teal-800 底） | 55.5% |
  | v0.4.2 头部副标题（teal-700 字 / teal-50 底） | **91.4%** |
  | 对话区正文（slate-900 字 / 白底，基准） | 96.3% |

- **状态语义色拆成 600/700 两档**：深底白字用 600 档，浅底小字号用 700 档
  （`SuccessDeep`/`WarningDeep`/`DangerDeep`）—— 小字号配 600 档同样取不满对比度。
- 行距按 `Font.Height` 算，不写死 px；`AutoScaleMode = AutoScaleMode.None`。

---

## 10. 现状与待办

**已完成**

- v0.4.3：按钮对齐 + 文字渲染真因修复 + 全项目尺寸随 DPI 换算。
- 37 个工具、7 页签、MCP 出口、标注链路、GP 打通。
- 回归 6/6 PASS，探针三条断言全绿（错位 0 / 裁字 0 / 透明背景 0）。

**待办 / 下一步**

| 项 | 说明 |
| --- | --- |
| 重启 ArcMap 验收 | 用户侧确认按钮对齐与文字锐利（需重启加载新 DLL） |
| 全量回归 | 用户方便时跑一次 `AddInTester`，确认无回归 |
| 模型能力扩展 | 模型节点只能是 **GP 工具**；插件里 ArcObjects 直接操作（加图层/渲染/出图）**不是 GP 工具，塞不进模型**。若要「把流程固化进 ModelBuilder 模型」，需换成 GP 等价物或包成脚本工具 |
| ModelBuilder 编程 | API 层面可行（`Md*` 接口族：`IGPModelTool.Model` 可写、`CreateTool(esriGPModelTool)`、`IMdModel.AddElement`、`IGPTool.Store()`），但**只能在 ArcMap 进程内做**（独立进程 `E_NOINTERFACE` 实测） |
| 工具集补全 | 尚未覆盖 QGIS Agent 全部工具；符号化仅至「唯一值 / 简单符号」 |

---

## 11. 相关文档索引

| 文件 | 内容 |
| --- | --- |
| `README.md` | GitHub 门户文档：截图 / 选型 / 能力 / 架构 / 构建 / 已知限制（面向外部读者） |
| `FEASIBILITY.md` | 最初的可行性分析：技术选型、架构映射、风险、分阶段计划 |
| `ArcMapAgent/README_build.md` | 构建与安装手册：清单格式、部署、自动化验证、已知限制（**最全**） |
| `PROJECT_OVERVIEW.md` | 本文 |

> **以下为开发期的本机产物** —— 探针、复现台、转储、截图与逐日工作日志。
> 2026-10-08 起它们已从仓库移除（规则见 `.gitignore`），只存在于开发机上；
> 要重跑实验得先找回这些文件。

| 文件 | 内容 |
| --- | --- |
| `.workbuddy/memory/2026-09-29.md` | v0.3.0 / v0.4.0 的实现记录与踩坑 |
| `.workbuddy/memory/2026-09-30.md` | v0.4.3 的文字渲染真因 + ModelBuilder 取证 |
| `_api_dump_gp.txt` | `ESRI.ArcGIS.Geoprocessing` 全量接口转储（158 KB） |
| `_layout_dump.txt` | 控件树 + 三条断言输出（回归证据） |
| `_blurtest2/` + `_blurcap2.py` | 文字发糊复现台（可复用） |
| `_probe2/` | 布局探针（控件事 + 断言 + 逐页截图） |
| `_probe5/` | GP/模型 API 反射转储器 |
| `_probe6/` | ModelBuilder 编程 PoC |
