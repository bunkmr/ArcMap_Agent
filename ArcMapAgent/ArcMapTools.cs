using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ESRI.ArcGIS.ArcMapUI;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Display;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.DataSourcesFile;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.Framework;
using ESRI.ArcGIS.esriSystem;

namespace ArcMapAgent
{
    /// <summary>图层信息（供「地图」页以表格方式展示）。</summary>
    public class LayerInfo
    {
        public int Index;
        public string Name;
        public string TypeName;
        public bool Visible;
        public int FeatureCount;   // 非要素图层为 -1
    }

    /// <summary>
    /// 直接操作 ArcMap 的 ArcObjects 工具集合。
    /// 所有方法都必须在 ArcMap 主（UI）线程执行，因此统一通过 AgentControl.Invoke 回到 UI 线程。
    /// 应用实例从 AppContext.Application 读取（由 Add-In 组件在 Hook 可用时写入）。
    ///
    /// 这里只使用 ArcMap/ArcObjects 原生 API（IMxDocument / IMap / ILayer / IFeatureClass / IActiveView 等），
    /// 不依赖任何 QGIS 概念；工具语义对应 ArcMap 用户在界面上能做的操作（图层管理、缩放、字段查看、保存地图等）。
    /// </summary>
    public static partial class ArcMapTools
    {
        private static IMxDocument GetMxDocument()
        {
            if (AppContext.Application == null) return null;
            return AppContext.Application.Document as IMxDocument;
        }

        /// <summary>按 1 基索引或名称（模糊）解析图层。</summary>
        private static ILayer ResolveLayer(IMap map, string refStr)
        {
            if (map == null || string.IsNullOrWhiteSpace(refStr)) return null;
            int idx;
            if (int.TryParse(refStr.Trim(), out idx))
            {
                if (idx >= 1 && idx <= map.LayerCount) return map.get_Layer(idx - 1);
            }
            for (int i = 0; i < map.LayerCount; i++)
            {
                ILayer l = map.get_Layer(i);
                if (l.Name != null && l.Name.Equals(refStr, StringComparison.OrdinalIgnoreCase)) return l;
            }
            for (int i = 0; i < map.LayerCount; i++)
            {
                ILayer l = map.get_Layer(i);
                if (l.Name != null && l.Name.IndexOf(refStr, StringComparison.OrdinalIgnoreCase) >= 0) return l;
            }
            return null;
        }

        // ---- 1. 列出当前地图的所有图层 ----
        public static string ListLayers(AgentControl ui)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档（IMxDocument 为空，请先从工具栏按钮打开面板）。"; return; }
                    IMap map = mxDoc.FocusMap;
                    var sb = new StringBuilder();
                    sb.AppendLine("当前数据框【" + (map.Name ?? "") + "】共有 " + map.LayerCount + " 个图层：");
                    for (int i = 0; i < map.LayerCount; i++)
                    {
                        ILayer lyr = map.get_Layer(i);
                        sb.AppendLine("  " + (i + 1) + ". " + (lyr.Name ?? "") + "  [" + lyr.GetType().Name + "]");
                    }
                    result = sb.ToString();
                }
                catch (Exception ex) { result = "列出图层失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 2. 添加 shapefile 图层 ----
        public static string AddShapefile(AgentControl ui, string path)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) { result = "路径为空。"; return; }
                    path = path.Trim().Trim('"');
                    if (!System.IO.File.Exists(path)) { result = "文件不存在: " + path; return; }
                    string dir = System.IO.Path.GetDirectoryName(path);
                    string name = System.IO.Path.GetFileNameWithoutExtension(path);

                    IWorkspaceFactory wf = new ShapefileWorkspaceFactoryClass();
                    IFeatureWorkspace fw = (IFeatureWorkspace)wf.OpenFromFile(dir, 0);
                    IFeatureClass fc = fw.OpenFeatureClass(name);

                    IFeatureLayer fl = new FeatureLayerClass();
                    fl.FeatureClass = fc;
                    fl.Name = fc.AliasName;

                    IMxDocument mxDoc = GetMxDocument();
                    mxDoc.FocusMap.AddLayer(fl);
                    mxDoc.ActiveView.Refresh();

                    result = "已添加图层: " + fl.Name + "（要素数 " + fc.FeatureCount(null) + "）";
                }
                catch (Exception ex) { result = "添加失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 3. 列出某图层的字段 ----
        public static string ListFields(AgentControl ui, string layerRef)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    ILayer layer = ResolveLayer(mxDoc.FocusMap, layerRef);
                    if (layer == null) { result = "未找到图层: " + layerRef; return; }
                    IFeatureLayer fl = layer as IFeatureLayer;
                    if (fl == null || fl.FeatureClass == null) { result = "图层【" + layer.Name + "】不是要素图层，无法列出字段。"; return; }
                    IFields fields = fl.FeatureClass.Fields;
                    var sb = new StringBuilder();
                    sb.AppendLine("图层【" + layer.Name + "】字段（共 " + fields.FieldCount + " 个）：");
                    for (int i = 0; i < fields.FieldCount; i++)
                    {
                        IField f = fields.get_Field(i);
                        sb.AppendLine("  " + (i + 1) + ". " + f.Name + "  类型=" + f.Type + "  长度=" + f.Length);
                    }
                    result = sb.ToString();
                }
                catch (Exception ex) { result = "列出字段失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 4. 统计某图层的要素数量 ----
        public static string FeatureCount(AgentControl ui, string layerRef)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    ILayer layer = ResolveLayer(mxDoc.FocusMap, layerRef);
                    if (layer == null) { result = "未找到图层: " + layerRef; return; }
                    IFeatureLayer fl = layer as IFeatureLayer;
                    if (fl == null || fl.FeatureClass == null) { result = "图层【" + layer.Name + "】不是要素图层。"; return; }
                    int n = fl.FeatureClass.FeatureCount(null);
                    result = "图层【" + layer.Name + "】共有 " + n + " 个要素。";
                }
                catch (Exception ex) { result = "统计要素失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 5. 缩放到某图层 ----
        public static string ZoomToLayer(AgentControl ui, string layerRef)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    ILayer layer = ResolveLayer(mxDoc.FocusMap, layerRef);
                    if (layer == null) { result = "未找到图层: " + layerRef; return; }
                    IEnvelope env = layer.AreaOfInterest;
                    if (env == null || env.IsEmpty) { result = "图层【" + layer.Name + "】没有有效范围。"; return; }
                    IActiveView view = mxDoc.ActiveView;
                    view.Extent = env;
                    view.Refresh();
                    result = "已缩放到图层【" + layer.Name + "】。";
                }
                catch (Exception ex) { result = "缩放失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 6. 移除图层 ----
        public static string RemoveLayer(AgentControl ui, string layerRef)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    ILayer layer = ResolveLayer(mxDoc.FocusMap, layerRef);
                    if (layer == null) { result = "未找到图层: " + layerRef; return; }
                    string nm = layer.Name;
                    mxDoc.FocusMap.DeleteLayer(layer);
                    mxDoc.ActiveView.Refresh();
                    result = "已移除图层: " + nm;
                }
                catch (Exception ex) { result = "移除失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 7. 设置图层可见性 ----
        public static string SetLayerVisibility(AgentControl ui, string layerRef, bool visible)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    ILayer layer = ResolveLayer(mxDoc.FocusMap, layerRef);
                    if (layer == null) { result = "未找到图层: " + layerRef; return; }
                    layer.Visible = visible;
                    mxDoc.ActiveView.PartialRefresh(esriViewDrawPhase.esriViewGeography, null, null);
                    result = "图层【" + layer.Name + "】已" + (visible ? "显示" : "隐藏") + "。";
                }
                catch (Exception ex) { result = "设置可见性失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 8. 列出地图文档中的所有数据框（Map） ----
        public static string ListDataFrames(AgentControl ui)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) { result = "无法获取地图文档。"; return; }
                    IMaps maps = mxDoc.Maps;
                    var sb = new StringBuilder();
                    sb.AppendLine("地图文档共有 " + maps.Count + " 个数据框：");
                    for (int i = 0; i < maps.Count; i++)
                    {
                        IMap m = maps.get_Item(i);
                        sb.AppendLine("  " + (i + 1) + ". " + (m.Name ?? "") + "  （图层数 " + m.LayerCount + "）");
                    }
                    result = sb.ToString();
                }
                catch (Exception ex) { result = "列出数据框失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 9. 保存地图文档（MXD） ----
        public static string SaveDocument(AgentControl ui, string path)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    if (AppContext.Application == null) { result = "无法获取 ArcMap 应用程序实例。"; return; }
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        // 保存到当前文档（若从未保存过，ArcMap 会弹出保存对话框）
                        AppContext.Application.SaveDocument("");
                        result = "已保存当前地图文档。";
                    }
                    else
                    {
                        path = path.Trim().Trim('"');
                        AppContext.Application.SaveAsDocument(path, false);
                        result = "已另存为: " + path;
                    }
                }
                catch (Exception ex) { result = "保存失败: " + ex.Message; }
            }));
            return result;
        }

        // ---- 10. 结构化：当前数据框信息 ----
        public static string ActiveDataFrameName(AgentControl ui)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    result = mxDoc == null || mxDoc.FocusMap == null ? "(无)" : (mxDoc.FocusMap.Name ?? "");
                }
                catch { result = "(无)"; }
            }));
            return result;
        }

        // ---- 11. 结构化：数据框列表 ----
        public static List<string> GetDataFrames(AgentControl ui)
        {
            var list = new List<string>();
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) return;
                    IMaps maps = mxDoc.Maps;
                    for (int i = 0; i < maps.Count; i++)
                    {
                        IMap m = maps.get_Item(i);
                        list.Add((m.Name ?? ("数据框" + (i + 1))) + "（" + m.LayerCount + " 图层）");
                    }
                }
                catch { }
            }));
            return list;
        }

        // ---- 12. 结构化：图层列表 ----
        public static List<LayerInfo> GetLayers(AgentControl ui)
        {
            var list = new List<LayerInfo>();
            ui.Invoke((Action)(() =>
            {
                try
                {
                    IMxDocument mxDoc = GetMxDocument();
                    if (mxDoc == null) return;
                    IMap map = mxDoc.FocusMap;
                    for (int i = 0; i < map.LayerCount; i++)
                    {
                        ILayer lyr = map.get_Layer(i);
                        var info = new LayerInfo
                        {
                            Index = i + 1,
                            Name = lyr.Name ?? "",
                            TypeName = lyr.GetType().Name,
                            Visible = lyr.Visible,
                            FeatureCount = -1
                        };
                        var fl = lyr as IFeatureLayer;
                        if (fl != null && fl.FeatureClass != null)
                        {
                            try { info.FeatureCount = fl.FeatureClass.FeatureCount(null); } catch { }
                        }
                        list.Add(info);
                    }
                }
                catch { }
            }));
            return list;
        }

        // =====================================================================
        //  以下为「让 Agent 真正能干活」的扩展工具：符号化、查询、统计、视图控制。
        //  所有方法都自带 try/catch 并返回可读文本，任何 ArcObjects 异常都变成对话里的一句话，
        //  不会把 ArcMap 带崩。
        // =====================================================================

        /// <summary>把 UI 线程上的操作包装成「返回字符串」的同步调用。</summary>
        private static string Run(AgentControl ui, Func<string> body)
        {
            string result = "";
            ui.Invoke((Action)(() =>
            {
                try { result = body(); }
                catch (Exception ex) { result = "操作失败: " + ex.Message; }
            }));
            return result;
        }

        private static string ResolveMap(IMxDocument mxDoc, string layerRef, out ILayer layer)
        {
            layer = null;
            if (mxDoc == null) return "无法获取地图文档（请确认面板由 ArcMap 工具栏按钮打开）。";
            layer = ResolveLayer(mxDoc.FocusMap, layerRef);
            if (layer == null) return "未找到图层: " + layerRef + "（可用序号或名称，见 /layers）";
            return null;
        }

        private static string Fmt(object v)
        {
            if (v == null || v is DBNull) return "(空)";
            return v.ToString();
        }

        private static void RefreshAfterSymbolChange(IMxDocument mxDoc)
        {
            try { mxDoc.ActiveView.ContentsChanged(); } catch { }
            try { mxDoc.UpdateContents(); } catch { }
            try { mxDoc.ActiveView.Refresh(); } catch { }
        }

        /// <summary>黄金角取色：相邻类别颜色差异大、整体不刺眼。</summary>
        private static IColor ClassColor(int idx)
        {
            double hue = (idx * 0.6180339887498949) % 1.0;
            double sat = 0.52 + 0.18 * ((idx % 3) / 2.0);
            double val = 0.92 - 0.20 * ((idx / 3) % 2);
            int i = (int)Math.Floor(hue * 6);
            double f = hue * 6 - i;
            double p = val * (1 - sat), q = val * (1 - f * sat), t = val * (1 - (1 - f) * sat);
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = val; g = t; b = p; break;
                case 1: r = q; g = val; b = p; break;
                case 2: r = p; g = val; b = t; break;
                case 3: r = p; g = q; b = val; break;
                case 4: r = t; g = p; b = val; break;
                default: r = val; g = p; b = q; break;
            }
            IRgbColor c = new RgbColorClass();
            c.Red = (int)Math.Round(r * 255);
            c.Green = (int)Math.Round(g * 255);
            c.Blue = (int)Math.Round(b * 255);
            return c;
        }

        private static IColor Gray(int v)
        {
            IRgbColor c = new RgbColorClass();
            c.Red = v; c.Green = v; c.Blue = v;
            return c;
        }

        /// <summary>按几何类型造一个合适的符号（面/线/点各用各自的简单符号）。</summary>
        private static ISymbol MakeSymbol(esriGeometryType gt, IColor color)
        {
            if (gt == esriGeometryType.esriGeometryPolygon)
            {
                ISimpleFillSymbol fs = new SimpleFillSymbolClass();
                fs.Color = color;
                fs.Style = esriSimpleFillStyle.esriSFSSolid;
                ISimpleLineSymbol ol = new SimpleLineSymbolClass();
                ol.Color = Gray(90);
                ol.Width = 0.6;
                ol.Style = esriSimpleLineStyle.esriSLSSolid;
                fs.Outline = ol;
                return (ISymbol)fs;
            }
            if (gt == esriGeometryType.esriGeometryPolyline)
            {
                ISimpleLineSymbol ls = new SimpleLineSymbolClass();
                ls.Color = color;
                ls.Width = 1.4;
                ls.Style = esriSimpleLineStyle.esriSLSSolid;
                return (ISymbol)ls;
            }
            ISimpleMarkerSymbol ms = new SimpleMarkerSymbolClass();
            ms.Color = color;
            ms.Style = esriSimpleMarkerStyle.esriSMSCircle;
            ms.Size = 7;
            return (ISymbol)ms;
        }

        // ---- 13. 图层详情 ----
        public static string LayerInfo(AgentControl ui, string layerRef)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                var sb = new StringBuilder();
                sb.AppendLine("图层【" + layer.Name + "】");
                sb.AppendLine("  类型: " + layer.GetType().Name);
                sb.AppendLine("  可见: " + (layer.Visible ? "是" : "否"));
                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl != null && fl.FeatureClass != null)
                {
                    IFeatureClass fc = fl.FeatureClass;
                    sb.AppendLine("  要素数: " + fc.FeatureCount(null));
                    sb.AppendLine("  几何类型: " + fc.ShapeType);
                    sb.AppendLine("  字段数: " + fc.Fields.FieldCount);
                    try
                    {
                        IGeoDataset fcgd = fc as IGeoDataset;
                        if (fcgd != null && fcgd.Extent != null)
                        {
                            IEnvelope e = fcgd.Extent;
                            sb.AppendLine("  范围: X[" + e.XMin.ToString("0.###") + ", " + e.XMax.ToString("0.###")
                                          + "]  Y[" + e.YMin.ToString("0.###") + ", " + e.YMax.ToString("0.###") + "]");
                        }
                    }
                    catch { }
                    try
                    {
                        IGeoDataset gd = fc as IGeoDataset;
                        if (gd != null && gd.SpatialReference != null)
                            sb.AppendLine("  坐标系: " + gd.SpatialReference.Name);
                    }
                    catch { }
                }
                try
                {
                    IDataLayer dl = layer as IDataLayer;
                    if (dl != null)
                    {
                        IDataset ds = dl.DataSourceName as IDataset;
                        if (ds != null)
                        {
                            sb.AppendLine("  数据源: " + ds.Name);
                            if (ds.Workspace != null) sb.AppendLine("  工作空间: " + ds.Workspace.PathName);
                        }
                    }
                }
                catch { }
                return sb.ToString();
            });
        }

        // ---- 14. 字段唯一值（分类前必须先看有哪些类） ----
        public static string UniqueValues(AgentControl ui, string layerRef, string field, int maxValues)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                IFeatureClass fc = fl.FeatureClass;
                int fi = fc.FindField(field);
                if (fi < 0)
                {
                    var sb0 = new StringBuilder("字段不存在: " + field + "。可用字段: ");
                    for (int k = 0; k < fc.Fields.FieldCount; k++)
                        sb0.Append(fc.Fields.get_Field(k).Name + (k < fc.Fields.FieldCount - 1 ? ", " : ""));
                    return sb0.ToString();
                }

                if (maxValues <= 0) maxValues = 30;
                var counts = new Dictionary<string, int>();
                var order = new List<string>();
                IFeatureCursor cur = fc.Search(null, false);
                try
                {
                    IFeature f;
                    while ((f = cur.NextFeature()) != null)
                    {
                        string s = Fmt(f.get_Value(fi));
                        if (!counts.ContainsKey(s)) { counts[s] = 0; order.Add(s); }
                        counts[s]++;
                        if (order.Count > 400) break;      // 高基数字段不必全扫
                    }
                }
                finally { try { Marshal.ReleaseComObject(cur); } catch { } }

                if (order.Count == 0) return "字段【" + field + "】没有任何值。";

                order.Sort((a, b) => counts[b].CompareTo(counts[a]));
                var sb = new StringBuilder();
                sb.AppendLine("字段【" + field + "】共 " + order.Count + " 个唯一值：");
                int show = Math.Min(order.Count, maxValues);
                for (int i = 0; i < show; i++)
                    sb.AppendLine("  " + (i + 1) + ". " + order[i] + "  (" + counts[order[i]] + " 个要素)");
                if (order.Count > show) sb.AppendLine("  … 其余 " + (order.Count - show) + " 个略");
                return sb.ToString();
            });
        }

        // ---- 15. 按字段唯一值分类着色 ----
        public static string SetUniqueValueRenderer(AgentControl ui, string layerRef, string field, int maxClasses)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层，无法分类着色。";
                IGeoFeatureLayer gfl = fl as IGeoFeatureLayer;
                if (gfl == null) return "图层【" + layer.Name + "】不支持符号化设置。";

                IFeatureClass fc = fl.FeatureClass;
                int fi = fc.FindField(field);
                if (fi < 0) return "字段不存在: " + field + "（先用 unique_values 或 list_fields 查可用字段）";
                if (maxClasses <= 0 || maxClasses > 64) maxClasses = 16;

                var counts = new Dictionary<string, int>();
                var order = new List<string>();
                IFeatureCursor cur = fc.Search(null, false);
                try
                {
                    IFeature f;
                    while ((f = cur.NextFeature()) != null)
                    {
                        string s = Fmt(f.get_Value(fi));
                        if (!counts.ContainsKey(s)) { counts[s] = 0; order.Add(s); }
                        counts[s]++;
                        if (order.Count > 300) break;
                    }
                }
                finally { try { Marshal.ReleaseComObject(cur); } catch { } }

                if (order.Count == 0) return "字段【" + field + "】没有任何值，无法分类。";
                order.Sort((a, b) => counts[b].CompareTo(counts[a]));

                // 类别过多时：只给前 N-1 个值单独配色，其余合并成「(其他)」
                bool hasOther = order.Count > maxClasses;
                int named = hasOther ? maxClasses - 1 : order.Count;
                if (named < 1) named = 1;

                IUniqueValueRenderer uvr = new UniqueValueRendererClass();
                uvr.FieldCount = 1;
                uvr.set_Field(0, field);
                uvr.UseDefaultSymbol = false;
                uvr.DefaultLabel = "(其他)";
                uvr.DefaultSymbol = MakeSymbol(fc.ShapeType, Gray(200));

                var known = new Dictionary<string, int>();
                for (int i = 0; i < named; i++)
                {
                    known[order[i]] = i;
                    uvr.AddValue(order[i], field, MakeSymbol(fc.ShapeType, ClassColor(i)));
                }
                if (hasOther)
                    uvr.AddValue("(其他)", field, MakeSymbol(fc.ShapeType, Gray(190)));

                gfl.Renderer = (IFeatureRenderer)uvr;
                RefreshAfterSymbolChange(mxDoc);

                var sb = new StringBuilder();
                sb.AppendLine("已按字段【" + field + "】对图层【" + layer.Name + "】完成唯一值分类着色。");
                sb.AppendLine("  类别数: " + (hasOther ? (named + 1) : named) + (hasOther ? "（" + (order.Count - named) + " 个低频值并入「(其他)」）" : ""));
                for (int i = 0; i < named; i++)
                    sb.AppendLine("   · " + order[i] + "  →  " + counts[order[i]] + " 个要素");
                sb.AppendLine("图例与配色已在内容列表中更新。");
                return sb.ToString();
            });
        }

        // ---- 16. 单一符号（整层同色） ----
        public static string SetSimpleRenderer(AgentControl ui, string layerRef, string colorText)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                IGeoFeatureLayer gfl = fl as IGeoFeatureLayer;
                if (gfl == null) return "图层【" + layer.Name + "】不支持符号化设置。";

                IColor color = null;
                string name = "默认色";
                if (!string.IsNullOrWhiteSpace(colorText))
                {
                    string t = colorText.Trim().TrimStart('#');
                    int r, g, b;
                    if (t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int hex))
                    {
                        r = (hex >> 16) & 0xFF; g = (hex >> 8) & 0xFF; b = hex & 0xFF;
                        IRgbColor rc = new RgbColorClass();
                        rc.Red = r; rc.Green = g; rc.Blue = b;
                        color = rc; name = "#" + t.ToUpperInvariant();
                    }
                    else if (TryNamedColor(colorText.Trim().ToLowerInvariant(), out r, out g, out b))
                    {
                        IRgbColor rc = new RgbColorClass();
                        rc.Red = r; rc.Green = g; rc.Blue = b;
                        color = rc; name = colorText.Trim();
                    }
                }
                if (color == null) color = ClassColor(0);

                ISimpleRenderer sr = new SimpleRendererClass();
                sr.Symbol = MakeSymbol(fl.FeatureClass.ShapeType, color);
                gfl.Renderer = (IFeatureRenderer)sr;
                RefreshAfterSymbolChange(mxDoc);
                return "已将图层【" + layer.Name + "】设置为单一符号（" + name + "）。";
            });
        }

        private static bool TryNamedColor(string name, out int r, out int g, out int b)
        {
            r = g = b = 0;
            switch (name)
            {
                case "红": case "红色": case "red": r = 220; g = 38; b = 38; return true;
                case "橙": case "橙色": case "orange": r = 234; g = 88; b = 12; return true;
                case "黄": case "黄色": case "yellow": r = 250; g = 204; b = 21; return true;
                case "绿": case "绿色": case "green": r = 22; g = 163; b = 74; return true;
                case "青": case "青色": case "cyan": case "teal": r = 13; g = 148; b = 136; return true;
                case "蓝": case "蓝色": case "blue": r = 37; g = 99; b = 235; return true;
                case "紫": case "紫色": case "purple": r = 147; g = 51; b = 234; return true;
                case "灰": case "灰色": case "gray": case "grey": r = 148; g = 163; b = 184; return true;
                case "黑": case "黑色": case "black": r = 30; g = 41; b = 59; return true;
                case "白": case "白色": case "white": r = 255; g = 255; b = 255; return true;
            }
            return false;
        }

        // ---- 17. 图层透明度 ----
        public static string SetLayerTransparency(AgentControl ui, string layerRef, int percent)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                ILayerEffects le = layer as ILayerEffects;
                if (le == null) return "图层【" + layer.Name + "】不支持透明度设置。";
                le.Transparency = (short)percent;
                RefreshAfterSymbolChange(mxDoc);
                return "图层【" + layer.Name + "】透明度已设为 " + percent + "%。";
            });
        }

        // ---- 18. 属性查询 ----
        public static string QueryFeatures(AgentControl ui, string layerRef, string where, int maxRows)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                IFeatureClass fc = fl.FeatureClass;
                if (maxRows <= 0 || maxRows > 100) maxRows = 20;

                IQueryFilter qf = new QueryFilterClass();
                if (!string.IsNullOrWhiteSpace(where))
                    qf.WhereClause = where.Trim();

                // 只取非几何、非大对象字段，避免输出爆炸
                var names = new List<string>();
                var idxs = new List<int>();
                for (int i = 0; i < fc.Fields.FieldCount; i++)
                {
                    IField fd = fc.Fields.get_Field(i);
                    if (fd.Type == esriFieldType.esriFieldTypeGeometry) continue;
                    if (fd.Type == esriFieldType.esriFieldTypeBlob) continue;
                    names.Add(fd.Name);
                    idxs.Add(i);
                    if (names.Count >= 10) break;
                }

                var sb = new StringBuilder();
                sb.AppendLine("查询 图层【" + layer.Name + "】" + (string.IsNullOrWhiteSpace(where) ? "（全部要素）" : "  条件: " + where));
                sb.AppendLine("显示字段: " + string.Join(", ", names.ToArray()));

                int shown = 0, total = 0;
                IFeatureCursor cur = fc.Search(qf, false);
                try
                {
                    IFeature f;
                    while ((f = cur.NextFeature()) != null)
                    {
                        total++;
                        if (shown >= maxRows) continue;
                        shown++;
                        var vals = new List<string>();
                        for (int k = 0; k < idxs.Count; k++) vals.Add(names[k] + "=" + Fmt(f.get_Value(idxs[k])));
                        sb.AppendLine("  行" + shown + ": " + string.Join("  ", vals.ToArray()));
                    }
                }
                finally { try { Marshal.ReleaseComObject(cur); } catch { } }

                if (total == 0) return "没有匹配的要素。" + (string.IsNullOrWhiteSpace(where) ? "" : "（条件可能写法有误，如：字段名 = '值'）");
                sb.AppendLine("共匹配 " + total + " 个要素" + (total > shown ? "，仅显示前 " + shown + " 个。" : "。"));
                return sb.ToString();
            });
        }

        // ---- 19. 按属性选择 ----
        public static string SelectByAttribute(AgentControl ui, string layerRef, string where)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                if (string.IsNullOrWhiteSpace(where)) return "请给出选择条件，例如：分公司 = '官渡'";

                IQueryFilter qf = new QueryFilterClass();
                qf.WhereClause = where.Trim();

                ISelectionSet sel = fl.FeatureClass.Select(
                    qf, esriSelectionType.esriSelectionTypeHybrid,
                    esriSelectionOption.esriSelectionOptionOnlyOne, null);
                IFeatureSelection fs = fl as IFeatureSelection;
                if (fs == null) return "图层【" + layer.Name + "】不支持选择集。";
                fs.SelectionSet = sel;
                fs.SelectionChanged();

                int n = sel == null ? 0 : sel.Count;
                try { mxDoc.ActiveView.PartialRefresh(esriViewDrawPhase.esriViewGeoSelection, null, null); } catch { }
                return "已在图层【" + layer.Name + "】中选择 " + n + " 个要素（条件: " + where + "）。";
            });
        }

        // ---- 20. 清除选择 ----
        public static string ClearSelection(AgentControl ui)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                IMap map = mxDoc.FocusMap;
                int cleared = 0;
                for (int i = 0; i < map.LayerCount; i++)
                {
                    IFeatureSelection fs = map.get_Layer(i) as IFeatureSelection;
                    if (fs == null) continue;
                    if (fs.SelectionSet != null && fs.SelectionSet.Count > 0) cleared++;
                    fs.Clear();
                }
                try { mxDoc.ActiveView.PartialRefresh(esriViewDrawPhase.esriViewGeoSelection, null, null); } catch { }
                return "已清除选择（涉及 " + cleared + " 个图层）。";
            });
        }

        // ---- 21. 字段统计 ----
        public static string FieldStatistics(AgentControl ui, string layerRef, string field)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                IFeatureClass fc = fl.FeatureClass;
                if (fc.FindField(field) < 0) return "字段不存在: " + field;

                IFeatureCursor cur = fc.Search(null, false);
                IDataStatistics ds = new DataStatisticsClass();
                ds.Field = field;
                // IFeatureCursor 在 PIA 里并不继承 ICursor（coclass 同时实现两者），必须显式转
                ds.Cursor = (ICursor)cur;
                IStatisticsResults res = ds.Statistics;
                try { Marshal.ReleaseComObject(cur); } catch { }

                if (res == null) return "字段【" + field + "】没有统计数据（可能是非数值字段）。";
                var sb = new StringBuilder();
                sb.AppendLine("字段【" + field + "】统计（图层 " + layer.Name + "）:");
                sb.AppendLine("  计数: " + res.Count);
                sb.AppendLine("  最小值: " + res.Minimum.ToString("0.####"));
                sb.AppendLine("  最大值: " + res.Maximum.ToString("0.####"));
                sb.AppendLine("  平均值: " + res.Mean.ToString("0.####"));
                sb.AppendLine("  总和: " + res.Sum.ToString("0.####"));
                sb.AppendLine("  标准差: " + res.StandardDeviation.ToString("0.####"));
                return sb.ToString();
            });
        }

        // ---- 22. 缩放到全图 ----
        public static string ZoomToFullExtent(AgentControl ui)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                IActiveView view = mxDoc.ActiveView;
                view.Extent = view.FullExtent;
                view.Refresh();
                return "已缩放到全图范围。";
            });
        }

        // ---- 23. 图层重命名 ----
        public static string RenameLayer(AgentControl ui, string layerRef, string newName)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                if (string.IsNullOrWhiteSpace(newName)) return "新名称为空。";
                string old = layer.Name;
                layer.Name = newName.Trim();
                RefreshAfterSymbolChange(mxDoc);
                return "图层已重命名: " + old + " → " + layer.Name;
            });
        }

        // ---- 24. 刷新视图 ----
        public static string RefreshView(AgentControl ui)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                RefreshAfterSymbolChange(mxDoc);
                return "已刷新地图视图与内容列表。";
            });
        }
    }
}
