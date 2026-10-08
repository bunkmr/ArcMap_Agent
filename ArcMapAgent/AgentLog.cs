using System;
using System.IO;

namespace ArcMapAgent
{
    /// <summary>
    /// 面板日志：把对话/工具输出同时写到 %APPDATA%\ArcMapAgent\panel.log。
    /// 用途：① 出问题时排查；② 自动化回归测试（UI Automation 读不到多行 TextBox 的值，改用日志文件取证）。
    /// </summary>
    internal static class AgentLog
    {
        private static readonly object _lock = new object();

        public static string FilePath
        {
            get { return Path.Combine(AppConfig.DirPath, "panel.log"); }
        }

        public static void Append(string text)
        {
            if (text == null) return;
            try
            {
                Directory.CreateDirectory(AppConfig.DirPath);
                lock (_lock)
                    File.AppendAllText(FilePath,
                        "[" + DateTime.Now.ToString("HH:mm:ss") + "] " +
                        text.Replace("\r\n", " | ").Replace("\n", " | ") + "\r\n");
            }
            catch { /* 日志失败不影响主流程 */ }
        }
    }
}
