using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ArcMapAgent
{
    /// <summary>
    /// OpenAI 兼容的聊天客户端，支持 SSE 流式输出与 function calling。
    /// 已实测可用的本地端点形如 http://localhost:8000/v1 （vLLM / Ollama 均兼容）。
    /// </summary>
    public class ToolDef
    {
        public string name;
        public string description;
        public object parameters; // JSON schema 对象（Dictionary）
    }

    public class ToolCall
    {
        public string id;          // 服务端返回的调用 id，回填 tool 消息时必须原样带回
        public string name;
        public string argumentsJson;
    }

    public class ChatResult
    {
        public string content = "";
        public List<ToolCall> toolCalls = new List<ToolCall>();
        public bool HasToolCalls { get { return toolCalls.Count > 0; } }
    }

    internal class ToolCallAcc { public string id = ""; public string name = ""; public string args = ""; }

    public class LlmClient
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly JavaScriptSerializer _jss = new JavaScriptSerializer();

        /// <summary>
        /// 发送一次对话，流式回调 content，同时累积 function calling 的 tool_calls。
        /// </summary>
        public async Task<ChatResult> ChatAsync(List<ChatMessage> messages, List<ToolDef> tools,
                                               Action<string> onContent, AppConfig cfg, CancellationToken ct)
        {
            var result = new ChatResult();
            var prof = cfg.Active;

            var req = new Dictionary<string, object>();
            req["model"] = prof.Model;

            var msgArr = new List<Dictionary<string, object>>();
            foreach (var m in messages)
            {
                var d = new Dictionary<string, object>();
                d["role"] = m.role;
                d["content"] = m.content ?? "";

                if (m.role == "tool")
                {
                    // OpenAI 规范：tool 消息用 tool_call_id 绑定到发起它的那一次调用。
                    // 只带 name 是旧式写法，很多服务端会直接 400 或忽略上下文。
                    if (!string.IsNullOrEmpty(m.tool_call_id)) d["tool_call_id"] = m.tool_call_id;
                    if (!string.IsNullOrEmpty(m.name)) d["name"] = m.name;
                }

                // assistant 消息如果发起了工具调用，必须把 tool_calls 原样带回去，
                // 否则后续的 tool 消息找不到"父调用"，整段工具上下文作废。
                if (m.role == "assistant" && m.tool_calls != null && m.tool_calls.Count > 0)
                {
                    var tcs = new List<Dictionary<string, object>>();
                    for (int k = 0; k < m.tool_calls.Count; k++)
                    {
                        var tc = m.tool_calls[k];
                        var t = new Dictionary<string, object>();
                        t["id"] = string.IsNullOrEmpty(tc.id)
                            ? ("call_" + Guid.NewGuid().ToString("N").Substring(0, 12))
                            : tc.id;
                        t["type"] = "function";
                        var fn = new Dictionary<string, object>();
                        fn["name"] = tc.name ?? "";
                        fn["arguments"] = string.IsNullOrEmpty(tc.argumentsJson) ? "{}" : tc.argumentsJson;
                        t["function"] = fn;
                        tcs.Add(t);
                    }
                    d["tool_calls"] = tcs;
                }

                msgArr.Add(d);
            }
            req["messages"] = msgArr;
            req["stream"] = true;
            try { req["temperature"] = Math.Max(0.0, Math.Min(2.0, cfg.Temperature)); } catch { }

            if (tools != null && tools.Count > 0)
            {
                var toolsArr = new List<Dictionary<string, object>>();
                foreach (var t in tools)
                {
                    var td = new Dictionary<string, object>();
                    td["type"] = "function";
                    var fn = new Dictionary<string, object>();
                    fn["name"] = t.name;
                    fn["description"] = t.description;
                    fn["parameters"] = t.parameters ?? new Dictionary<string, object>();
                    td["function"] = fn;
                    toolsArr.Add(td);
                }
                req["tools"] = toolsArr;
                req["tool_choice"] = "auto";
            }

            string body = _jss.Serialize(req);
            string url = prof.Endpoint.TrimEnd('/') + "/chat/completions";

            var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(prof.ApiKey))
                httpReq.Headers.Add("Authorization", "Bearer " + prof.ApiKey);

            var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var toolAcc = new Dictionary<int, ToolCallAcc>();
            using (var stream = await resp.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    string json = line.Substring(5).Trim();
                    if (json == "[DONE]") break;

                    Dictionary<string, object> obj = null;
                    try { obj = _jss.Deserialize<Dictionary<string, object>>(json); }
                    catch { continue; }
                    if (obj == null || !obj.ContainsKey("choices")) continue;

                    var choices = obj["choices"] as ArrayList;
                    if (choices == null || choices.Count == 0) continue;
                    var choice = choices[0] as Dictionary<string, object>;
                    if (choice == null) continue;

                    if (choice.ContainsKey("delta"))
                    {
                        var delta = choice["delta"] as Dictionary<string, object>;
                        if (delta != null)
                        {
                            if (delta.ContainsKey("content") && delta["content"] != null)
                            {
                                string c = delta["content"].ToString();
                                result.content += c;
                                onContent?.Invoke(c);
                            }
                            if (delta.ContainsKey("tool_calls") && delta["tool_calls"] != null)
                            {
                                var tcs = delta["tool_calls"] as ArrayList;
                                if (tcs != null)
                                {
                                    foreach (var o in tcs)
                                    {
                                        var tc = o as Dictionary<string, object>;
                                        if (tc == null) continue;
                                        int idx = 0;
                                        if (tc.ContainsKey("index"))
                                        {
                                            try { idx = Convert.ToInt32(tc["index"]); } catch { }
                                        }
                                        if (!toolAcc.ContainsKey(idx)) toolAcc[idx] = new ToolCallAcc();
                                        var acc = toolAcc[idx];
                                        if (tc.ContainsKey("id") && tc["id"] != null) acc.id = tc["id"].ToString();
                                        if (tc.ContainsKey("function") && tc["function"] != null)
                                        {
                                            var fn = tc["function"] as Dictionary<string, object>;
                                            if (fn != null)
                                            {
                                                if (fn.ContainsKey("name") && fn["name"] != null) acc.name = fn["name"].ToString();
                                                if (fn.ContainsKey("arguments") && fn["arguments"] != null) acc.args += fn["arguments"].ToString();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            var idxs = new List<int>(toolAcc.Keys);
            idxs.Sort();
            foreach (var i in idxs)
            {
                var acc = toolAcc[i];
                result.toolCalls.Add(new ToolCall { id = acc.id, name = acc.name, argumentsJson = acc.args });
            }
            return result;
        }

        /// <summary>
        /// 连接测试：向当前模型的 /chat/completions 发一条最小请求（非流式、max_tokens=1）。
        /// 成功即返回；失败抛出带可读原因的异常（HTTP 状态码 + 响应片段）。
        /// </summary>
        public async Task PingAsync(AppConfig cfg)
        {
            var prof = cfg.Active;
            if (string.IsNullOrWhiteSpace(prof.Endpoint)) throw new InvalidOperationException("端点为空白，请先在「模型」页填写 API 端点。");
            if (string.IsNullOrWhiteSpace(prof.Model)) throw new InvalidOperationException("模型名为空白，请先在「模型」页填写模型名。");

            var req = new Dictionary<string, object>();
            req["model"] = prof.Model;
            req["messages"] = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "user", ["content"] = "ping" }
            };
            req["max_tokens"] = 1;
            req["stream"] = false;

            string url = prof.Endpoint.TrimEnd('/') + "/chat/completions";
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(20);
                var hr = new HttpRequestMessage(HttpMethod.Post, url);
                hr.Content = new StringContent(_jss.Serialize(req), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(prof.ApiKey))
                    hr.Headers.Add("Authorization", "Bearer " + prof.ApiKey);
                var resp = await http.SendAsync(hr);
                if (!resp.IsSuccessStatusCode)
                {
                    string t = "";
                    try { t = await resp.Content.ReadAsStringAsync(); } catch { }
                    if (t != null && t.Length > 300) t = t.Substring(0, 300);
                    throw new InvalidOperationException("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "  " + t);
                }
            }
        }
    }
}
