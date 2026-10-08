using ESRI.ArcGIS.Framework;

namespace ArcMapAgent
{
    /// <summary>
    /// 持有 ArcMap 应用程序实例（IApplication）。
    /// Add-In 组件（按钮 / 停靠窗）在 OnClick / OnCreateChild 中通过基类 Hook 取得并写入此处，
    /// 供 ArcMapTools 等静态工具读取，从而避免使用本版本并不存在的静态
    /// ArcMap.Application 访问器。
    /// </summary>
    public static class AppContext
    {
        public static IApplication Application { get; set; }
    }
}
