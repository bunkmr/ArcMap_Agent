# ArcMap Agent — 构建与安装

> C# (.NET Add-In) 自然语言操控 ArcMap 的 AI 助手，UI 与功能对齐同系列的 **QGIS Agent** 插件。
> **已在本地 ArcGIS Desktop 10.4.1 实测通过（AddInTester 6/6 PASS）**：工具栏加载 / 停靠面板构建 /
> `/layers` / `/add` / **自然语言多轮 function calling 工具闭环** / **标注链路（set_labels）**；
> 另经 1:1 像素比对确认头部文字与对话区同样锐利（笔画实心率 91.4% vs 96.3%）、
> 且 `set_labels` 在真实 ArcMap 视图里确实画出带白色晕圈的标注。
> 证据见 `..\_test_result.txt`、`..\_harness_stdout.txt` 与 `..\_arcmap_test.png`。
> 目标 ArcMap 10.2+（见版本兼容说明）。

## 0. 界面结构（v0.4.3）

| 页签 | 说明 |
| --- | --- |
| **对话** | 一问一答气泡（用户右对齐 / Agent 左对齐）+ **Markdown 渲染**（标题、粗斜体、代码块、列表、表格、引用、链接）；工具调用显示为徽章，工具结果以等宽块展示。输入区支持多轮 function calling。 |
| **历史** | 会话以 JSON 存于 `%APPDATA%\ArcMapAgent\conversations\`，可保存 / 载入 / 删除。 |
| **模型** | 多模型配置表 + 厂商预设（OpenAI / DeepSeek / GLM / 通义 / 本地 vLLM / Ollama）+ 连接测试。 |
| **MCP** | 本地 127.0.0.1 HTTP + JSON-RPC 端点，把 ArcObjects 工具暴露给外部 Agent；端口 / 令牌 / 特权工具 / 连通性自检。 |
| **地图** | （ArcMap 特色）当前数据框与图层列表，直接执行缩放 / 显隐 / 移除 / 字段 / 统计 / 添加 shp / 打开 MXD。 |
| **工具** | ArcObjects 工具目录 + slash 命令速查。 |
| **帮助** | 功能与使用说明。 |

- 工具栏按钮与停靠窗口图标复用 QGIS Agent 的机器人图标（`Images\show16.png` / `agent32.png`），
  面板头部 Logo 用同一张 `Images\icon.png`（以嵌入资源方式打进 DLL）。
- 页签为自绘胶囊按钮（`TabHost`），窄停靠面板下自动换行；只有当前页的控件可见
  （自动化测试依赖这一点来定位聊天控件）。

### 按钮对齐（v0.4.3）

**`Control.Margin` 的默认值是 `(3,3,3,3)`，而 `FlowLayoutPanel` 是认 `Margin.Top` 摆放子控件的。**
同一排按钮里若有的用默认 Margin、有的显式写了 `Margin = new Padding(6,0,0,0)`，顶距就是 3 vs 0
→ 整排错开 3px；在 225% 缩放下实际是 **7px**（用户截图里肉眼一眼可见）。

改法：`FlatButton` 构造函数把默认 Margin 钉成 `(0, 0, S(6), 0)`（**top 恒为 0**），
并删掉 `HistoryPanel` / `MapPanel` / `McpPanel` / `ModelPanel` / `ToolsPanel` 里所有
`new Padding(6,0,0,0)` 式的覆盖。回归断言见 `_probe2`：不换行的 `FlowLayoutPanel` 内
所有 `Button` 的 `Top` 必须一致。

### 尺寸常量必须随 DPI 换算（v0.4.3）

`UiTheme.UI(px)` 会按 DPI 放大字体，而布局常量过去全是 96dpi 字面量（按钮高 26、标签高 18/24…）。
两者不匹配时字形会比容器高 → 上下被硬裁。新增 `UiTheme.S(px)`（长度换算）与
`UiTheme.TextRowHeight(font, extraPad)`（按字体行高算容器高），全项目尺寸常量统一走它们；
`FlatButton.SetBoundsCore` 再兜一道不变量：高度小于 `TextRowHeight` 时自动抬高。

> 注意：在 ArcMap 里 `DpiX` 报 96（进程 DPI 不感知），所以 `S()` 在真机上是恒等变换 ——
> 这条修正主要是为了在 DPI 感知的宿主里也成立，以及在探测程序（`SetProcessDPIAware` 后
> `DpiX=216`）里能被量化验证。

### 文字渲染（v0.4.0 起；v0.4.3 找到真因）

**真因（v0.4.3 实测确认）：文字只要先落进「离屏位图缓冲」，就一定发糊。**

本机给 `ArcMap.exe` 设了兼容标志 `~ GDIDPISCALING DPIUNAWARE`
（`HKCU\...\AppCompatFlags\Layers`）。ArcMap 因此是 **DPI 不感知**进程
（`shcore.GetProcessDpiAwareness` 实测 = 0 = `PROCESS_DPI_UNAWARE`），桌面在 225%：

- 窗口被整体放大 **2.25x** —— 非文字内容（圆角、边框、1px 线）都是位图放大，实测 1px 线变成 2px 宽。
- 但 Windows 会**拦截「直接画到窗口 DC 的 GDI 文字」并按真实 DPI 重新渲染** → 这类文字是清晰的。
- **先画进离屏位图再贴图的内容不吃这个拦截**，只能被位图放大 → 糊。

这解释了两个现象：
1. `TextBox` / `RichTextBox`（原生系统控件，自己直接画到窗口）一直锐利；
2. 用 `OptimizedDoubleBuffer` 自绘的文字一直发糊 —— 因为它先进了缓冲位图。

**实测数据**（`_blurtest2`：白字压 teal 底、同一字号，挂上同一兼容标志后抓真实屏幕 1:1 像素、
逐像素统计；`笔画实体度` = 近纯白像素 / 文字像素，越高越实）：

| 出字方式 | 笔画实体度 | 彩边占比 |
| --- | --- | --- |
| `Label`（GDI `TextRenderer`） | 0.13 | 0.32 |
| `Label`（GDI+ `DrawString`，即 `UseCompatibleTextRendering=true` 默认值） | 0.12 | 0.32 |
| 透明背景子 `Label`（**v0.4.2 `FlatButton` 的做法**） | 0.12 | 0.32 |
| `OptimizedDoubleBuffer` 的 `OnPaint` 里 `TextRenderer`（**改之前**） | 0.12 | 0.33 |
| **`base.WndProc` 之后画到窗口 DC**（**v0.4.3 改成了这个**） | **0.23** | **0.26** |
| 不双缓冲的 `OnPaint` 里 `TextRenderer` | 0.23 | 0.26 |
| 离屏位图 + 灰度抗锯齿后贴图 | 0.20 | 0.29 |

结论（三条都被实测否定/证实过，别再走回头路）：

1. **不是 GDI vs GDI+ 的问题**。上表前两行几乎一样 —— GDI+ 的 `DrawString` 走的也是 GDI 光栅器，
   同样吃得到系统的文字重绘。所以「把 `UseCompatibleTextRendering` 全改成 false」**没有收益**
   （已试过并回退）。顺带：`Application.SetCompatibleTextRenderingDefault(false)` 在插件里**调不了**
   —— 它必须在创建第一个窗口之前调用，ArcMap 启动时窗口早就有了，调用会抛
   `InvalidOperationException`（`_probe4` 实测）。
2. **也不是「透明 Label 回退 GDI+」的问题**。透明 Label 与普通 Label 的实测值完全一致。
3. **是「有没有先落进离屏位图」的问题**。所以 `FlatButton` 保留 `OptimizedDoubleBuffer`
   （自绘圆角在 hover / 缩放时不闪烁），但把标题挪到 `WndProc` 里、`base.WndProc` 画完底色之后
   再用 `Graphics.FromHwnd(Handle)` **补画到窗口 DC** 上；`Control.DrawToBitmap` 走的是
   `WM_PRINT`/`WM_PRINTCLIENT`（不经过 `WM_PAINT`），那条路径仍由 `OnPaint` 出字，保证抓图不缺字。

- 字体单位改用**像素**（`UiTheme.UI(px)` 按 DPI 取整）而非磅，避免 pt→px 的亚像素落点导致模糊。
- `CardPanel` / `HeaderBar` 只画背景，标题与说明用原生 `Label`（原生控件直接画到窗口，不受影响）。

**v0.4.1 追加的两条**：

1. **底色不要用渐变**。GDI+ 的 `LinearGradientBrush` 会在位图上做抖动（dither），文字压在有噪点的
   底上，笔画边缘就会显得毛糙。头部改为**纯色**。
2. **`AutoSize = true` 的 Label 会吃掉 SetBounds 的高度**，导致 `SetBounds(x, 0, w, Height)` 里的
   `Height` 失效、文字贴顶，与垂直居中的状态指示点错位。状态 Label 改为
   `AutoSize = false` + `AutoEllipsis = true`；同时头部只显示**短状态词**（完整信息在底部状态栏），
   避免长句把标题/副标题挤掉。

**v0.4.2 定稿：头部改为「浅底深字」（这才是「对话区锐利、头部发糊」的真因）**

先前的思路（深底白字 + 提高背景对比度）方向错了。对着截图逐像素比对后确认：

- **ClearType 的次像素抗锯齿是为「深字浅底」设计的** —— 它把文字笔画分摊到 R/G/B 三个子像素上。
  反过来「白字压深底」时：笔画边缘留下一圈红蓝彩边（color fringing），笔画又取不满对比度。
  同一台机器、同一份字体，于是「对话区（黑字白底）锐利、头部（白字青底）发糊」。
- 量化证据（同一张截图、同一台机器，统计字形像素的亮度偏离量）：

  | 区域 | 满对比笔画 | 弱对比笔画 | 笔画实心率 |
  | --- | --- | --- | --- |
  | v0.4.1 头部副标题（白字 / teal-800 底） | 636 | 509 | **55.5%** |
  | v0.4.2 头部副标题（teal-700 字 / teal-50 底） | 1655 | 156 | **91.4%** |
  | 对话区正文（slate-900 字 / 白底，基准） | 207 | 8 | **96.3%** |

- 做法：`HeaderBar` 底色改 `UiTheme.AccentSoft`（teal-50），标题 `slate-900`、
  副标题 `teal-700`、状态文字用语义色的 **700 档**（`UiTheme.SuccessDeep` 等，见该处注释）；
  底部保留 3px `UiTheme.Accent` 主色线作为品牌色与下边界。
- **状态语义色拆成 600/700 两档**：深底白字用 600 档，浅底小字号用 700 档
  （`SuccessDeep` / `WarningDeep` / `DangerDeep`）——小字号配 600 档同样取不满对比度。

**v0.4.2 追加的第 3 条：行距按字体度量算，不要写死 px**

`HeaderBar` 原先硬编码 `Height = 60`、标题 `SetBounds(left, 9, w, 22)`、副标题 `SetBounds(left, 32, w, 18)`。
一旦字体比预期高（`UiTheme.UI(px)` 会按 DPI 缩放），标题的降部（`g` / `p`）就**直接压到副标题字头上**，
看起来像两行字叠在一起。

改法：以 `Font.Height` 为准算行距与高度，`AutoScaleMode = None` 下布局在任何 DPI 都成立。

```csharp
_lineGap = Math.Max(2, _lblSub.Font.Height / 4);                    // 行距随字体缩放
Height   = _lblTitle.Font.Height + _lineGap + _lblSub.Font.Height + 2 * PadV;
// LayoutStatus() 内：
int blockH = titleH + _lineGap + subH;
int top    = Math.Max(1, (Height - blockH) / 2);
int logoSize = Math.Max(16, Math.Min(blockH, Height - 2 * PadV));   // 图标与文字块等高
_lblTitle.SetBounds(left, top, titleW, titleH);
_lblSub  .SetBounds(left, top + titleH + _lineGap, titleW, subH);
```

排版结果实测：头部共 54px，标题字形 y=141..157、副标题 y=164..177，中间**6 行干净空隙**。

另外 `AgentControl` 显式设置 `AutoScaleMode = AutoScaleMode.None`：`UserControl` 默认
`Inherit`，被 ArcMap 停靠容器按宿主字体做一次比例换算时，子控件会落到半像素上。

### 对话渲染（v0.4.0）
`ChatTranscript : RichTextBox` 负责气泡；`MarkdownRenderer` 负责把模型输出渲染成富文本。

- 流式时先以**纯文本 + `▍` 光标**追加（`BeginAssistant`/`AppendDelta`），收到结束时删除该区间，
  再交给 `MarkdownRenderer.Render()` **整体重排**（`EndAssistant`）——避免半截 Markdown 造成抖动。
- 逐段格式用 `SelectionFont` / `SelectionColor` / `SelectionBackColor` / `SelectionIndent` 施加，
  **不走 RTF**，规避 CJK 的 `\uNNNN` 转义问题。
- 表格按**中日韩全角宽度**（`DisplayWidth`）重新对齐；代码块等宽字体 + 浅色底。

## 1. 前提

- **ArcGIS Desktop 10.x** 已安装（提供 ArcObjects 运行时 + Add-In 框架）。
- **.NET Framework 4.8**。
- **Visual Studio 2019/2022/18** 或仅 `dotnet` CLI（本仓库用 `dotnet build` 验证）。
- **ESRI 引用来源**：本机 GAC 中的 ArcObjects PIA + `C:\Program Files (x86)\ArcGIS\Desktop10.4\bin\ESRI.ArcGIS.Desktop.AddIns.dll`。
  **无需安装 ArcObjects SDK 也能编译**。

## 2. 构建

方式 A（推荐，一键）：
```
build.bat
```
方式 B（手动）：
```
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File pack.ps1
```
> 若 PowerShell 的 `Compress-Archive` 在本机不可用，可用 Python 替代打包（产物完全一致）：
> ```
> python _pack.py
> ```
> 图标可重新生成（从 QGIS Agent 的 icon.png 复用并缩放）：
> ```
> python _gen_icon.py
> ```

产物：`ArcMapAgent.esriaddin`（= ZIP，官方布局：`Config.xml` + `Install/ArcMapAgent.dll` + `Images/`）。

### 实现要点
- **清单文件名为 `Config.xml`**（不是 .esriaddinx），根元素 `<ESRI.Configuration>`，`AddInID` 为 GUID，
  `class=` 用相对类名（相对 `<AddIn namespace>`）；工具条 Item 用 `<Button refID="..."/>`。
  ——对照本机 ArcGIS 自带 `ESRI.ArcGIS.MapCenter.esriaddin` 验证。
- **DLL 必须位于包内 `Install/` 子目录**，`Images/` 在包根。
- **`<Extension>` 必须显式写 `autoLoad="true"`**（v0.4.0 踩过的大坑）：
  ESRI schema 中 `autoLoad` 的说明是「Specifies if the extension should automatically load at
  start up or just-in-time. **The default is false, just-in-time.**」
  → 只写 `id`/`class` 时 `OnStartup()` **永远不会在 ArcMap 启动时被调用**。
  症状极像「加载项没加载」：**工具栏按钮正常实例化**，但扩展日志一行都没有、
  `AppContext.Application` 未注入、停靠面板不自动显示。
  （排查手法见下 `ext.diag`。）
- **安装位置**：`文档\ArcGIS\AddIns\Desktop10.4\{AddInID}\ArcMapAgent.esriaddin`（存放包文件本身，
  ArcMap 启动时自动解压到 `%LOCALAPPDATA%\ESRI\Desktop10.4\AssemblyCache\{AddInID}\`）。
- **同一 AddInID 只能有一份安装**。若历史上用过自定义加载目录（注册表
  `HKCU\Software\ESRI\Desktop10.4\Settings\AddInFolders`），请删掉那里的旧副本，否则会出现
  「插件时好时坏」的重复注册冲突。
- **Add-In 组件不用特性标注**：`ShowDockableWindowButton : ESRI.ArcGIS.Desktop.AddIns.Button`、
  `AgentDockableWindow : ESRI.ArcGIS.Desktop.AddIns.DockableWindow`，靠 `Config.xml` 的
  `class="XXX"` 关联，覆写 `OnClick()` / `OnCreateChild()`。
- **面板 UI 挂载**：`OnCreateChild()` 返回 WinForms `UserControl.Handle`（框架负责 SetParent）。
- **取 ArcMap 应用实例**：本版本没有静态 `ArcMap.Application`，改用基类 `Hook` 属性，写入
  静态 `AppContext.Application`，供 `ArcMapTools` 读取。
- **已验证 DLL 为 x86**，可在 32 位 ArcMap 进程内加载。
- **排查日志**（两个，各有分工）：
  - `%APPDATA%\ArcMapAgent\panel.log` —— 面板对话 / 工具输出（也是自动化测试的取证来源）。
  - `%APPDATA%\ArcMapAgent\ext.diag` —— **加载期诊断**：静态构造标记、`OnStartup` 各步、
    停靠窗口 ctor 与 `OnCreateChild`、UID 解析尝试结果。它**不依赖 `AppConfig` / `AgentLog`**，
    是判定「扩展到底有没有被实例化」的硬证据（`panel.log` 里只有按钮那一行、
    而 `ext.diag` 完全不存在 ⇒ 扩展类型根本没被触碰）。建议保留。
- **停靠窗口 UID 只在 ArcMap 进程内可靠**：扩展内部用
  `UID.Value = "ArcMapAgent_AgentDockableWindow"` 能正常取到窗口；
  但**外部进程**（经 AppROT 拿代理，如 `AddInTester`）对同一 UID 会抛
  「值不在预期的范围内」。因此回归测试不要用 `GetDockableWindow` 判定面板是否创建，
  应改用 UI Automation 能否找到 `AgentInput`/`AgentSend`/`AgentOutput`。

### Agent 工具调用闭环（v0.4.0 关键修正）
v0.3.0 里「Agent 调不动 ArcMap 工具」的原因在**插件侧**（端点本身正常，已用 `curl` 验证过
`finish_reason:"tool_calls"` 与 `tool_call_id` 回传）：

1. 只跑**一轮**：第一轮拿 `tool_calls` 执行完就结束，从不把工具结果回灌给模型，也不带
   `tools` 再问一次 → 模型永远看不到结果。
2. **丢失协议字段**：assistant 消息没带 `tool_calls`、tool 消息没带 `tool_call_id`，
   多轮上下文无法闭合。
3. **提示词怂恿编造**：SystemPrompt 是散文式「你可以……」，加上允许 `You:`/`Agent:` 角色扮演，
   模型倾向于**直接编一个答案**而不是调用工具。

修正：
- `AgentController.SendAsync` 改为 **多轮循环**（`MaxToolRounds = 8`）：每轮都带 `tools` +
  `tool_choice:"auto"`；把 assistant 消息（**含 `tool_calls`**）与每条 tool 消息（**含匹配的
  `tool_call_id`**）依次入库，继续下一轮，直到模型给出**不含工具调用**的最终回答。
- `LlmClient` 请求构建器按 OpenAI 规范序列化 `tool_calls` / `tool_call_id`；流式解析累积
  `delta.tool_calls`（按 `index` 合并 name/arguments，保留 `id`）。
- `SystemPrompt` 重写为**强约束**：必须调用工具获取事实、不得编造结果、不得做角色扮演。
- 工具集从 9 个扩到约 20 个（见下），并给每个工具标注 `IsWriteTool` 以便确认弹窗。

## 3. 安装到 ArcMap

1. 关闭 ArcMap。
2. 双击 `ArcMapAgent.esriaddin` → 安装（即复制到 `文档\ArcGIS\AddIns\Desktop10.4\{AddInID}\`）。
3. 启动 ArcMap → 菜单 **自定义(Customize) → 工具条(Toolbars)** → 勾选 **ArcMap Agent**。
4. 点击工具栏上的机器人图标，右侧出现「ArcMap Agent」面板（首次加载后面板通常会自动停靠显示）。

## 4. 配置 LLM

打开 **模型** 页：可从「厂商预设」一键填入，或「＋ 添加」自建。至少填写：
- **API 端点**：OpenAI 兼容端点，默认 `http://localhost:8000/v1`（本地 vLLM）。
- **模型名**：如 `Qwen2.5-7B-Instruct`（按 vLLM 实际加载的模型名填）。
- **API Key**：本地 vLLM 一般留空；云端 API 填密钥。
- 选中该行 → **设为当前**；点 **测试连接** 验证（会向 `/chat/completions` 发一条最小请求）。

配置写入 `%APPDATA%\ArcMapAgent\config.json`（旧版扁平配置会自动迁移为一个模型档）。

## 5. 使用

- 直接对话：输入框输入问题，回车或点 **发送**，模型流式输出；点 **停止** 可中断。
- **一问一答气泡 + Markdown**：用户消息右对齐（青底），Agent 回复左对齐并带「Agent · 时间」，
  内容按 Markdown 渲染（标题 / 粗斜体 / 代码块 / 列表 / 表格 / 引用 / 链接）。
- 写操作（添加图层 / 移除图层 / 重命名 / 改符号化 / 保存文档）默认会弹窗确认，可在对话页勾选
  「跳过确认」关闭。
- **slash 命令**（无需模型能力即可验证 ArcObjects 链路）：
  `/layers`、`/add <路径>`、`/fields <图层>`、`/count <图层>`、`/zoom <图层>`、`/remove <图层>`、
  `/show <图层>`、`/hide <图层>`、`/frames`、`/save [路径]`、`/help`，以及：
  `/info <图层>`、`/unique <图层> <字段>`、`/color <图层> <字段>`、`/stats <图层> <字段>`、
  `/query <图层> <条件>`、`/full`、`/refresh`、`/clear`；
  **v0.4.1 新增**：`/label <图层> <字段>`（显示标注）、`/unlabel <图层>`（关闭标注）、
  `/breaks <图层> <数值字段>`（分级着色）、`/where <图层> <条件>`（定义查询）、
  `/sel <图层> <条件>`（按属性选择）、`/selwhere <源> <目标>`（按位置选择）、
  `/extent`（视图范围）、`/bookmarks`（书签）、`/export <路径>`（导出地图）、
  `/gptools [关键词]`（查 ArcToolbox 工具）。
- **自然语言工具调用（多轮）**：端点支持 function calling 时，模型会自行决定调用哪些工具、
  看结果再决定下一步。工具集共 **37 个**：

  | 分类 | 工具 |
  | --- | --- |
  | 读取 | `list_layers`、`list_data_frames`、`layer_info`、`list_fields`、`feature_count`、`field_statistics`、`unique_values`、`query_features`、`get_labels`、`get_selected_features`、`get_map_extent`、`list_bookmarks` |
  | 符号化 | `set_unique_value_renderer`（类别着色）、`set_graduated_colors`（数值分级着色）、`set_simple_renderer`、`set_layer_transparency` |
  | **标注** | `set_labels`（= 图层属性 → Labels：表达式 / 字号 / 颜色 / 加粗 / 晕圈 / 范围）、`clear_labels` |
  | 查询与选择 | `select_by_attribute`、`select_by_location`（空间关系）、`clear_selection`、`zoom_to_selected` |
  | 过滤 | `set_definition_query`、`clear_definition_query` |
  | 图层编辑 | `add_shapefile`、`remove_layer`、`rename_layer`、`set_layer_visibility`、`move_layer`（叠放顺序） |
  | 视图 | `zoom_to_layer`、`zoom_to_full_extent`、`zoom_to_scale`、`set_map_extent`、`add_bookmark`、`goto_bookmark`、`refresh_view` |
  | 输出 | `export_map`（png/jpg/tif/pdf，数据框或页面布局）、`save_document` |
  | **Geoprocessing** | `run_gp`（任意 ArcToolbox 工具）、`gp_tools`（常用工具目录 + 参数说明） |

  带 `IsWriteTool` 标记的（着色 / 标注 / 选择 / 定义查询 / 移动 / 书签 / 导出 / `run_gp` 等）
  默认弹窗确认，可在对话页勾选「跳过确认」关闭。

### 标注（Labeling）—— v0.4.1 补齐的能力
用户反馈「Agent 说它没有显示标注的功能」。原因是标注**不是 ILayer 上的一个开关**，而要走一整套接口，
当时完全没对接：

```
IGeoFeatureLayer.DisplayAnnotation = true          // 总开关
ILabelEngineLayerProperties2 lelp = new LabelEngineLayerPropertiesClass();
lelp.Expression = "[字段名]";                       // 或 [名称] & "(" & [类型] & ")"
lelp.Symbol     = textSymbol;                       // TextSymbolClass：Size / Color / Font(含加粗)
lelp.OverposterLayerProperties = basic;             // BasicOverposterLayerPropertiesClass.FeatureType
                                                    // 决定点/线/面各自的放置策略
IAnnotateLayerProperties alp = (IAnnotateLayerProperties)lelp;
alp.FeatureLayer = featureLayer;                    // 必须指定，否则不生效
alp.LabelWhichFeatures = esriAllFeatures | esriVisibleFeatures | esriSelectedFeatures;
gfl.AnnotationProperties.Clear();                   // 先清再挂，避免重复叠加
gfl.AnnotationProperties.Add(alp);
```
- `ITextSymbol.Font` 是 COM 的 `IFontDisp`，.NET 里没有强类型可赋值对象 → 用
  `Type.GetTypeFromProgID("StdFont")` 在**运行期**造一个（反射设 Name/Size/Bold/Italic），
  避免为了设字体去引 `stdole` PIA；失败则静默跳过，保持默认字体。
- 晕圈（halo）：`ITextSymbol as IMask` + `MaskStyle = esriMSHalo` + 白色 `MaskSymbol`，
  同样用 `as` 兜底（不同符号类实现情况不一）。

### Geoprocessing —— 一次打通整个 ArcToolbox
`run_gp(tool, params[])` 用 `ESRI.ArcGIS.Geoprocessor.Geoprocessor.Execute(string, IVariantArray, ITrackCancel)`
把任意 ArcToolbox 工具交给 ArcMap 自己跑（`OverwriteOutput=true`、`AddOutputsToMap=true`，
并用 `MessageCount/GetMessage/GetSeverity` 回读执行消息）。

- 参数一律用**字符串数组按位置传**：GP 会自行转换（`"100 Meters"`、字段名、路径、`"SUM;MEAN"`）。
- **工具名必须带工具箱别名后缀**：`Buffer_analysis`、`Clip_analysis`、`CalculateField_management` …
  写错时返回的提示里会点明这一点。
- `gp_tools(keyword)` 内置一份 **90 条常用工具目录**（工具名 + 参数顺序说明），
  让模型不必猜参数顺序；这比动态 `ListTools` 更实用（后者会吐出几千个名字）。
- 这一步把「还有哪些 ArcMap 功能没对接」这个问题基本关掉：矢量分析（缓冲/裁剪/相交/联合/擦除/
  空间连接/近邻）、数据管理（投影/合并/溶解/字段增删改/字段计算/排序/去重）、转换（栅格⇄矢量、
  Excel/表互转、KML、CAD）、部分 3D/Spatial Analyst（等值线/坡度/晕渲/插值/分区统计）都能直接跑。

- **MCP 外部驱动**：在 **MCP** 页点「启动服务」，复制客户端配置粘贴到支持 HTTP(streamable) 的
  MCP 客户端；`tools/call` 的 `name` 与上表工具名一致。特权工具（添加/移除图层、保存文档）
  默认不暴露，需在 MCP 页显式允许。

## 6. 自动化验证（AddInTester）

`..\AddInTester\`：x86 测试程序，流程 = 启动 ArcMap → 等待完全就绪（轮询 AppROT 直到
支持 `ICommandBars`）→ UIA 切到「对话」页 → 依次驱动面板：

| 步骤 | 检查项 | 判定 |
| --- | --- | --- |
| [5] | 停靠窗口 UID 解析 | 仅作参考（外部进程**取不到**，见上文「UID 只在进程内可靠」） |
| [6] | 面板 UI 已构建 | UIA 能找到 `AgentInput`/`AgentSend`/`AgentOutput` → `可停靠窗口` PASS |
| [6a] | `/layers` 工具链路 | `panel.log` 出现 `>> 列出图层` |
| [6b] | `/add` 工具链路 | 出现 `>> 添加图层` 且 `已添加图层`，图层数 +1 |
| [6c] | **自然语言多轮工具调用** | 出现 ≥1 次 `[调用工具]` 与 ≥1 次 `[工具结果]`，且无 `[错误]` |
| [6d] | **标注链路（`set_labels`）** | 出现 `[调用工具] set_labels`，且结果含 `已为图层` + `标注`，无 `[错误]` |

- [6c] 发送「列出当前地图的所有图层，并统计 continent 图层的要素数量」，最多等 120s；
  这是对「Agent 能否真正调用 ArcMap 工具」的**直接验证**。实测输出示例：两轮工具调用
  `list_layers` → `feature_count {"layer":"continent"}` → 最终以 Markdown 表格作答。
- [6d] 先用 `/fields continent` 取真实字段名，再用自然语言让模型去标注 —— 覆盖
  「模型声称没有标注功能」这个历史问题。**取不到字段名时会降级为不带字段的指令**
  （考察模型能否自己挑字段），保证这一步永远不会被静默跳过。
- 全程只做只读操作（[6b] 会加一个示例 shp，仅在本测试启动的临时会话里），
  结束后截图并 WM_CLOSE 优雅关闭自己启动的 ArcMap。
- 运行：`dotnet build -c Release` 后执行 `bin\Release\net48\AddInTester.exe`，结果写 `..\_test_result.txt`
  （另在 stdout 输出，`..\_harness_stdout.txt` 为最近一次运行记录）。

### 解析 `panel.log` 前必须还原 `" | "` 换行（踩过一次，代价是假 FAIL）

`AgentLog.Append` 落盘时会把 `\r\n` / `\n` 统一替换成 `" | "`，好让**一条日志占一行**。
所以任何「按行解析 `panel.log` 输出」的测试代码都必须先把 `" | "` 还原成 `\n`：

```csharp
static string[] PanelLines(string s)
    => (s ?? "").Replace(" | ", "\n").Split('\n');
```

不还原的后果：`ParseFirstFieldName` 只拿到第一条子行（`FID`），判定它是 OID 后 `continue`，
循环随即结束 → 返回 `null` → 整个 [6d] 标注测试被**静默跳过**，汇总里显示
`标注链路(set_labels): FAIL`，看起来像「插件没有标注功能」，实际是**测试脚本自己的 bug**。
（顺带把 `Indent()` 也走 `PanelLines`，控制台日志的多行结果才显示得出来。）

> **教训**：看到某项 FAIL 时，先确认「是功能坏了」还是「测试没跑到」——`Log` 里
> 打一行「将要执行的指令 / 选中的参数」就能立刻区分。

> 另外，`[6b]` 之后若面板必须刷新状态，`runCmd` 可能重试若干次才点中输入框；
> 每次重试都会重新读 UIA 边界并重新点击，属正常现象。

### 驱动方式：真实鼠标 + 键盘（**不要**用 UIA 设值）
- 高 DPI（本机 216 DPI / 225%）下，ArcMap 是 **DPI-UNWARE** 进程。跨进程 MSAA→UIA 桥
  对它子窗口会走「虚拟化代理」，表现为：
  - `BoundingRectangle` 被放大（报的坐标仍是**物理像素**，与实际位置的比例等于缩放比）；
  - `IsEnabled` / `IsKeyboardFocusable` **一律报 False**（误报！界面其实完全可用）；
  - `ValuePattern.SetValue` 会抛 `ElementNotEnabledException`（「对于未启用的元素…」）。
- 因此测试改为**用 `SendInput` 模拟真实用户**：`SetCursorPos` + 左键点击输入框坐标 →
  `Ctrl+A`/`Delete` 清空 → 按 Unicode 逐字符敲入命令（中文也能送）→ `Enter` 触发发送。
  这条路不依赖无障碍状态，而且顺带证明了「面板对真实用户是可交互的」。
- **重试策略**：面板刚显示时会重排一次，第一次读到的坐标可能已过期 → 每次重试都重读边界；
  因为发送成功后 `panel.log` 里会**立即**出现 `You: …`，所以「日志没增长」即可安全判定
  「没点中」，重试不会重复发送。

> 注意：测试会驱动「本进程启动的那个 ArcMap」，会移动鼠标并模拟按键，
> 运行期间请不要手工操作键盘鼠标。需要远端模型可连通；若模型不可用，
> [6c] 会 FAIL 而其余项仍应 PASS。


## 7. 版本兼容说明

- 本工程以 **ArcGIS Desktop 10.4 的引用**编译（GAC 中 PIA 版本 10.4.0.0）。
- 要在 **10.2 / 10.3** 运行，需安装对应版本的 ArcObjects SDK 并重新编译。
- 要在 **10.5–10.8** 运行，10.4 编出的包一般可直接用；如遇加载失败，按目标版本 SDK 重编。

## 8. 已知限制

- ArcObjects 工具集约 20 个，尚未覆盖 QGIS Agent 的全部工具；符号化仅至「唯一值 / 简单符号」层级。
- MCP 端点实现的是 HTTP + JSON-RPC 最小子集（initialize / tools/list / tools/call / ping），
  仅 stdio 的 MCP 客户端需自备 HTTP 转发。
- 流式 function calling 解析依赖标准 OpenAI 格式；非标准端点可能只走普通对话。
- 多轮循环上限 `MaxToolRounds = 8`，超限会要求模型直接作答。
- Markdown 渲染为**自实现子集**（不依赖第三方库），暂不支持图片、嵌套列表与行内 HTML。
- 每次改代码后必须：`dotnet build -c Release` → `_pack.py` → 覆盖部署包 →
  **清空 `%LOCALAPPDATA%\ESRI\Desktop10.4\AssemblyCache\{AddInID}\` 与 `%APPDATA%\ArcMapAgent\panel.log`**
  → 再启动 ArcMap，否则会用到旧解包 DLL（曾因此误判「修复没生效」）。
