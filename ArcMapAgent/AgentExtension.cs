using System;
using ESRI.ArcGIS.Desktop.AddIns;
using ESRI.ArcGIS.Framework;
using ESRI.ArcGIS.esriSystem;

namespace ArcMapAgent
{
    /// <summary>
    /// ArcMap 扩展（Add-In Extension）：随 ArcMap 启动自动加载，用于
    ///   ① 尽早把 IApplication 写入 AppContext，使工具不再依赖「用户先点一次按钮」；
    ///   ② 写一条「加载成功」日志 —— 判定加载项是否真的被加载，比截图更硬
    ///      （面板日志见 %APPDATA%\ArcMapAgent\panel.log）；
    ///   ③ 自动显示 Agent 面板。
    ///
    /// 注意：ArcMap 对 OnStartup 里逃逸的异常会禁用整个加载项，因此这里全部 try/catch，
    /// 失败只记日志，绝不让扩展把插件拖崩。
    /// </summary>
    public class AgentExtension : Extension
    {
        private const string DockableWindowId = "ArcMapAgent_AgentDockableWindow";

        /// <summary>诊断文件：不依赖 AppConfig/AgentLog，用于定位「扩展到底有没有被实例化」。</summary>
        internal static readonly string DiagPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcMapAgent", "ext.diag");

        /// <summary>静态构造标记：只要类型被触碰就会留下痕迹。</summary>
        static AgentExtension()
        {
            Diag("static ctor");
        }

        internal static void Diag(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DiagPath));
                System.IO.File.AppendAllText(DiagPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        protected override void OnStartup()
        {
            Diag("OnStartup enter");
            try
            {
                AppContext.Application = this.Hook as IApplication;
                Diag("Hook -> IApplication: " + (AppContext.Application == null ? "null" : "ok"));
                AgentLog.Append("[add-in] ArcMap Agent 扩展已加载 v" + McpBridge.Version
                                + " · app=" + (AppContext.Application == null ? "null" : "ok"));
            }
            catch (Exception ex)
            {
                Diag("startup log threw: " + ex.GetType().Name + ": " + ex.Message);
                try { AgentLog.Append("[add-in] 扩展初始化异常: " + ex.Message); } catch { }
            }

            try { ShowPanel(); Diag("ShowPanel done"); }
            catch (Exception ex)
            {
                Diag("ShowPanel threw: " + ex.GetType().Name + ": " + ex.Message);
                try { AgentLog.Append("[add-in] 自动显示面板失败: " + ex.Message); } catch { }
            }
        }

        protected override void OnShutdown()
        {
            Diag("OnShutdown");
            try { McpBridge.Get().Stop(); } catch { }
            try { AgentLog.Append("[add-in] 扩展已卸载"); } catch { }
        }

        /// <summary>把停靠面板显示出来（已显示则不动）。</summary>
        private static void ShowPanel()
        {
            var app = AppContext.Application;
            if (app == null) { Diag("ShowPanel: app null"); return; }

            var dwm = app as IDockableWindowManager;
            if (dwm == null) { Diag("ShowPanel: no IDockableWindowManager"); return; }

            IDockableWindow dw = ResolveDockableWindow(dwm);
            if (dw == null) { Diag("ShowPanel: all UID forms failed"); return; }

            Diag("ShowPanel: got dockable window");
            bool visible = false;
            try { visible = dw.IsVisible(); } catch { }
            if (!visible) { dw.Show(true); Diag("ShowPanel: dw.Show(true) returned"); }
        }

        /// <summary>
        /// 依次尝试几种 UID 形式取得停靠窗口，并记录哪一种成功。
        /// 加进日志是为了让 ext.diag 自己说明「正确的 UID 长什么样」，
        /// 避免以后再靠猜（Add-In 的 GUID 前缀形式 / 相对类名形式都试过）。
        /// </summary>
        internal static IDockableWindow ResolveDockableWindow(IDockableWindowManager dwm)
        {
            string[] candidates =
            {
                DockableWindowId,                                              // ArcMapAgent_AgentDockableWindow
                "{6F3A9C52-8B14-4E2D-9A7B-1D5C0E4F83A6}_" + DockableWindowId,   // <AddInID>_<id>
                "{6F3A9C52-8B14-4E2D-9A7B-1D5C0E4F83A6}",                      // 仅 AddInID
                "ArcMapAgent.AgentDockableWindow"                              // 完整类名
            };

            foreach (var cand in candidates)
            {
                try
                {
                    var uid = new UIDClass();
                    uid.Value = cand;
                    var dw = dwm.GetDockableWindow(uid);
                    if (dw != null) { Diag("ResolveDockableWindow OK: " + cand); return dw; }
                    Diag("ResolveDockableWindow null: " + cand);
                }
                catch (Exception ex)
                {
                    Diag("ResolveDockableWindow threw [" + cand + "]: " + ex.Message);
                }
            }
            return null;
        }
    }
}
