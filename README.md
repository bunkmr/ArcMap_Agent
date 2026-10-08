# ArcMap Agent

> 用中文对 ArcMap 说一句话，AI 自己去调 ArcObjects 工具把活干完。

[![Platform](https://img.shields.io/badge/ArcGIS%20Desktop-10.2%2B-2f7d7d)](https://www.esri.com/)
[![Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)](https://dotnet.microsoft.com/)
[![Arch](https://img.shields.io/badge/build-x86-orange)]()
[![Lang](https://img.shields.io/badge/C%23-7.8k%20LOC-blue)]()
[![Tools](https://img.shields.io/badge/ArcObjects%20tools-37-6f42c1)]()
[![Test](https://img.shields.io/badge/AddInTester-6%2F6%20PASS-brightgreen)]()

![ArcMap Agent 面板](dist/ArcMapAgent_v0.4.3/images/screenshot-panel.png)

---

## 这是什么

一个塞进 **ArcGIS Desktop 10.x（ArcMap）** 的 **C# .NET Add-In**。形态是工具栏按钮 + 停靠面板（DockableWindow），
面板里是一个能多轮调用 ArcObjects 工具的 AI 助手：

```
你：列出当前地图的所有图层，并统计 continent 图层的要素数量
[调用工具] list_layers        {}
[工具结果] 当前数据框【图层】共有 1 个图层：1. continent [FeatureLayerClass]
[调用工具] feature_count      {"layer":"continent"}
[工具结果] 图层【continent】共有 8 个要素。
Agent：## 当前地图图层信息 …
```

能干的活：加图层、改符号化、打标注、跑 ArcToolbox 空间分析、做选择与定义查询、导出地图 ——
UI 与能力对齐同系列的 **QGIS Agent** 插件。

- **规模**：约 7,800 行 C#（28 个源文件）、**37 个内置工具**、**90 条常用 GP 工具目录**。
- **状态**：本地 ArcGIS 10.4.1 实测 **6/6 PASS**，可日常使用；MCP 出口已通。

---

## 为什么给一个「过时」的平台做插件

ArcGIS Desktop 10.x 早已停止功能更新，但因为历史数据、国土/规划行业的既有流程、各种定制工具链都绑在它上面，
**短期换不掉**。这批用户的处境很尴尬：一边是被时代淘汰的 32 位桌面软件，一边是最新的大模型能力，中间没有桥。

早期做法是 Python Add-In（如 `TiandituTools`），实际用起来卡顿感明显。根因不是代码写得差，是平台限制：

| Python Add-In 的限制 | 后果 |
| --- | --- |
| Tkinter 不能进 ArcMap 进程（会 CRT abort） | 界面被迫丢到**独立子进程**，靠 JSON 来回传 → 多一层进程 + 序列化开销 |
| `comtypes` **晚绑定** COM | 每次 ArcObjects 调用都有反射开销，批量要素操作慢 |
| Python 2.7 + GIL | 没有原生 async，LLM 流式只能自己管 |

所以换了路线：**用 C# 重写**。

| 维度 | Python Add-In | C# / .NET Add-In（本工程） |
| --- | --- | --- |
| 解释器 | Python 2.7 32 位，子进程冷启数百 ms~1s | 编译 IL + JIT，DLL 随 ArcMap 直接加载 |
| UI | Tkinter **必须丢子进程** | 原生 WinForms **DockableWindow**，与 ArcMap 同一 UI 线程 |
| ArcObjects | `comtypes` 晚绑定，反射开销大 | 直接引用 `ESRI.ArcGIS.*` **早绑定**互操作，调用近乎原生 |
| HTTP / LLM | `urllib2` 同步，流式自管 | `HttpClient` + **async/await**，原生流式，UI 不冻结 |
| 多线程 | GIL | 真并行 `Task`；STA + `Invoke` 回主线程 |
| 部署 | `.esriaddin`(ZIP) + py 脚本 | `.esriaddin`(ZIP) + DLL |

> **要诚实的地方**：C# 不能让 AI「答得更快」—— LLM 的网络往返（几百 ms 到数秒）跟语言无关。
> C# 解决的是**插件自身跟手**：界面不卡、工具调用即时、流式逐字显示、绘图不闪烁。
> 如果你的「慢」主要来自模型本身或网络，换语言治标不治本。

**硬约束**（踩过才知道）：

- ArcMap 是 **32 位进程** → 插件 DLL 必须编译为 **x86**（不能 AnyCPU），否则加载失败。
- 宿主是 **.NET Framework**（本工程锁 4.8），不能用 .NET Core/5+ 专属 API。
- **STA**：UI 在主线程；网络放后台 `Task`；任何碰 ArcObjects 的操作必须 `Invoke` 回主线程。

---

## 能力清单

### 界面：7 个页签

| 页签 | 做什么 |
| --- | --- |
| **对话** | 一问一答气泡（用户右对齐 / Agent 左对齐）+ Markdown 渲染；工具调用显示为徽章，工具结果等宽块展示；支持多轮 function calling |
| **历史** | 会话以 JSON 存于 `%APPDATA%\ArcMapAgent\conversations\`，可保存 / 载入 / 删除 |
| **模型** | 多模型配置表 + 厂商预设（OpenAI / DeepSeek / GLM / 通义 / 本地 vLLM / Ollama）+ 连接测试 |
| **MCP** | 本地 `127.0.0.1` HTTP + JSON-RPC 端点，把 ArcObjects 工具暴露给外部 Agent；端口 / 令牌 / 特权工具 / 自检 |
| **地图** | 当前数据框与图层列表，直接缩放 / 显隐 / 移除 / 看字段 / 统计 / 加 shp / 开 MXD |
| **工具** | ArcObjects 工具目录 + slash 命令速查 |
| **帮助** | 功能与使用说明 |

### 37 个 ArcObjects 工具

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

带 `IsWriteTool` 标记的（着色 / 标注 / 选择 / 定义查询 / 移动 / 书签 / 导出 / `run_gp`）默认**弹窗确认**，
可在对话页勾选「跳过确认」关闭。

### `run_gp` 把整个 ArcToolbox 打通了

`run_gp(tool, params[])` 调 `Geoprocessor.Execute()`，把任意 ArcToolbox 工具交给 ArcMap 自己跑
（`OverwriteOutput=true`、`AddOutputsToMap=true`，并用 `MessageCount/GetMessage/GetSeverity` 回读执行消息）。
这一步基本关掉了「还有哪些 ArcMap 功能没对接」这个问题：缓冲 / 裁剪 / 相交 / 联合 / 擦除 / 空间连接、
投影 / 合并 / 溶解 / 字段计算、栅格⇄矢量 / Excel / KML / CAD、等值线 / 坡度 / 晕渲 / 插值 / 分区统计 都能直接跑。

两个必须记住的细节：

1. **工具名必须带工具箱别名后缀**：`Buffer_analysis`、`Clip_analysis`、`CalculateField_management`。
2. 参数一律**字符串数组按位置传**，GP 会自行转换（`"100 Meters"`、字段名、路径、`"SUM;MEAN"`）。

`gp_tools(keyword)` 内置一份 90 条常用目录，让模型不必猜参数顺序 —— 比动态 `ListTools` 实用得多（后者会吐几千个名字）。

---

## 架构

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
- **配置**：`%APPDATA%\ArcMapAgent\config.json`（多模型档 + ActiveName + Temperature + SkipConfirm + MCP 设置），
  旧的扁平配置会自动迁移。

---

## 验证证据（不是「应该能用」）

自动化回归工具 `AddInTester/`：x86 测试程序，流程 = 启动 ArcMap → 等完全就绪（轮询 AppROT 直到支持 `ICommandBars`）
→ UIA 切到「对话」页 → 用 **SendInput 模拟真实鼠标键盘**驱动面板。

| 步骤 | 检查项 | 结果 |
| --- | --- | --- |
| [5] | 停靠窗口 UID 解析 | 仅作参考（外部进程取不到） |
| [6] | 面板 UI 已构建 | **PASS** |
| [6a] | `/layers` 工具链路 | **PASS** |
| [6b] | `/add` 工具链路 | **PASS** —— 图层数 1→2 |
| [6c] | **自然语言多轮工具调用** | **PASS** —— ≥1 次 `[调用工具]` 与 `[工具结果]`，无 `[错误]` |
| [6d] | **标注链路（`set_labels`）** | **PASS** —— 结果含 `已为图层` + `标注` |

另经 **1:1 像素比对**确认头部文字与对话区同样锐利（笔画实心率 91.4% vs 96.3%），
且 `set_labels` 在真实 ArcMap 视图里确实画出带白色晕圈的标注。

测试程序与判定逻辑都在 [`AddInTester/`](AddInTester/) 里，跑完会把结果写到 `_test_result.txt`。
（开发期的取证材料 —— 屏幕截图、控件树转储、探针程序、文字发糊复现台 —— 属本机产物，未纳入本仓库。）

---

## 关键攻坚

这些都是真踩过的坑，完整版见 [`PROJECT_OVERVIEW.md`](PROJECT_OVERVIEW.md) §7。

**① 文字发糊：追到注册表里一个兼容标志。**
用户现象是「用了样式的控件字发糊，但 TextBox 里的字清楚」。这个「一半清楚一半糊」的对照是破案关键。
最终定位：本机给 `ArcMap.exe` 设了 `~ GDIDPISCALING DPIUNAWARE` 兼容标志，ArcMap 成了 DPI 不感知进程，
窗口被整体位图放大 2.25×；而 Windows 会拦截「直接画到窗口 DC 的 GDI 文字」并按真实 DPI 重绘。
**结论：文字只要先落进离屏位图缓冲，就一定发糊。**
改法是把标题挪到 `WndProc` 里、`base.WndProc` 之后用 `Graphics.FromHwnd(Handle)` 补画到窗口 DC
（笔画实心率 0.12 → 0.23）。顺带否掉了「GDI vs GDI+」和「透明 Label 回退」两个方向。

**② 头部比对话区糊：ClearType 是为「深字浅底」设计的。**
白字压 teal 底时笔画边缘留一圈红蓝彩边、对比度取不满。头部改成**浅底深字**后实心率 55.5% → **91.4%**。

**③ 按钮不水平对齐：`Control.Margin` 默认是 `(3,3,3,3)`。**
`FlowLayoutPanel` 按 `Margin.Top` 摆放，同排里混用默认值与手写值就会错开 3px（225% 缩放下是 7px）。
回归断言：42 排按钮全部 Top 一致，错位 0。

**④ `<Extension>` 必须显式写 `autoLoad="true"`。**
schema 默认值是 `false`（just-in-time），只写 `id`/`class` 时扩展**永远不会在启动时被实例化** ——
症状极像「加载项没加载」：工具栏按钮正常，但扩展日志一行都没有。

**⑤ Agent 调不动工具：插件侧协议不完整。**
只跑一轮、丢失 `tool_calls`/`tool_call_id`、提示词怂恿编造。改为多轮循环 + 按 OpenAI 规范序列化 + 强约束提示词。

**⑥ 回归测试的两个反直觉坑。**
测试程序未声明 DPI 感知时，跨进程 UIA 桥返回的 `IsEnabled`/`IsKeyboardFocusable` **一律误报 False**；
高 DPI 下 `ValuePattern.SetValue` 抛假异常 → 改用 `SendInput` 模拟真实用户。
另外 `AgentLog` 落盘时把换行替换成 `" | "`，测试脚本不还原就会让标注测试被**静默跳过**，看起来像功能缺失。

---

## 快速开始

### 1. 前提

- **ArcGIS Desktop 10.x** 已安装（提供 ArcObjects 运行时 + Add-In 框架）
- **.NET Framework 4.8**
- Visual Studio 2019/2022 或仅 `dotnet` CLI
- **无需安装 ArcObjects SDK 也能编译** —— 引用来自本机 GAC 中的 ArcObjects PIA 与
  `C:\Program Files (x86)\ArcGIS\Desktop10.4\bin\ESRI.ArcGIS.Desktop.AddIns.dll`

### 2. 构建

```bat
cd ArcMapAgent
build.bat
```

或手动：

```bat
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File pack.ps1
```

> `Compress-Archive` 不可用时可用 Python 打包（产物完全一致）：`python _pack.py`

产物：`ArcMapAgent.esriaddin`（= ZIP，官方布局 `Config.xml` + `Install/ArcMapAgent.dll` + `Images/`）。

### 3. 安装到 ArcMap

1. 关闭 ArcMap
2. 双击 `ArcMapAgent.esriaddin` 安装（即复制到 `文档\ArcGIS\AddIns\Desktop10.4\{AddInID}\`）
3. 启动 ArcMap → **自定义 → 工具条** → 勾选 **ArcMap Agent**
4. 点工具栏上的机器人图标，右侧出现「ArcMap Agent」面板

> **不清缓存会用到旧 DLL** —— 每次改代码后必须清空
> `%LOCALAPPDATA%\ESRI\Desktop10.4\AssemblyCache\{AddInID}\` 与 `%APPDATA%\ArcMapAgent\panel.log`，
> 否则会误判「修复没生效」。

### 4. 配置 LLM

打开 **模型** 页 → 从「厂商预设」一键填入或「＋ 添加」自建（OpenAI 兼容端点），
填 **API 端点** / **模型名** / **API Key** → **设为当前** → **测试连接**。

### 5. 使用

直接输入自然语言即可；也可以先用 **slash 命令**验证 ArcObjects 链路（不需要模型能力）：

```
/layers  /add <路径>  /fields <图层>  /count <图层>  /zoom <图层>  /stats <图层> <字段>
/label <图层> <字段>  /breaks <图层> <数值字段>  /where <图层> <条件>  /sel <图层> <条件>
/export <路径>  /gptools [关键词]  /help
```

### 6. MCP 外部接入

**MCP** 页点「启动服务」→ 复制客户端配置 → 粘到支持 HTTP(streamable) 的 MCP 客户端
（Claude Desktop / Cursor），外部 Agent 就能驱动 ArcMap。示例见
[`dist/ArcMapAgent_v0.4.3/MCP-外部接入示例.json`](dist/ArcMapAgent_v0.4.3/MCP-外部接入示例.json)。
服务只监听 `127.0.0.1` + 令牌校验；**特权工具**（添加/移除图层、保存文档）默认不暴露。

---

## 目录结构

```
arcmap_agent/
├── ArcMapAgent/            插件源码（28 个 .cs）+ 打包脚本
│   ├── AgentController.cs    Agent 多轮循环
│   ├── LlmClient.cs          OpenAI 兼容客户端（流式 + function calling）
│   ├── ArcMapTools.cs        ArcObjects 工具实现（37 个）
│   ├── McpBridge.cs          MCP HTTP + JSON-RPC 端点
│   ├── ChatPanel.cs 等       7 个页签
│   ├── UiTheme.cs / UiControls.cs   主题与自绘控件
│   └── README_build.md       构建与安装手册（最全）
├── AddInTester/            自动化回归测试（x86，模拟真实鼠标键盘）
├── dist/                   打包好的 .esriaddin + 安装说明
├── FEASIBILITY.md          最初的可行性分析报告
├── PROJECT_OVERVIEW.md     项目全貌（写给半年后的自己）
└── .gitignore  .gitattributes   仓库只保留源码 / 文档 / 安装包
```

---

## 已知限制

- ArcObjects 工具集尚未覆盖 QGIS Agent 的全部工具；符号化仅到「唯一值 / 简单符号」层级。
- **模型节点只能是 GP 工具**；插件里 ArcObjects 直接操作（加图层/渲染/出图）不是 GP 工具，塞不进 ModelBuilder 模型。
- MCP 端点实现的是 HTTP + JSON-RPC 最小子集（initialize / tools/list / tools/call / ping），
  仅支持 stdio 的 MCP 客户端需自备 HTTP 转发。
- 流式 function calling 解析依赖标准 OpenAI 格式；非标准端点可能只走普通对话。
- 多轮循环上限 `MaxToolRounds = 8`。
- Markdown 渲染为**自实现子集**（不依赖第三方库），暂不支持图片、嵌套列表与行内 HTML。
- 本工程以 **ArcGIS 10.4 的引用**编译；要在 10.2 / 10.3 运行需装对应版本 SDK 重编；10.5–10.8 一般可直接用。

---

## 文档索引

| 文件 | 内容 |
| --- | --- |
| [`PROJECT_OVERVIEW.md`](PROJECT_OVERVIEW.md) | 项目全貌：缘起 / 选型 / 能力 / 架构 / 验证证据 / 关键攻坚 / 部署 / 待办 |
| [`FEASIBILITY.md`](FEASIBILITY.md) | 最初的可行性分析：技术选型、架构映射、风险、分阶段计划 |
| [`ArcMapAgent/README_build.md`](ArcMapAgent/README_build.md) | 构建与安装手册：清单格式、部署、自动化验证、已知限制（**最全**） |
| [`dist/ArcMapAgent_v0.4.3/安装说明.md`](dist/ArcMapAgent_v0.4.3/安装说明.md) | 面向最终用户的安装说明 |

---

## 相关项目

同系列的 **QGIS Agent** —— 同样的思路跑在 QGIS 上，ArcMap Agent 的 UI 与工具集对齐它。

---

## 许可

本仓库暂未附带开源许可证。如需公开分发，请先补充 `LICENSE`。

ArcObjects / ArcGIS Desktop 是 Esri 的商标与版权产品，本插件不包含任何 Esri 二进制文件，
运行时依赖用户本机已安装的 ArcGIS Desktop。
