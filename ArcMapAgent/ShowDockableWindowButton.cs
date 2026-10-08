using System;
using ESRI.ArcGIS.Desktop.AddIns;
using ESRI.ArcGIS.ArcMapUI;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Framework;

namespace ArcMapAgent
{
    /// <summary>
    /// 工具栏按钮：点击后显示 Agent 面板。类名必须与 Config.esriaddinx 中 Button 的 class 属性一致。
    /// 注意：Desktop.AddIns.Button 与 Framework 命名空间下的 Button 重名，此处用完全限定名消除歧义。
    /// </summary>
    public class ShowDockableWindowButton : ESRI.ArcGIS.Desktop.AddIns.Button
    {
        public ShowDockableWindowButton()
        {
            // 加载期日志：与「扩展已加载」一起，作为加载项确实被加载的硬证据
            try { AgentLog.Append("[add-in] 工具栏按钮已实例化"); } catch { }
        }

        protected override void OnClick()
        {
            AgentExtension.Diag("Button.OnClick");
            // Hook 在运行时是 IMxApplication（实现了 IApplication / IDockableWindowManager）
            AppContext.Application = this.Hook as IApplication;

            IDockableWindowManager dwm = AppContext.Application as IDockableWindowManager;
            if (dwm != null)
            {
                // 复用同一套 UID 解析（含诊断日志），与扩展自动显示逻辑保持一致
                IDockableWindow dw = AgentExtension.ResolveDockableWindow(dwm);
                if (dw != null)
                {
                    try { dw.Show(true); AgentExtension.Diag("Button.OnClick: shown"); }
                    catch (Exception ex) { AgentExtension.Diag("Button.OnClick Show threw: " + ex.Message); }
                }
                else AgentExtension.Diag("Button.OnClick: ResolveDockableWindow returned null");
            }
            else AgentExtension.Diag("Button.OnClick: no IDockableWindowManager");
        }
    }
}
