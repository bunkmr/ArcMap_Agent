using ESRI.ArcGIS.Desktop.AddIns;
using ESRI.ArcGIS.Framework;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 可停靠面板。类名必须与 Config.esriaddinx 中 DockableWindow 的 class 属性一致。
    /// Add-In 框架通过反射实例化此类，无需任何特性标注。
    /// 正确的 UI 挂载方式：覆写 OnCreateChild()，返回子控件（WinForms UserControl）的窗口句柄，
    /// 框架会把该句柄 SetParent 到停靠窗口客户区。
    /// </summary>
    public class AgentDockableWindow : ESRI.ArcGIS.Desktop.AddIns.DockableWindow
    {
        private AgentControl m_control;

        public AgentDockableWindow()
        {
            AgentExtension.Diag("DockableWindow ctor");
        }

        protected override System.IntPtr OnCreateChild()
        {
            AgentExtension.Diag("OnCreateChild enter");
            // Hook 在运行时是 IMxApplication；捕获应用程序实例供工具使用
            AppContext.Application = this.Hook as IApplication;

            m_control = new AgentControl();
            // 强制创建句柄，避免返回尚未创建的 Handle
            m_control.CreateControl();
            AgentExtension.Diag("OnCreateChild -> handle " + m_control.Handle);
            return m_control.Handle;
        }
    }
}
