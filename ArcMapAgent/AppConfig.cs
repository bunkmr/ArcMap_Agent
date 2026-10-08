using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ArcMapAgent
{
    /// <summary>单个模型配置档（OpenAI 兼容端点 + 模型名 + 密钥）。</summary>
    public class ModelProfile
    {
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }

        public ModelProfile()
        {
            Name = "";
            Endpoint = "";
            Model = "";
            ApiKey = "";
        }

        public ModelProfile(string name, string endpoint, string model, string apiKey)
        {
            Name = name; Endpoint = endpoint; Model = model; ApiKey = apiKey;
        }

        public ModelProfile Clone()
        {
            return new ModelProfile(Name, Endpoint, Model, ApiKey);
        }

        public override string ToString() { return Name; }
    }

    /// <summary>
    /// 插件配置，存于 %APPDATA%\ArcMapAgent\config.json。
    /// 从「单一 Endpoint/Model/Key」升级为「多模型档 + 当前档 + 生成参数 + MCP 服务设置」。
    /// </summary>
    public class AppConfig
    {
        public List<ModelProfile> Models { get; set; }
        public string ActiveName { get; set; }
        public double Temperature { get; set; }
        public bool SkipConfirm { get; set; }

        // ---- MCP 桥接服务 ----
        public bool McpAutostart { get; set; }
        public int McpPort { get; set; }
        public string McpToken { get; set; }
        public bool McpAllowDangerous { get; set; }

        public AppConfig()
        {
            Models = new List<ModelProfile>();
            ActiveName = "";
            Temperature = 0.0;
            SkipConfirm = false;
            McpAutostart = false;
            McpPort = 9876;
            McpToken = "";
            McpAllowDangerous = false;
        }

        public static string DirPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcMapAgent");
            }
        }

        public static string FilePath { get { return Path.Combine(DirPath, "config.json"); } }

        /// <summary>当前生效的模型档；缺失时回落到第一个，仍缺失则补一个默认本地档。</summary>
        [ScriptIgnore]
        public ModelProfile Active
        {
            get
            {
                if (Models == null) Models = new List<ModelProfile>();
                if (Models.Count == 0) SeedDefaults();
                ModelProfile found = null;
                foreach (var m in Models)
                    if (string.Equals(m.Name, ActiveName, StringComparison.OrdinalIgnoreCase)) { found = m; break; }
                if (found == null) found = Models[0];
                return found;
            }
        }

        [ScriptIgnore]
        public string Endpoint { get { return Active.Endpoint; } }
        [ScriptIgnore]
        public string Model { get { return Active.Model; } }
        [ScriptIgnore]
        public string ApiKey { get { return Active.ApiKey; } }

        /// <summary>首次使用时的内置预设（均为 OpenAI 兼容端点，按需改）。</summary>
        private void SeedDefaults()
        {
            Models = new List<ModelProfile>
            {
                new ModelProfile("本地 vLLM", "http://localhost:8000/v1", "Qwen2.5-7B-Instruct", ""),
                new ModelProfile("Ollama", "http://localhost:11434/v1", "qwen2.5:7b", ""),
                new ModelProfile("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "")
            };
            ActiveName = "本地 vLLM";
        }

        private void EnsureDefaults()
        {
            if (Models == null) Models = new List<ModelProfile>();
            if (Models.Count == 0) SeedDefaults();
            // 清洗空档，避免下拉框出现空白项
            Models.RemoveAll(m => m == null);
            if (string.IsNullOrEmpty(ActiveName)) ActiveName = Models[0].Name;
            if (string.IsNullOrEmpty(McpToken)) McpToken = NewToken();
            if (McpPort < 1024 || McpPort > 65535) McpPort = 9876;
            if (Temperature < 0) Temperature = 0;
            if (Temperature > 2) Temperature = 2;
        }

        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static AppConfig Load()
        {
            var c = new AppConfig();
            try
            {
                if (File.Exists(FilePath))
                {
                    string txt = File.ReadAllText(FilePath);
                    var jss = new JavaScriptSerializer();
                    c = jss.Deserialize<AppConfig>(txt) ?? new AppConfig();
                    // 兼容旧版扁平配置（Endpoint/Model/ApiKey 三个键）
                    if (c.Models == null || c.Models.Count == 0)
                    {
                        try
                        {
                            var legacy = jss.Deserialize<Dictionary<string, string>>(txt);
                            if (legacy != null && (legacy.ContainsKey("Endpoint") || legacy.ContainsKey("Model")))
                            {
                                c.Models = new List<ModelProfile>
                                {
                                    new ModelProfile("默认",
                                        legacy.ContainsKey("Endpoint") ? legacy["Endpoint"] : "",
                                        legacy.ContainsKey("Model") ? legacy["Model"] : "",
                                        legacy.ContainsKey("ApiKey") ? legacy["ApiKey"] : "")
                                };
                                c.ActiveName = "默认";
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { c = new AppConfig(); }
            c.EnsureDefaults();
            return c;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
                var jss = new JavaScriptSerializer();
                File.WriteAllText(FilePath, jss.Serialize(this));
            }
            catch { }
        }
    }
}
