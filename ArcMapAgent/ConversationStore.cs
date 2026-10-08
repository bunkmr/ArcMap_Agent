using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ArcMapAgent
{
    /// <summary>一段被持久化的对话。</summary>
    public class ConversationRecord
    {
        public string Title { get; set; }
        public string Created { get; set; }
        public int MessageCount { get; set; }
        public List<ChatMessage> Messages { get; set; }

        public ConversationRecord()
        {
            Title = "";
            Created = "";
            Messages = new List<ChatMessage>();
            MessageCount = 0;
        }
    }

    /// <summary>
    /// 对话历史存储：每条会话一个 JSON 文件，落在 %APPDATA%\ArcMapAgent\conversations\。
    /// 对应 QGIS Agent 的「历史」页；ArcMap 侧做成轻量版本（保存/加载/删除当前会话）。
    /// </summary>
    public static class ConversationStore
    {
        public static string DirPath { get { return Path.Combine(AppConfig.DirPath, "conversations"); } }

        public static List<ConversationRecord> List()
        {
            var list = new List<ConversationRecord>();
            try
            {
                if (!Directory.Exists(DirPath)) return list;
                var jss = new JavaScriptSerializer();
                foreach (var f in Directory.GetFiles(DirPath, "*.json"))
                {
                    try
                    {
                        var rec = jss.Deserialize<ConversationRecord>(File.ReadAllText(f));
                        if (rec != null)
                        {
                            if (rec.Messages == null) rec.Messages = new List<ChatMessage>();
                            rec.MessageCount = rec.Messages.Count;
                            rec.Title = string.IsNullOrEmpty(rec.Title) ? Path.GetFileNameWithoutExtension(f) : rec.Title;
                            list.Add(rec);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            list.Sort((a, b) => string.CompareOrdinal(b.Created ?? "", a.Created ?? ""));
            return list;
        }

        public static string Save(string title, List<ChatMessage> messages)
        {
            try
            {
                if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
                var rec = new ConversationRecord
                {
                    Title = string.IsNullOrEmpty(title) ? "未命名对话" : title,
                    Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Messages = messages ?? new List<ChatMessage>()
                };
                string safe = MakeSafe(rec.Title);
                string file = Path.Combine(DirPath,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + safe + ".json");
                File.WriteAllText(file, new JavaScriptSerializer().Serialize(rec));
                return file;
            }
            catch { return null; }
        }

        private static string MakeSafe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            var r = sb.ToString().Trim();
            return r.Length > 40 ? r.Substring(0, 40) : (r.Length == 0 ? "chat" : r);
        }
    }
}
