# ArcMap Agent 能力对标与优化路线

> 对标对象：易智瑞（Esri 中国）2026-11 发布的 GA Plus《AI 重塑 GIS：大模型驱动智能专题制图》
> 本文只回答两个问题：**它做了什么值得我们学**、**我们该按什么顺序补**。

---

## 一、那个产品的本质

GA Plus 不是一个"会聊天的 GIS"，它是**把"专题制图"这条业务链路拆成七个固定环节**，
每个环节配一套确定性工具，并且每个环节都留一个**看得见的产出物**：

```
数据入库 → 拓扑修复 → 标准化渲染 → 分层配图 → 风格美化 → 模板出图 → 服务发布
   ↓           ↓            ↓            ↓           ↓          ↓          ↓
 上图      问题图斑高亮    符号库      比例尺分级   参考图配色  版面模板   地图服务
          + 质检报告      + 渲染方案
```

这个结构里最值得注意的不是"用了大模型"，而是：**每一步都有落地产物**（问题图斑被选中、
报告能下载、样式能复用、服务能访问）。用户不是为了"对话"付费，是为了"东西做完了"付费。

---

## 二、它的七块能力，逐条拆开

| # | 能力 | 具体做法 |
|---|---|---|
| 1 | 批量上图 | 一次上传多个文件，自动全部加到地图；每个数据集给一个**唯一 ID**，存进"个人资源池"，后续按 ID 引用 |
| 2 | 智能质检 | 8 类检查：压盖面、内部缝隙、自相交、狭长面、内部孔洞、拓扑、多部件、尖锐角；配 `修复几何`、`条件过滤`；**异常要素高亮**；**一键导出检测报告** |
| 3 | 符号标准化 | 导入 `.stylx`（符号库）、导入 `.lyrx`（单图层样式）；**渲染方案可存储**（记渲染字段/类型/几何类型），按几何类型自动匹配 |
| 4 | 批量配图 | 用"图层名 ↔ 方案名"的精确/模糊匹配，一次给全图多个图层配色；顺便配置注记（字段、颜色、字号） |
| 5 | 分级显示 | 按比例尺设 `最小/最大` 显示范围，缩放时自动切换内容与样式；配图层过滤（属性条件） |
| 6 | 版面出图 | 导入 `.pagx` 版面模板，解析文本/图片/比例尺/指北针/图例；LLM 只负责补标题、说明这些差异化文字 |
| 7 | 服务发布 | 工程直接发布为地图服务/要素服务，可建切片缓存 |

外加一条：**多模态风格迁移**——上传一张参考图（水墨、手绘），整幅地图按该风格重绘。

---

## 三、我们现在有什么

**40 个工具**（不是文档里写的 37，这是最新实数），按能力分：

| 域 | 工具 | 数量 |
|---|---|---|
| 图层与结构 | `list_layers` `layer_info` `list_fields` `list_data_frames` `feature_count` | 5 |
| 统计 | `field_statistics` `unique_values` | 2 |
| 查询与选择 | `query_features` `select_by_attribute` `select_by_location` `get_selected_features` `clear_selection` | 5 |
| 符号化 | `set_simple_renderer` `set_unique_value_renderer` `set_graduated_colors` `set_layer_transparency` | 4 |
| 标注 | `set_labels` `clear_labels` `get_labels` | 3 |
| 过滤与可见性 | `set_definition_query` `clear_definition_query` `set_layer_visibility` | 3 |
| 视图与导航 | `zoom_to_full_extent` `zoom_to_layer` `zoom_to_scale` `zoom_to_selected` `get_map_extent` `set_map_extent` `refresh_view` | 7 |
| 书签 | `add_bookmark` `goto_bookmark` `list_bookmarks` | 3 |
| 图层管理 | `add_shapefile` `remove_layer` `move_layer` `rename_layer` | 4 |
| 产出 | `export_map` `save_document` | 2 |
| 通用 GP | `run_gp` `gp_tools` | 2 |

**结构特点**：全是**原子操作**，没有一件事是"整条链路一次做完"。

---

## 四、差距矩阵

| 能力 | GA Plus | 我们 | 差距 | 值不值得补 |
|---|---|---|---|---|
| 批量上图 | 多文件 + 唯一 ID + 资源池 | `add_shapefile` 单文件、按路径 | **有** | 值得（小） |
| **数据质检** | 8 类检查 + 修复 + 高亮 + 报告 | **完全没有** | **大** | **最值得** |
| 样式库 | `.stylx` / `.lyrx` + 方案存储 | 三个 renderer 工具，无持久化 | **大** | 值得 |
| 批量配图 | 名称匹配批量套用 | 逐图层调用 | **中** | 值得 |
| 比例尺分级 | min/max scale + 过滤 | 只有开关显隐 + 属性过滤 | **小** | 值得（极便宜） |
| 版面出图 | `.pagx` 模板 + 补文字 | 只有 `export_map` 出图 | **大** | 看场景 |
| 服务发布 | 地图/要素/切片服务 | 无 | **大** | 多数人不适用 |
| 风格迁移 | 多模态生成 | 无 | **大** | 不建议 |

**结论：我们缺的不是"更多工具"，是"闭环"。** 40 个原子工具已经够多，
但用户要做一次完整的"数据核验 → 修 → 出图 → 存档"，得靠模型自己串七八步，
中间任何一步跑偏就前功尽弃。

---

## 五、四个"照搬会踩空"的地方

这几条必须先说清，否则照着文章抄会白干：

1. **`.stylx` / `.lyrx` 是 ArcGIS Pro 的格式，ArcMap 读不了。**
   ArcMap 的对应物是 `.style`（符号库）和 `.lyr`（图层文件）。概念可以搬，文件格式不能搬。
2. **`.pagx` 也是 Pro 的。** ArcMap 的版面模板是 `.mxd`——用现成的 mxd 当模板，
   替换其中的文本元素再导出，这是 ArcMap 里等价的做法。
3. **服务发布需要 ArcGIS Server。** Desktop 用户绝大多数没有 Server 许可，
   这条对个人/内网用户基本无效。要给出路只能是导出 `.sd` 草稿包或整包数据。
4. **风格迁移产出的是"一张图"，不是"一套制图规范"。** 它好看，但下一批数据不会自动沿用。
   对每天出图的业务来说，**可复用的渲染方案**比一次性的风格化有用得多。

---

## 六、真正的启发（一条，但重要）

GA Plus 的所有能力都指向同一个设计原则：

> **把高频链路做成"一键闭环"，并且每一步都留下可交付的实物。**

对话只是入口，交付物才是价值。对照我们自己：

- 现在模型说"我给图层做了分级配色"——用户得自己去看图层对不对；
- 应该是"分级配色做完了，这是分级断点表、这是配色预览图、这是项目文件（已另存）"。

这个原则落到代码上，就是**每个闭环工具的返回值里必须带"我产出了什么文件/选中了多少要素/改了什么"**，
而不是只回一句"已完成"。

---

## 七、建议路线

### P0 · 数据质检闭环（最该先做）

**为什么是它**：它同时命中两件事——文章里最有分量的能力，以及你本人的日常工作（地址库/小区数据审核）。
而且它是唯一一个"当前完全空白、但业务上天天要用"的域。

**工具设计**（一个闭环，不是八个散装工具）：

```
check_features(layer, checks[])      → 统一入口，checks 可多选
    ├─ self_intersect   自相交
    ├─ overlap          压盖（同层要素互相重叠）
    ├─ gap              缝隙（相邻要素之间的空隙）
    ├─ sliver           狭长面（面积/周长比过低）
    ├─ hole             内部孔洞
    ├─ multipart        多部件
    ├─ sharp_angle      尖锐角
    ├─ null_geometry    空几何/无效几何
    └─ duplicate        重复要素（同位置同属性）

highlight_issues(layer, issue_id)    → 选中问题要素 + 缩放到它（对应"异常图斑高亮"）
repair_geometry(layer, method)       → 调 GP RepairGeometry
export_qc_report(path, format)       → CSV / HTML，含要素 OID、问题类型、坐标、面积
```

**关键实现点**：
- 简单要素（shapefile）没有 GDB 拓扑，所以走**几何级检查**：
  `ITopologicalOperator.IsSimple` 查自相交与无效几何；
  压盖/缝隙用 `ITopologicalOperator.Intersect` 或 GP 的 `Union`/`Erase`；
  狭长面用 `面积 / 周长²` 的经验阈值；尖锐角遍历环上相邻三点算夹角。
- 必须给 `max_rows` 上限 + 进度回调，否则几万要素会把 ArcMap 卡死。
- **报告要带 OID**，用户能据此在属性表里定位——这是"可整改"的前提，比好看的截图重要。

**改动量**：中等。约 600–900 行，新增 1 个 `QcTools.cs`，不动现有工具。

---

### P1 · 比例尺分级（最便宜的高价值）

`set_layer_scale_range(layer, min_scale, max_scale)`

`ILayer.MinimumScale` / `ILayer.MaximumScale` 在 API 里确认存在（见接口转储第 292–293 行），
**二十行代码**就能补齐文章里的"多层级配图"。配合已有的 `set_definition_query`
和 `set_layer_visibility`，就是完整的分级显示能力。

优先级排在质检之后只是因为"改动量小到随时能塞"，不是价值低。

---

### P2 · 渲染方案库

```
save_layer_style(layer, path)          → 存成 .lyr
apply_layer_style(layer, path)         → 从 .lyr 恢复（含字段映射）
save_render_scheme(name, layer)        → JSON：字段/类型/色带/分级数
apply_render_scheme(name, layer)       → 按几何类型自动匹配
batch_apply_styles([{layer, scheme}])  → 一次配完多个图层
```

**一个必须提前说的坑**：`.lyr` 里记着**原数据源路径**。直接套到另一个 shapefile 上，
打开的还是老数据。正确做法是打开 `.lyr` 后把 `IFeatureLayer.FeatureClass`
重指到目标要素类——否则用户会看到"样式应用成功但地图没变"。

---

### P3 · 版面模板出图

```
list_layout_elements()                 → 列出 mxd 版面里的文本/图例/比例尺元素
set_layout_text(element_name, text)    → 替换标题、说明、编制单位
export_layout(path, dpi)               → 按版面导出
```

用现成 `.mxd` 当模板，模型只负责填文字。等价于文章的 `.pagx` + LLM 补全，
但不需要解析 Pro 的压缩包格式。

---

### P4 · 资源池（顺带解决多轮对话的老问题）

```
register_dataset(path)  → 返回短 ID（D1、D2…）
list_datasets()
```

现在多轮对话里，模型经常"忘了"刚才那个 shapefile 的完整路径，只能重新问用户。
给数据集一个短 ID 后，"用刚才那个"就能真的对上。这也是文章里"唯一 ID"的等价物，
但目的不是去重，是**让多轮对话有稳定的指代**。

---

### 不建议做

| 项 | 原因 |
|---|---|
| 服务发布 | 需要 ArcGIS Server，Desktop 用户普遍没有；真要给，导出 `.sd` 比直接发布现实 |
| 全量风格迁移 | 需要图像生成，产出是栅格图；对"每天出图"的业务不解决问题 |
| `.stylx` 解析 | 是 Pro 格式，ArcMap 用不上 |
| 再堆原子工具 | 40 个已经够多，继续加只会让模型选错工具 |

---

## 八、如果只做一件事

**做 P0 质检闭环。**

理由不是"文章里有"，而是：你每天在做地址库和小区数据的审核。现在的插件能查、能选、
能上色，唯独不能替你判断"这批数据哪儿有问题"。而这恰恰是审核工作里最耗时的部分。

而且它有一个别的功能没有的性质：**它产出的是"证据"**（问题要素 + 报告文件），
可以直接拿去跟作业单位对账。这比"再出十张漂亮的地图"更贴近真实工作。

---

## 附：本次输入框改造（已实现，v0.4.4）

顺带记录了本轮已完成的改动，与上面的路线无关，但属于同一批交付：

- 输入框改为**多行**（`ChatInputBox`）：Enter 发送、Shift+Enter 换行、Ctrl+Enter 也发送；
- 内容变多自动长高，6 行封顶后出滚动条，不挤压上方对话记录；
- **输入法组字期间回车不发送**——挂钩 `WM_IME_STARTCOMPOSITION/ENDCOMPOSITION`，
  避免打拼音选字时把半截话发出去；
- 占位提示改由同色 Label 承载（多行 EDIT 不支持 `EM_SETCUEBANNER`），
  且必须用不透明白底——透明背景会退回 GDI+ 出字，在 GDIDPISCALING 下必糊。
