using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ESRI.ArcGIS.ArcMapUI;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Display;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Output;

namespace ArcMapAgent
{
    /// <summary>
    /// ArcObjects 工具集（第二部分）：标注 / 定义查询 / 分级着色 / 空间选择 /
    /// 图层顺序 / 书签 / 地图导出 / 任意 Geoprocessing 工具。
    ///
    /// 设计原则与第一部分一致：全部在 ArcMap UI 线程执行，全部返回人话文本，
    /// 任何 ArcObjects 异常都被转换成一行的错误说明，绝不把 ArcMap 带崩。
    ///
    /// 关于「为什么以前没有标注功能」：ArcMap 的标注不是 ILayer 上的一个开关，
    /// 而是要走 IGeoFeatureLayer.DisplayAnnotation + ILabelEngineLayerProperties +
    /// IAnnotateLayerPropertiesCollection.Add() 这一整套接口（见 SetLabels 注释）。
    /// 这套接口之前完全没有对接，所以模型只能说「我做不到」。
    /// </summary>
    public static partial class ArcMapTools
    {
        // =====================================================================
        //  标注（Labeling）—— ArcMap 里对应「图层属性 → Labels 选项卡」
        // =====================================================================

        /// <summary>
        /// 显示图层标注。
        /// ArcMap 的标注由三个对象协作完成：
        ///   IGeoFeatureLayer.DisplayAnnotation      —— 总开关
        ///   ILabelEngineLayerProperties             —— 表达式 + 文本符号（大小/颜色/字体）
        ///   IAnnotateLayerPropertiesCollection      —— 图层保存的属性集合（先 Clear 再 Add）
        /// 少任何一步界面上都不会出现标注，这也是「看起来设了却没反应」的常见原因。
        /// </summary>
        /// <param name="field">用于标注的字段名（自动包成 [字段]）</param>
        /// <param name="expression">自定义表达式（给了就优先用，如 "Round([面积],1) &amp; \" 亩\""）</param>
        /// <param name="size">字号（磅）</param>
        /// <param name="colorText">颜色：中文色名或 #RRGGBB</param>
        /// <param name="bold">是否加粗</param>
        /// <param name="halo">是否加白色晕圈（底图复杂时非常有用）</param>
        /// <param name="which">all / visible / selected</param>
        public static string SetLabels(AgentControl ui, string layerRef, string field,
                                       double size, string colorText, bool bold,
                                       string expression, bool halo, string which)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;

                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null)
                    return "图层【" + layer.Name + "】不是要素图层，无法标注（栅格/图层组请先选要素图层）。";
                IGeoFeatureLayer gfl = fl as IGeoFeatureLayer;
                if (gfl == null) return "图层【" + layer.Name + "】不支持标注。";

                IFeatureClass fc = fl.FeatureClass;

                // 1) 表达式
                string expr = expression == null ? "" : expression.Trim();
                if (expr.Length == 0)
                {
                    if (string.IsNullOrWhiteSpace(field))
                        return "请给出要标注的字段名（先用 list_fields 查看字段）。";
                    string f = field.Trim();
                    if (f.IndexOf('[') >= 0) expr = f;
                    else if (fc.FindField(f) >= 0) expr = "[" + f + "]";
                    else
                    {
                        var sb0 = new StringBuilder("字段不存在: " + f + "。可用字段: ");
                        for (int k = 0; k < fc.Fields.FieldCount; k++)
                        {
                            IField fd = fc.Fields.get_Field(k);
                            if (fd.Type == esriFieldType.esriFieldTypeGeometry) continue;
                            sb0.Append(fd.Name + (k < fc.Fields.FieldCount - 1 ? ", " : ""));
                        }
                        return sb0.ToString();
                    }
                }
                else if (expr.IndexOf('[') < 0 && fc.FindField(expr) >= 0)
                {
                    expr = "[" + expr + "]";
                }

                // 2) 文本符号
                ITextSymbol ts = new TextSymbolClass();
                if (size <= 0) size = 10;
                ts.Size = size;
                IColor col = ParseColorOr(colorText, 30, 41, 59, out _);
                ts.Color = col;
                TryApplyTextFont(ts, "Microsoft YaHei", size, bold, false);
                if (halo) TryApplyHalo(ts);

                // 3) 标注引擎属性
                ILabelEngineLayerProperties2 lelp = new LabelEngineLayerPropertiesClass();
                lelp.Expression = expr;
                lelp.IsExpressionSimple = true;
                lelp.Symbol = ts;

                // 放置属性：按几何类型给出合适的定位方式（点/线/面各不同）
                try
                {
                    IBasicOverposterLayerProperties4 basic = new BasicOverposterLayerPropertiesClass();
                    basic.FeatureType = FeatureTypeOf(fc.ShapeType);
                    basic.LabelWeight = esriBasicOverposterWeight.esriMediumWeight;
                    IOverposterLayerProperties olp = basic as IOverposterLayerProperties;
                    if (olp != null) lelp.OverposterLayerProperties = olp;
                }
                catch { /* 放置属性失败不影响标注显示，用引擎默认值 */ }

                IAnnotateLayerProperties alp = lelp as IAnnotateLayerProperties;
                if (alp == null) return "当前环境不支持标注引擎（ILabelEngineLayerProperties 无法转换为 IAnnotateLayerProperties）。";
                alp.FeatureLayer = fl;
                alp.WhereClause = "";
                alp.DisplayAnnotation = true;
                alp.Priority = 0;
                string w = (which ?? "all").Trim().ToLowerInvariant();
                alp.LabelWhichFeatures =
                    w.StartsWith("sel") ? esriLabelWhichFeatures.esriSelectedFeatures :
                    w.StartsWith("vis") ? esriLabelWhichFeatures.esriVisibleFeatures :
                                          esriLabelWhichFeatures.esriAllFeatures;

                // 4) 挂上并打开开关
                IAnnotateLayerPropertiesCollection coll = gfl.AnnotationProperties;
                coll.Clear();
                coll.Add(alp);
                gfl.DisplayAnnotation = true;

                RefreshLabels(mxDoc, layer);

                var sb = new StringBuilder();
                sb.AppendLine("已为图层【" + layer.Name + "】开启标注。");
                sb.AppendLine("  表达式: " + expr);
                sb.AppendLine("  字号: " + size.ToString("0.#") + " 磅" + (bold ? "（加粗）" : "")
                              + (halo ? " + 晕圈" : ""));
                sb.AppendLine("  标注范围: " + (alp.LabelWhichFeatures == esriLabelWhichFeatures.esriSelectedFeatures ? "仅选中要素"
                              : alp.LabelWhichFeatures == esriLabelWhichFeatures.esriVisibleFeatures ? "仅可见要素" : "所有要素"));
                sb.AppendLine("若注记被淹没在底图里，可再叠加晕圈或调整字号。");
                return sb.ToString();
            });
        }

        /// <summary>关闭图层标注。</summary>
        public static string ClearLabels(AgentControl ui, string layerRef)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IGeoFeatureLayer gfl = layer as IGeoFeatureLayer;
                if (gfl == null) return "图层【" + layer.Name + "】不支持标注。";
                try { gfl.AnnotationProperties.Clear(); } catch { }
                gfl.DisplayAnnotation = false;
                RefreshLabels(mxDoc, layer);
                return "已关闭图层【" + layer.Name + "】的标注。";
            });
        }

        /// <summary>查看图层当前的标注设置。</summary>
        public static string GetLabels(AgentControl ui, string layerRef)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IGeoFeatureLayer gfl = layer as IGeoFeatureLayer;
                if (gfl == null) return "图层【" + layer.Name + "】不支持标注。";

                var sb = new StringBuilder();
                sb.AppendLine("图层【" + layer.Name + "】标注状态: " + (gfl.DisplayAnnotation ? "已开启" : "已关闭"));
                IAnnotateLayerPropertiesCollection coll = gfl.AnnotationProperties;
                int n = coll == null ? 0 : coll.Count;
                sb.AppendLine("  标注类数量: " + n);
                for (int i = 0; i < n && i < 5; i++)
                {
                    try
                    {
                        IAnnotateLayerProperties alp;
                        IElementCollection placed, unplaced;
                        coll.QueryItem(i, out alp, out placed, out unplaced);
                        if (alp == null) continue;
                        ILabelEngineLayerProperties lelp = alp as ILabelEngineLayerProperties;
                        sb.AppendLine("  [" + (i + 1) + "] 表达式: "
                                      + (lelp != null ? lelp.Expression : "(非标准标注引擎)"));
                        if (lelp != null && lelp.Symbol != null)
                            sb.AppendLine("      字号: " + lelp.Symbol.Size.ToString("0.#") + " 磅");
                        sb.AppendLine("      范围: " + alp.LabelWhichFeatures
                                      + "  Where: " + (string.IsNullOrEmpty(alp.WhereClause) ? "(无)" : alp.WhereClause));
                        if (placed != null && placed.Count > 0)
                            sb.AppendLine("      当前放置数量: " + placed.Count);
                    }
                    catch (Exception ex) { sb.AppendLine("  [" + (i + 1) + "] 读取失败: " + ex.Message); }
                }
                if (n == 0) sb.AppendLine("  提示：用 set_labels 指定字段即可开启标注。");
                return sb.ToString();
            });
        }

        private static esriBasicOverposterFeatureType FeatureTypeOf(esriGeometryType gt)
        {
            if (gt == esriGeometryType.esriGeometryPolygon) return esriBasicOverposterFeatureType.esriOverposterPolygon;
            if (gt == esriGeometryType.esriGeometryPolyline) return esriBasicOverposterFeatureType.esriOverposterPolyline;
            return esriBasicOverposterFeatureType.esriOverposterPoint;
        }

        /// <summary>
        /// 给文本符号设字体（含加粗/斜体/中文字体）。
        /// ITextSymbol.Font 是 COM 的 IFontDisp，在 .NET 里没有强类型可赋值对象；
        /// 这里用 ProgID("StdFont") 在运行期造一个，避免为了一行字体设置去引 stdole PIA。
        /// 任何一步失败都静默跳过——保持默认字体也能正常显示。
        /// </summary>
        private static void TryApplyTextFont(ITextSymbol ts, string family, double pt, bool bold, bool italic)
        {
            try
            {
                if (ts == null || pt <= 0) return;
                Type std = Type.GetTypeFromProgID("StdFont");
                if (std == null) return;
                object font = Activator.CreateInstance(std);
                std.InvokeMember("Name", BindingFlags.SetProperty, null, font, new object[] { family });
                std.InvokeMember("Size", BindingFlags.SetProperty, null, font, new object[] { (float)pt });
                std.InvokeMember("Bold", BindingFlags.SetProperty, null, font, new object[] { bold });
                std.InvokeMember("Italic", BindingFlags.SetProperty, null, font, new object[] { italic });
                ts.GetType().InvokeMember("Font", BindingFlags.SetProperty, null, ts, new object[] { font });
            }
            catch { }
        }

        /// <summary>给文本符号加白色晕圈（ArcMap 的 TextSymbol 通过 IMask 支持 halo）。</summary>
        private static void TryApplyHalo(ITextSymbol ts)
        {
            try
            {
                IMask mask = ts as IMask;
                if (mask == null) return;
                IRgbColor wc = new RgbColorClass();
                wc.Red = 255; wc.Green = 255; wc.Blue = 255;
                ISimpleFillSymbol fs = new SimpleFillSymbolClass();
                fs.Color = wc;
                mask.MaskSymbol = fs;
                mask.MaskSize = 1.2;
                mask.MaskStyle = esriMaskStyle.esriMSHalo;
            }
            catch { }
        }

        private static void RefreshLabels(IMxDocument mxDoc, ILayer layer)
        {
            try { mxDoc.ActiveView.PartialRefresh(esriViewDrawPhase.esriViewGeography, layer, null); } catch { }
            try { mxDoc.ActiveView.ContentsChanged(); } catch { }
            try { mxDoc.UpdateContents(); } catch { }
            try { mxDoc.ActiveView.Refresh(); } catch { }
        }

        // =====================================================================
        //  定义查询（图层过滤）—— 对应「图层属性 → Definition Query」
        // =====================================================================

        public static string SetDefinitionQuery(AgentControl ui, string layerRef, string where)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IFeatureLayerDefinition fld = layer as IFeatureLayerDefinition;
                if (fld == null) return "图层【" + layer.Name + "】不支持定义查询（仅要素图层可用）。";
                if (string.IsNullOrWhiteSpace(where)) return "请给出 SQL 条件，例如：支局名 = '官渡支局' 或 人口 > 50000。";
                try
                {
                    fld.DefinitionExpression = where.Trim();
                }
                catch (Exception ex)
                {
                    return "定义查询设置失败（SQL 语法可能不对）: " + ex.Message + "\n示例：字段 = '值'、字段 > 100、字段 IN ('A','B')";
                }
                RefreshAfterSymbolChange(mxDoc);
                IFeatureLayer fl = layer as IFeatureLayer;
                int n = fl != null && fl.FeatureClass != null ? fl.FeatureClass.FeatureCount(null) : -1;
                return "已为图层【" + layer.Name + "】设置定义查询: " + where.Trim()
                       + "\n当前显示要素数: " + n + "\n（用 clear_definition_query 可取消）";
            });
        }

        public static string ClearDefinitionQuery(AgentControl ui, string layerRef)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IFeatureLayerDefinition fld = layer as IFeatureLayerDefinition;
                if (fld == null) return "图层【" + layer.Name + "】不支持定义查询。";
                fld.DefinitionExpression = "";
                RefreshAfterSymbolChange(mxDoc);
                return "已清除图层【" + layer.Name + "】的定义查询。";
            });
        }

        // =====================================================================
        //  分级着色（数值字段 → 色阶）—— 对应「图层属性 → Symbology → Graduated colors」
        // =====================================================================

        /// <summary>
        /// 按数值字段做分级着色。
        /// method: quantile（分位数，每级要素数接近）| equal（等间距）| natural（自然断点，调用 ArcObjects IClassify）
        /// </summary>
        public static string SetGraduatedColors(AgentControl ui, string layerRef, string field,
                                                int classes, string method, string ramp)
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

                IFeatureClass fc = fl.FeatureClass;
                int fi = fc.FindField(field);
                if (fi < 0) return "字段不存在: " + field + "（分级着色需要数值字段，可先用 field_stats 看取值范围）";
                IField fdef = fc.Fields.get_Field(fi);
                if (fdef.Type != esriFieldType.esriFieldTypeInteger && fdef.Type != esriFieldType.esriFieldTypeSmallInteger
                    && fdef.Type != esriFieldType.esriFieldTypeDouble && fdef.Type != esriFieldType.esriFieldTypeSingle)
                    return "字段【" + field + "】不是数值类型（" + fdef.Type + "），无法分级着色；类别字段请用 color_by_field。";

                if (classes < 2) classes = 5;
                if (classes > 32) classes = 32;

                // 取值（上限 5 万条，够统计出稳定分位点，又不会把大图层卡死）
                const int cap = 50000;
                var vals = new List<double>(1024);
                IQueryFilter qf = new QueryFilterClass();
                qf.SubFields = field;
                IFeatureCursor cur = fc.Search(qf, false);
                try
                {
                    IFeature f;
                    while ((f = cur.NextFeature()) != null)
                    {
                        object v = f.get_Value(fi);
                        if (v != null && !(v is DBNull))
                        {
                            double d;
                            if (double.TryParse(v.ToString(), out d) && !double.IsNaN(d)) vals.Add(d);
                        }
                        if (vals.Count >= cap) break;
                    }
                }
                finally { try { Marshal.ReleaseComObject(cur); } catch { } }

                if (vals.Count < classes)
                    return "字段【" + field + "】只有 " + vals.Count + " 个有效数值，不足以分 " + classes + " 级。";
                vals.Sort();

                double[] breaks;
                string methodName;
                if (!TryClassify(vals, classes, method, out breaks, out methodName))
                {
                    // 兜底：分位数
                    breaks = QuantileBreaks(vals, classes);
                    methodName = "quantile(内置兜底)";
                }

                IClassBreaksRenderer cbr = new ClassBreaksRendererClass();
                cbr.Field = field;
                cbr.SortClassesAscending = true;
                cbr.BreakCount = breaks.Length;
                cbr.MinimumBreak = breaks[0];

                double lo, hi;
                RampColors(ramp, out lo, out hi, out string rampName);
                int n = breaks.Length;
                for (int i = 0; i < n; i++)
                {
                    double t = n <= 1 ? 1.0 : (i / (double)(n - 1));
                    cbr.set_Symbol(i, MakeSymbol(fc.ShapeType, RampColor(lo, hi, t)));
                    cbr.set_Break(i, breaks[i]);
                    double upper = (i + 1 < n) ? breaks[i + 1] : vals[vals.Count - 1];
                    cbr.set_Label(i, FmtNum(breaks[i]) + " - " + FmtNum(upper));
                }

                gfl.Renderer = (IFeatureRenderer)cbr;
                RefreshAfterSymbolChange(mxDoc);

                var sb = new StringBuilder();
                sb.AppendLine("已按字段【" + field + "】对图层【" + layer.Name + "】做分级着色。");
                sb.AppendLine("  分级方法: " + methodName + "，级数: " + n + "，色带: " + rampName);
                sb.AppendLine("  取值范围: " + FmtNum(vals[0]) + " ~ " + FmtNum(vals[vals.Count - 1])
                              + "（样本 " + vals.Count + " 个）");
                for (int i = 0; i < n; i++)
                {
                    double upper = (i + 1 < n) ? breaks[i + 1] : vals[vals.Count - 1];
                    sb.AppendLine("   · " + FmtNum(breaks[i]) + " ~ " + FmtNum(upper));
                }
                return sb.ToString();
            });
        }

        private static string FmtNum(double d)
        {
            if (Math.Abs(d) >= 100000) return d.ToString("#,0");
            if (Math.Abs(d - Math.Round(d)) < 1e-9) return d.ToString("0");
            return d.ToString("0.###");
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        private static double[] QuantileBreaks(List<double> sorted, int classes)
        {
            var br = new double[classes];
            for (int i = 0; i < classes; i++)
            {
                int idx = (int)Math.Floor(i * (sorted.Count - 1) / (double)classes);
                br[i] = sorted[Math.Max(0, Math.Min(sorted.Count - 1, idx))];
            }
            return br;
        }

        /// <summary>优先用 ArcObjects 的 IClassify（自然断点）；不可用时返回 false 由调用方兜底。</summary>
        private static bool TryClassify(List<double> vals, int classes, string method,
                                        out double[] breaks, out string methodName)
        {
            breaks = null;
            methodName = "";
            string m = (method ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0) m = "quantile";
            try
            {
                if (m.StartsWith("equal") || m.StartsWith("等"))
                {
                    breaks = new double[classes];
                    double lo = vals[0], hi = vals[vals.Count - 1];
                    for (int i = 0; i < classes; i++) breaks[i] = lo + (hi - lo) * i / classes;
                    methodName = "等间距(equal interval)";
                    return true;
                }
                if (m.StartsWith("quantile") || m.StartsWith("分位") || m.StartsWith("quant"))
                {
                    breaks = QuantileBreaks(vals, classes);
                    methodName = "分位数(quantile)";
                    return true;
                }

                // 自然断点：用 esriSystem 的 IClassify，把频数直方图喂进去
                var hist = new Dictionary<long, int>();
                double lo2 = vals[0], hi2 = vals[vals.Count - 1];
                double span = hi2 - lo2;
                if (span <= 0) return false;
                int bins = 256;
                foreach (double v in vals)
                {
                    int bi = (int)((v - lo2) / span * (bins - 1));
                    if (bi < 0) bi = 0; if (bi > bins - 1) bi = bins - 1;
                    long key = bi;
                    int c;
                    hist.TryGetValue(key, out c);
                    hist[key] = c + 1;
                }
                var dvals = new double[bins];
                var freq = new int[bins];
                bool any = false;
                foreach (var kv in hist)
                {
                    dvals[kv.Key] = lo2 + span * kv.Key / (bins - 1.0);
                    freq[kv.Key] = kv.Value;
                    any = true;
                }
                if (!any) return false;

                IClassify cls = new NaturalBreaksClass();
                cls.SetHistogramData(dvals, freq);
                object cb = cls.ClassBreaks;
                double[] arr = cb as double[];
                if (arr == null || arr.Length < 2) return false;
                if (arr.Length > classes)                       // 引擎可能给出更多断点，取前 classes 个
                {
                    var t = new double[classes];
                    System.Array.Copy(arr, t, classes);
                    t[classes - 1] = arr[classes - 1];
                    arr = t;
                }
                breaks = arr;
                methodName = "自然断点(natural breaks / Jenks)";
                return true;
            }
            catch { return false; }
        }

        private static void RampColors(string ramp, out double lo, out double hi, out string name)
        {
            string r = (ramp ?? "").Trim().ToLowerInvariant();
            lo = 0; hi = 0; name = "默认（浅青→深青）";
            if (r.Length == 0) { lo = 0xCCFBF1; hi = 0x0F766E; return; }
            if (r.Contains("红") || r.Contains("red") || r.Contains("warm") || r.Contains("暖"))
            { lo = 0xFFF3C4; hi = 0xB91C1C; name = "黄→红（暖色）"; return; }
            if (r.Contains("绿") || r.Contains("green"))
            { lo = 0xDCFCE7; hi = 0x166534; name = "浅绿→深绿"; return; }
            if (r.Contains("蓝") || r.Contains("blue") || r.Contains("cool"))
            { lo = 0xDBEAFE; hi = 0x1E3A8A; name = "浅蓝→深蓝"; return; }
            if (r.Contains("橙") || r.Contains("orange"))
            { lo = 0xFFEDD5; hi = 0xC2410C; name = "浅橙→深橙"; return; }
            if (r.Contains("紫") || r.Contains("purple"))
            { lo = 0xEDE9FE; hi = 0x5B21B6; name = "浅紫→深紫"; return; }
            if (r.Contains("灰") || r.Contains("gray") || r.Contains("grey"))
            { lo = 0xF1F5F9; hi = 0x334155; name = "浅灰→深灰"; return; }
            lo = 0xCCFBF1; hi = 0x0F766E;
        }

        private static IRgbColor RampColor(double loHex, double hiHex, double t)
        {
            int lo = (int)loHex, hi = (int)hiHex;
            IRgbColor c = new RgbColorClass();
            c.Red = (int)Math.Round(Lerp((lo >> 16) & 0xFF, (hi >> 16) & 0xFF, t));
            c.Green = (int)Math.Round(Lerp((lo >> 8) & 0xFF, (hi >> 8) & 0xFF, t));
            c.Blue = (int)Math.Round(Lerp(lo & 0xFF, hi & 0xFF, t));
            return c;
        }

        // =====================================================================
        //  空间选择（Select By Location）—— 对应「选择 → 按位置选择」
        // =====================================================================

        /// <summary>
        /// 按位置选择：把 source 图层（有选中就只用选中要素，否则整层）的几何合并成一个整体，
        /// 再用 ISpatialFilter 去筛 target 图层。relation: intersect / within / contains。
        /// </summary>
        public static string SelectByLocation(AgentControl ui, string sourceRef, string targetRef, string relation)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                IMap map = mxDoc.FocusMap;

                ILayer src = ResolveLayer(map, sourceRef);
                if (src == null) return "未找到源图层: " + sourceRef;
                ILayer tgt = ResolveLayer(map, targetRef);
                if (tgt == null) return "未找到目标图层: " + targetRef;

                IFeatureLayer sfl = src as IFeatureLayer;
                IFeatureLayer tfl = tgt as IFeatureLayer;
                if (sfl == null || sfl.FeatureClass == null) return "源图层【" + src.Name + "】不是要素图层。";
                if (tfl == null || tfl.FeatureClass == null) return "目标图层【" + tgt.Name + "】不是要素图层。";

                IGeometry geom;
                string srcDesc;
                if (!TryBuildUnionGeometry(sfl, out geom, out srcDesc))
                    return "构建源几何失败: " + srcDesc;

                esriSpatialRelEnum rel = esriSpatialRelEnum.esriSpatialRelIntersects;
                string r = (relation ?? "").Trim().ToLowerInvariant();
                if (r.StartsWith("within") || r.Contains("内")) rel = esriSpatialRelEnum.esriSpatialRelWithin;
                else if (r.StartsWith("contain") || r.Contains("包含")) rel = esriSpatialRelEnum.esriSpatialRelContains;
                else if (r.StartsWith("touch") || r.Contains("接触")) rel = esriSpatialRelEnum.esriSpatialRelTouches;
                else if (r.StartsWith("overlap") || r.Contains("重叠")) rel = esriSpatialRelEnum.esriSpatialRelOverlaps;
                else if (r.StartsWith("cross") || r.Contains("穿越")) rel = esriSpatialRelEnum.esriSpatialRelCrosses;

                IFeatureClass tfc = tfl.FeatureClass;
                ISpatialFilter sf = new SpatialFilterClass();
                sf.Geometry = geom;
                sf.SpatialRel = rel;
                sf.GeometryField = tfc.ShapeFieldName;
                sf.WhereClause = "";

                ISelectionSet sel = null;
                try
                {
                    sel = tfc.Select(sf, esriSelectionType.esriSelectionTypeHybrid,
                                     esriSelectionOption.esriSelectionOptionOnlyOne, null);
                }
                catch (Exception ex)
                {
                    return "按位置选择失败: " + ex.Message;
                }
                IFeatureSelection fs = tfl as IFeatureSelection;
                if (fs == null) return "目标图层【" + tgt.Name + "】不支持选择集。";
                fs.SelectionSet = sel;
                fs.SelectionChanged();
                try { mxDoc.ActiveView.PartialRefresh(esriViewDrawPhase.esriViewGeoSelection, null, null); } catch { }

                int n = sel == null ? 0 : sel.Count;
                string relName = rel == esriSpatialRelEnum.esriSpatialRelWithin ? "位于…之内"
                               : rel == esriSpatialRelEnum.esriSpatialRelContains ? "包含…"
                               : rel == esriSpatialRelEnum.esriSpatialRelTouches ? "接触…"
                               : rel == esriSpatialRelEnum.esriSpatialRelOverlaps ? "与…重叠"
                               : rel == esriSpatialRelEnum.esriSpatialRelCrosses ? "与…交叉" : "与…相交";
                return "按位置选择完成：在【" + tgt.Name + "】中选出 " + n + " 个要素"
                       + "（" + relName + "【" + src.Name + "】，" + srcDesc + "）。";
            });
        }

        /// <summary>把图层（优先选中要素）合并成一个几何，用于空间过滤。</summary>
        private static bool TryBuildUnionGeometry(IFeatureLayer fl, out IGeometry geom, out string desc)
        {
            geom = null;
            desc = "";
            try
            {
                IFeatureClass fc = fl.FeatureClass;
                IFeatureSelection fs = fl as IFeatureSelection;
                ISelectionSet ss = fs == null ? null : fs.SelectionSet;
                bool useSelection = ss != null && ss.Count > 0;

                IGeometryBag bag = new GeometryBagClass();
                IGeometryCollection gc = (IGeometryCollection)bag;
                object missing = Type.Missing;

                int taken = 0;
                if (useSelection)
                {
                    IEnumIDs ids = ss.IDs;
                    int id;
                    while ((id = ids.Next()) != -1 && taken < 2000)
                    {
                        IFeature f = fc.GetFeature(id);
                        if (f == null) continue;
                        IGeometry g = f.ShapeCopy;
                        if (g != null && !g.IsEmpty) { gc.AddGeometry(g, ref missing, ref missing); taken++; }
                    }
                }
                else
                {
                    IFeatureCursor cur = fc.Search(null, false);
                    try
                    {
                        IFeature f;
                        while ((f = cur.NextFeature()) != null && taken < 2000)
                        {
                            IGeometry g = f.ShapeCopy;
                            if (g != null && !g.IsEmpty) { gc.AddGeometry(g, ref missing, ref missing); taken++; }
                        }
                    }
                    finally { try { Marshal.ReleaseComObject(cur); } catch { } }
                }

                if (taken == 0) { desc = "源图层没有可用要素"; return false; }

                if (taken == 1)
                {
                    geom = gc.get_Geometry(0);
                    desc = "用 1 个要素";
                }
                else
                {
                    IGeometry first = gc.get_Geometry(0);
                    IGeometry host = (IGeometry)((IClone)first).Clone();
                    ITopologicalOperator topo = host as ITopologicalOperator;
                    if (topo == null) { desc = "几何不支持拓扑运算"; return false; }
                    topo.ConstructUnion((IEnumGeometry)bag);
                    geom = host;
                    desc = "合并 " + taken + " 个要素" + (taken >= 2000 ? "（已达上限 2000）" : "");
                }
                if (useSelection) desc += "（取自选择集）";
                return geom != null && !geom.IsEmpty;
            }
            catch (Exception ex) { desc = ex.Message; return false; }
        }

        // =====================================================================
        //  选中要素读取 / 缩放到选中
        // =====================================================================

        public static string GetSelected(AgentControl ui, string layerRef, int maxRows)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";
                IFeatureSelection fs = fl as IFeatureSelection;
                ISelectionSet ss = fs == null ? null : fs.SelectionSet;
                if (ss == null || ss.Count == 0) return "图层【" + layer.Name + "】当前没有选中要素。";
                if (maxRows <= 0 || maxRows > 100) maxRows = 20;

                IFeatureClass fc = fl.FeatureClass;
                var names = new List<string>();
                var idxs = new List<int>();
                for (int i = 0; i < fc.Fields.FieldCount; i++)
                {
                    IField fd = fc.Fields.get_Field(i);
                    if (fd.Type == esriFieldType.esriFieldTypeGeometry || fd.Type == esriFieldType.esriFieldTypeBlob) continue;
                    names.Add(fd.Name);
                    idxs.Add(i);
                    if (names.Count >= 10) break;
                }

                var sb = new StringBuilder();
                sb.AppendLine("图层【" + layer.Name + "】选中 " + ss.Count + " 个要素，显示前 " + Math.Min(maxRows, ss.Count) + " 个：");
                IEnumIDs ids = ss.IDs;
                int id, shown = 0;
                while ((id = ids.Next()) != -1 && shown < maxRows)
                {
                    IFeature f = fc.GetFeature(id);
                    if (f == null) continue;
                    shown++;
                    var vals = new List<string>();
                    for (int k = 0; k < idxs.Count; k++) vals.Add(names[k] + "=" + Fmt(f.get_Value(idxs[k])));
                    sb.AppendLine("  行" + shown + ": " + string.Join("  ", vals.ToArray()));
                }
                return sb.ToString();
            });
        }

        public static string ZoomToSelected(AgentControl ui, string layerRef)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IFeatureSelection fs = layer as IFeatureSelection;
                ISelectionSet ss = fs == null ? null : fs.SelectionSet;
                if (ss == null || ss.Count == 0) return "图层【" + layer.Name + "】没有选中要素，无法缩放。";
                IFeatureLayer fl = layer as IFeatureLayer;
                if (fl == null || fl.FeatureClass == null) return "图层【" + layer.Name + "】不是要素图层。";

                IEnvelope env = null;
                IEnumIDs ids = ss.IDs;
                int id;
                while ((id = ids.Next()) != -1)
                {
                    IFeature f = fl.FeatureClass.GetFeature(id);
                    if (f == null) continue;
                    IEnvelope e = f.Shape == null ? null : f.Shape.Envelope;
                    if (e == null || e.IsEmpty) continue;
                    if (env == null)
                    {
                        env = new EnvelopeClass();
                        env.PutCoords(e.XMin, e.YMin, e.XMax, e.YMax);
                        try { env.SpatialReference = e.SpatialReference; } catch { }
                    }
                    else env.Union(e);
                }
                if (env == null || env.IsEmpty) return "无法计算选中要素范围。";
                env.Expand(1.06, 1.06, true);
                IActiveView view = mxDoc.ActiveView;
                view.Extent = env;
                view.Refresh();
                return "已缩放到图层【" + layer.Name + "】的 " + ss.Count + " 个选中要素。";
            });
        }

        // =====================================================================
        //  图层顺序 / 书签 / 视图范围 / 比例尺
        // =====================================================================

        public static string MoveLayer(AgentControl ui, string layerRef, string position)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                ILayer layer;
                string err = ResolveMap(mxDoc, layerRef, out layer);
                if (err != null) return err;
                IMap map = mxDoc.FocusMap;
                int cur = -1;
                for (int i = 0; i < map.LayerCount; i++)
                    if (ReferenceEquals(map.get_Layer(i), layer)) { cur = i; break; }
                if (cur < 0) return "未能在数据框中定位图层【" + layer.Name + "】。";

                string p = (position ?? "").Trim().ToLowerInvariant();
                int to;
                if (p.StartsWith("up") || p.Contains("上")) to = cur + 1;
                else if (p.StartsWith("down") || p.Contains("下")) to = cur - 1;
                else if (p.StartsWith("top") || p.Contains("顶")) to = map.LayerCount - 1;
                else if (p.StartsWith("bottom") || p.Contains("底")) to = 0;
                else if (int.TryParse(p, out to)) { /* 允许直接给目标序号（1 基） */ to = to - 1; }
                else return "位置参数无法识别：up / down / top / bottom 或目标序号。";

                if (to < 0) to = 0;
                if (to > map.LayerCount - 1) to = map.LayerCount - 1;
                if (to == cur) return "图层【" + layer.Name + "】已经在该位置，无需移动。";
                map.MoveLayer(layer, to);
                RefreshAfterSymbolChange(mxDoc);
                return "已将图层【" + layer.Name + "】移动到位置 " + (to + 1) + "（共 " + map.LayerCount + " 层，序号越大越靠上）。";
            });
        }

        public static string AddBookmark(AgentControl ui, string name)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                if (string.IsNullOrWhiteSpace(name)) return "请给出书签名称。";
                IMapBookmarks mb = mxDoc.FocusMap as IMapBookmarks;
                if (mb == null) return "当前地图不支持书签。";
                IAOIBookmark bk = new AOIBookmarkClass();
                bk.Location = mxDoc.ActiveView.Extent;
                bk.Name = name.Trim();
                mb.AddBookmark(bk);
                return "已添加书签【" + bk.Name + "】（记录当前视图范围）。";
            });
        }

        public static string ListBookmarks(AgentControl ui)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                IMapBookmarks mb = mxDoc.FocusMap as IMapBookmarks;
                if (mb == null) return "当前地图不支持书签。";
                var names = new List<string>();
                IEnumSpatialBookmark en = mb.Bookmarks;
                en.Reset();
                ISpatialBookmark sb;
                while ((sb = en.Next()) != null && names.Count < 200) names.Add(sb.Name);
                if (names.Count == 0) return "当前数据框没有任何书签（可用 add_bookmark 新建）。";
                var sb2 = new StringBuilder("当前数据框共有 " + names.Count + " 个书签：\n");
                for (int i = 0; i < names.Count; i++) sb2.AppendLine("  " + (i + 1) + ". " + names[i]);
                return sb2.ToString();
            });
        }

        public static string GotoBookmark(AgentControl ui, string name)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                if (string.IsNullOrWhiteSpace(name)) return "请给出书签名称。";
                IMapBookmarks mb = mxDoc.FocusMap as IMapBookmarks;
                if (mb == null) return "当前地图不支持书签。";
                string want = name.Trim();
                IEnumSpatialBookmark en = mb.Bookmarks;
                en.Reset();
                ISpatialBookmark sb;
                while ((sb = en.Next()) != null)
                {
                    if (sb.Name != null && sb.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sb.ZoomTo(mxDoc.FocusMap);
                        try { mxDoc.ActiveView.Refresh(); } catch { }
                        return "已跳转到书签【" + sb.Name + "】。";
                    }
                }
                return "未找到书签: " + want + "（可用 list_bookmarks 查看）。";
            });
        }

        public static string GetMapExtent(AgentControl ui)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                IActiveView view = mxDoc.ActiveView;
                IEnvelope e = view.Extent;
                IMap map = mxDoc.FocusMap;
                var sb = new StringBuilder();
                sb.AppendLine("当前视图：【" + (map.Name ?? "") + "】，比例尺 1:" + map.MapScale.ToString("#,0"));
                sb.AppendLine("  范围 X: " + e.XMin.ToString("0.###") + " ~ " + e.XMax.ToString("0.###"));
                sb.AppendLine("  范围 Y: " + e.YMin.ToString("0.###") + " ~ " + e.YMax.ToString("0.###"));
                try { if (map.SpatialReference != null) sb.AppendLine("  坐标系: " + map.SpatialReference.Name); } catch { }
                return sb.ToString();
            });
        }

        public static string SetMapExtent(AgentControl ui, double xmin, double ymin, double xmax, double ymax)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                if (xmax <= xmin || ymax <= ymin) return "范围无效：需要 xmin<xmax 且 ymin<ymax。";
                IEnvelope env = new EnvelopeClass();
                env.PutCoords(xmin, ymin, xmax, ymax);
                IActiveView view = mxDoc.ActiveView;
                view.Extent = env;
                view.Refresh();
                return "已将视图范围设为 X[" + xmin.ToString("0.###") + ", " + xmax.ToString("0.###")
                       + "] Y[" + ymin.ToString("0.###") + ", " + ymax.ToString("0.###") + "]。";
            });
        }

        public static string ZoomToScale(AgentControl ui, double scale)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                if (scale <= 0) return "比例尺必须大于 0，例如 50000 表示 1:50000。";
                IMap map = mxDoc.FocusMap;
                IActiveView view = mxDoc.ActiveView;
                IEnvelope e = view.Extent;
                double cx = (e.XMin + e.XMax) / 2, cy = (e.YMin + e.YMax) / 2;
                map.MapScale = scale;                       // 先设比例尺
                IEnvelope ne = view.Extent;
                ne.CenterAt(MakePoint(cx, cy, map.SpatialReference));
                view.Extent = ne;
                view.Refresh();
                return "已缩放到 1:" + scale.ToString("#,0") + "（保持原有中心点）。";
            });
        }

        private static IPoint MakePoint(double x, double y, ISpatialReference sr)
        {
            IPoint p = new PointClass();
            p.PutCoords(x, y);
            if (sr != null) { try { p.SpatialReference = sr; } catch { } }
            return p;
        }

        // =====================================================================
        //  地图导出（图片 / PDF）—— 对应「文件 → 导出地图」
        // =====================================================================

        /// <summary>target: data（当前数据框视图）| layout（页面布局）。按扩展名选择导出器。</summary>
        public static string ExportMap(AgentControl ui, string path, int width, int height, int dpi, string target)
        {
            return Run(ui, () =>
            {
                IMxDocument mxDoc = GetMxDocument();
                if (mxDoc == null) return "无法获取地图文档。";
                if (string.IsNullOrWhiteSpace(path)) return "请给出导出路径，例如 D:\\out\\map.png。";
                path = path.Trim().Trim('"');
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    try { System.IO.Directory.CreateDirectory(dir); }
                    catch (Exception ex) { return "无法创建目录: " + ex.Message; }
                }
                string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext.Length == 0) { ext = ".png"; path += ".png"; }
                if (width <= 0) width = 1600;
                if (height <= 0) height = 1200;
                if (dpi <= 0) dpi = 150;

                IActiveView view;
                if ((target ?? "").Trim().ToLowerInvariant().StartsWith("lay"))
                {
                    view = mxDoc.PageLayout as IActiveView;
                    if (view == null) return "当前文档没有页面布局，无法导出 layout。";
                }
                else view = mxDoc.ActiveView;

                IExport exporter;
                if (ext == ".pdf") exporter = new ExportPDFClass();
                else if (ext == ".jpg" || ext == ".jpeg") exporter = new ExportJPEGClass();
                else if (ext == ".tif" || ext == ".tiff") exporter = new ExportTIFFClass();
                else if (ext == ".emf") exporter = new ExportEMFClass();
                else { exporter = new ExportPNGClass(); ext = ".png"; }

                IEnvelope px = new EnvelopeClass();
                px.PutCoords(0, 0, width, height);
                exporter.PixelBounds = px;
                exporter.Resolution = dpi;
                exporter.ExportFileName = path;

                try
                {
                    var pe = new PrintAndExportClass();
                    pe.Export(view, exporter, dpi, false, null);
                }
                catch (Exception ex)
                {
                    return "导出失败: " + ex.Message + "\n（提示：PDF 建议 dpi>=300；路径不要以空格结尾。）";
                }
                finally { try { exporter.Cleanup(); } catch { } }

                long size = 0;
                try { size = new System.IO.FileInfo(path).Length; } catch { }
                return "已导出地图（" + ext.TrimStart('.').ToUpperInvariant() + "）: " + path
                       + "\n  尺寸: " + width + "×" + height + " 像素，分辨率 " + dpi + " dpi，文件 "
                       + (size / 1024) + " KB";
            });
        }

        // =====================================================================
        //  Geoprocessing：一次打通整个 ArcToolbox
        // =====================================================================

        /// <summary>
        /// 执行任意 ArcToolbox 工具（Geoprocessor）。
        /// 参数按位置顺序给出，全部以字符串传入 —— GP 会自行转换（"100 Meters"、字段名、路径等）。
        /// 这是「Agent 还能干什么」的兜底答案：缓冲区、裁剪、相交、投影、空间连接、
        /// 字段计算、要素转点、合并、溶解……几千个工具都不用单独写代码。
        /// </summary>
        public static string RunGp(AgentControl ui, string tool, string[] args)
        {
            return Run(ui, () =>
            {
                if (string.IsNullOrWhiteSpace(tool))
                    return "请给出工具名（需带工具箱别名），例如 Buffer_analysis。可用 gp_tools 查询常用工具。";
                string t = tool.Trim();

                var gp = new ESRI.ArcGIS.Geoprocessor.Geoprocessor();
                try { gp.OverwriteOutput = true; } catch { }
                try { gp.AddOutputsToMap = true; } catch { }
                try { gp.LogHistory = false; } catch { }
                try { gp.SetEnvironmentValue("addOutputsToMap", true); } catch { }

                IVariantArray p = new VarArrayClass();
                var echo = new List<string>();
                if (args != null)
                {
                    foreach (string a in args)
                    {
                        string v = a ?? "";
                        p.Add(v);
                        if (echo.Count < 8) echo.Add("[" + echo.Count + "] " + (v.Length > 60 ? v.Substring(0, 60) + "…" : v));
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("执行 Geoprocessing 工具: " + t + "（参数 " + p.Count + " 个）");
                if (echo.Count > 0) sb.AppendLine("  " + string.Join("  ", echo.ToArray()));

                object ret;
                try
                {
                    ret = gp.Execute(t, p, null);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("工具执行失败: " + ex.Message);
                    AppendGpMessages(gp, sb);
                    if (t.IndexOf('_') < 0)
                        sb.AppendLine("提示：工具名必须带工具箱别名，例如 Buffer_analysis、Clip_analysis、CalculateField_management。");
                    return sb.ToString();
                }

                AppendGpMessages(gp, sb);
                if (ret != null)
                {
                    string rv = ret.ToString();
                    if (!string.IsNullOrWhiteSpace(rv) && rv != "System.__ComObject") sb.AppendLine("返回: " + rv);
                }
                sb.AppendLine("执行完成。");
                return sb.ToString();
            });
        }

        private static void AppendGpMessages(ESRI.ArcGIS.Geoprocessor.Geoprocessor gp, StringBuilder sb)
        {
            try
            {
                int n = gp.MessageCount;
                if (n <= 0) return;
                int start = n > 24 ? n - 24 : 0;
                if (start > 0) sb.AppendLine("  …（前 " + start + " 条消息略）");
                for (int i = start; i < n; i++)
                {
                    int sev = 0;
                    try { sev = gp.GetSeverity(i); } catch { }
                    string prefix = sev == 0 ? "信息" : sev == 1 ? "警告" : "错误";
                    string msg;
                    try { msg = gp.GetMessage(i); } catch { msg = "(无法读取)"; }
                    if (string.IsNullOrWhiteSpace(msg)) continue;
                    sb.AppendLine("  [" + prefix + "] " + msg.Trim());
                }
            }
            catch { }
        }

        /// <summary>常用 GP 工具目录（工具名 + 中文说明），供模型挑选与自查。</summary>
        private static readonly string[,] GpCatalog =
        {
            { "Buffer_analysis",           "缓冲区：参数 输入要素, 输出要素类, 距离(如 100 Meters)" },
            { "Clip_analysis",             "裁剪：参数 输入要素, 裁剪要素, 输出要素类" },
            { "Intersect_analysis",        "相交：参数 输入要素列表(;分隔), 输出要素类, 连接属性 ALL" },
            { "Union_analysis",            "联合：参数 输入要素列表, 输出要素类, ALL" },
            { "Erase_analysis",            "擦除：参数 输入要素, 擦除要素, 输出要素类" },
            { "Identity_analysis",         "标识叠加：参数 输入要素, 标识要素, 输出要素类, ALL" },
            { "SymDiff_analysis",          "对称差：参数 输入要素, 更新要素, 输出要素类" },
            { "SpatialJoin_analysis",      "空间连接：参数 目标要素, 连接要素, 输出要素类, JOIN_ONE_TO_ONE, INTERSECT" },
            { "Near_analysis",             "近邻分析：参数 输入要素, 邻近要素, 搜索半径(可空)" },
            { "Dissolve_management",       "溶解：参数 输入要素, 输出要素类, [溶解字段, ...]" },
            { "Merge_management",          "合并：参数 输入要素列表, 输出要素类" },
            { "Append_management",         "追加：参数 输入要素, 目标要素, NO_TEST" },
            { "Project_management",        "投影：参数 输入要素, 输出要素类, 目标坐标系(如 GCS_WGS_1984)" },
            { "ProjectRaster_management",  "栅格投影：参数 输入栅格, 输出栅格, 目标坐标系, 重采样" },
            { "FeatureToPoint_management", "要素转点：参数 输入要素, 输出要素类, CENTROID" },
            { "FeatureToPolygon_management","要素转面：参数 输入要素, 输出要素类" },
            { "FeatureToLine_management",  "要素转线：参数 输入要素, 输出要素类" },
            { "PolygonToLine_management",  "面转线：参数 输入要素, 输出要素类, IDENTIFY_NEIGHBORS" },
            { "Select_analysis",           "按属性提取：参数 输入要素, 输出要素类, 表达式" },
            { "SelectLayerByAttribute_management", "按属性选择工具：参数 图层名, NEW_SELECTION, SQL" },
            { "SelectLayerByLocation_management",  "按位置选择工具：参数 图层名, INTERSECT, 源图层名" },
            { "AddField_management",       "添加字段：参数 输入表, 字段名, DOUBLE, 精度, 小数位" },
            { "DeleteField_management",    "删除字段：参数 输入表, 字段名" },
            { "AlterField_management",     "修改字段：参数 输入表, 字段名, 新名, 新类型" },
            { "CalculateField_management", "字段计算器：参数 输入表, 字段名, 表达式(VB 语法如 !亩!*0.0667), PYTHON_9.3" },
            { "CalculateStatistics_management", "计算栅格统计：参数 输入栅格" },
            { "SummaryStatistics_management","汇总统计：参数 输入表, 输出表, 统计字段;统计类型(SUM;MEAN), 分组字段" },
            { "Frequency_management",      "频数统计：参数 输入表, 输出表, 字段" },
            { "Statistics_analysis",       "要素统计：参数 输入要素, 输出表, 统计字段 类型, 分组字段" },
            { "JoinField_management",      "字段连接：参数 输入表, 连接字段, 连接表, 连接字段, 传递字段" },
            { "AddJoin_management",        "添加连接：参数 图层名, 连接字段, 连接表, 连接字段" },
            { "RemoveJoin_management",     "移除连接：参数 图层名, 连接名或 ALL" },
            { "MakeFeatureLayer_management","创建要素图层：参数 输入要素, 输出图层名, [SQL]" },
            { "MakeXYEventLayer_management","XY 事件图层：参数 表格, X字段, Y字段, 输出图层" },
            { "XYTableToPoint_management", "XY 表转点：参数 输入表, 输出要素类, X字段, Y字段, 坐标系" },
            { "CreateFishnet_management",  "创建渔网：参数 输出要素类, 原点, 对角点, 行数, 列数" },
            { "CreateBuffers_analysis",    "多点缓冲：参数 输入要素, 输出要素类, 距离" },
            { "MinimumBoundingGeometry_management", "最小包围几何：参数 输入要素, 输出要素类, RECTANGLE" },
            { "FeatureEnvelopeToPolygon_management","要素包络转面：参数 输入要素, 输出要素类" },
            { "MultipartToSinglepart_management",   "多部件转单部件：参数 输入要素, 输出要素类" },
            { "Dice_management",           "切割：参数 输入要素, 输出要素类, 顶点数" },
            { "Eliminate_management",      "消除：参数 输入要素, 输出要素类, LENGTH" },
            { "SimplifyPolygon_cartography","简化面：参数 输入要素, 输出要素类, 容差, BEND_SIMPLIFY" },
            { "SmoothPolygon_cartography", "平滑面：参数 输入要素, 输出要素类, 容差, PAEK" },
            { "AggregatePolygons_cartography","聚合面：参数 输入要素, 输出要素类, 距离, 最小面积" },
            { "ConvertLabelsToAnnotation_cartography", "注记转标注：参数 输入要素, 输出地理数据库, 比例尺" },
            { "Contour_3d",                "等值线：参数 输入栅格, 输出要素类, 等值距" },
            { "Slope_3d",                  "坡度：参数 输入栅格, 输出栅格" },
            { "Hillshade_3d",              "晕渲：参数 输入栅格, 输出栅格" },
            { "Aspect_3d",                 "坡向：参数 输入栅格, 输出栅格" },
            { "Viewshed_3d",               "视域：参数 输入栅格, 观察点, 输出栅格" },
            { "Idw_3d",                    "反距离权重插值：参数 输入点, 高程字段, 输出栅格" },
            { "Kriging_3d",                "克里金插值：参数 输入点, 高程字段, 输出栅格" },
            { "ExtractByMask_sa",          "按掩膜提取：参数 输入栅格, 掩膜, 输出栅格" },
            { "ZonalStatisticsAsTable_sa", "分区统计：参数 输入分区, 分区字段, 输入栅格, 输出表" },
            { "Reclassify_sa",             "重分类：参数 输入栅格, 重分类字段, 重映射, 输出栅格" },
            { "RasterToPolygon_conversion","栅格转面：参数 输入栅格, 输出要素类, NO_SIMPLIFY" },
            { "PolygonToRaster_conversion","面转栅格：参数 输入要素, 值字段, 输出栅格, 像元大小" },
            { "FeatureToRaster_conversion","要素转栅格：参数 输入要素, 字段, 输出栅格, 像元大小" },
            { "RasterToPoint_conversion",  "栅格转点：参数 输入栅格, 输出点" },
            { "PointsToLine_conversion",   "点集转线：参数 输入点, 输出线, 排序字段" },
            { "TableToExcel_conversion",   "表转 Excel：参数 输入表, 输出 xls" },
            { "ExcelToTable_conversion",   "Excel 转表：参数 输入 xls, 输出表" },
            { "TableToTable_conversion",   "表转表：参数 输入表, 输出表" },
            { "FeatureClassToFeatureClass_conversion", "要素类转要素类：参数 输入要素, 输出位置, 输出名, [SQL]" },
            { "FeatureClassToShapefile_conversion",    "导出 Shapefile：参数 输入要素, 输出文件夹" },
            { "KMLToLayer_conversion",     "KML 转图层：参数 输入 kml, 输出位置, 输出名" },
            { "LayerToKML_conversion",     "图层转 KML：参数 图层名, 输出 kmz, 比例尺" },
            { "CADToGeodatabase_conversion","CAD 转地理数据库：参数 输入 CAD, 输出 GDB, 数据集名, 坐标系" },
            { "CreateFileGDB_management",  "创建文件地理数据库：参数 输出位置, 名称" },
            { "CreateFeatureclass_management", "创建要素类：参数 输出位置, 名称, 几何类型, 模板, 坐标系" },
            { "AddXY_management",          "添加 XY 坐标：参数 输入要素" },
            { "RepairGeometry_management", "修复几何：参数 输入要素, DELETE_NULL" },
            { "CheckGeometry_management",  "检查几何：参数 输入要素, 输出表" },
            { "Sort_management",           "排序：参数 输入要素, 输出要素类, 排序字段 方向" },
            { "DeleteIdentical_management","删除重复：参数 输入要素, 字段列表" },
            { "FindIdentical_management",  "查找重复：参数 输入要素, 输出表, 字段列表" },
            { "TransposeFields_management","字段转置：参数 输入表, 输出表, 转置字段, 属性字段, 值字段" },
            { "PivotTable_management",     "数据透视：参数 输入表, 透视字段, 值字段, 输出表" },
        };

        /// <summary>按关键词查常用 GP 工具（返回工具名 + 中文说明）。</summary>
        public static string SearchGpTools(AgentControl ui, string keyword)
        {
            string k = (keyword ?? "").Trim().ToLowerInvariant();
            var hits = new List<string>();
            for (int i = 0; i < GpCatalog.GetLength(0); i++)
            {
                string name = GpCatalog[i, 0];
                string desc = GpCatalog[i, 1];
                if (k.Length == 0
                    || name.ToLowerInvariant().IndexOf(k, StringComparison.Ordinal) >= 0
                    || desc.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add("  · " + name + "  —— " + desc);
                }
                if (hits.Count >= 60) break;
            }
            if (hits.Count == 0)
                return "没有匹配【" + keyword + "】的常用工具。可直接用 run_gp 传任意 ArcToolbox 工具名（需带别名后缀，如 XXX_management）。";
            var sb = new StringBuilder();
            sb.AppendLine("匹配【" + (k.Length == 0 ? "全部常用" : keyword) + "】的 Geoprocessing 工具 " + hits.Count + " 个：");
            foreach (string h in hits) sb.AppendLine(h);
            sb.AppendLine("调用方式：run_gp(tool=\"Buffer_analysis\", params=[\"道路\", \"D:\\\\out\\\\buf.shp\", \"100 Meters\"])");
            return sb.ToString();
        }

        // =====================================================================
        //  颜色解析小工具
        // =====================================================================

        /// <summary>把「红 / 蓝色 / #RRGGBB」解析成 IColor；解析不出来时用给定的默认 RGB。</summary>
        private static IColor ParseColorOr(string colorText, int dr, int dg, int db, out string used)
        {
            used = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(colorText))
                {
                    string t = colorText.Trim();
                    string hex = t.TrimStart('#');
                    int r, g, b;
                    int parsed;
                    if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out parsed))
                    {
                        r = (parsed >> 16) & 0xFF; g = (parsed >> 8) & 0xFF; b = parsed & 0xFF;
                        used = "#" + hex.ToUpperInvariant();
                    }
                    else if (TryNamedColor(t.ToLowerInvariant(), out r, out g, out b)) used = t;
                    else { r = dr; g = dg; b = db; }

                    IRgbColor c = new RgbColorClass();
                    c.Red = r; c.Green = g; c.Blue = b;
                    return c;
                }
            }
            catch { }
            IRgbColor dc = new RgbColorClass();
            dc.Red = dr; dc.Green = dg; dc.Blue = db;
            return dc;
        }
    }
}
