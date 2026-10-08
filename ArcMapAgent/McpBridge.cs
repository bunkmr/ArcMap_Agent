using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ArcMapAgent
{
    /// <summary>
    /// 本地 MCP 桥接服务：把本插件的 ArcObjects 工具暴露给 Claude Desktop / Cursor 等外部 Agent。
    /// 参考 QGIS Agent 的 MCP 设计，但用纯 TcpListener 实现一个最小的 HTTP + JSON-RPC(2.0) 端点，
    /// 避免 HttpListener 在非管理员下需要 URL 预留的问题。
    ///
    /// 端点：
    ///   GET  /health   → 无鉴权，返回运行状态
    ///   POST /mcp      → JSON-RPC，必须携带令牌（X-Auth-Token 或 Authorization: Bearer）
    /// 方法：initialize / tools/list / tools/call / ping
    /// 仅监听 127.0.0.1，局域网其他机器无法连接。
    /// </summary>
    public class McpBridge
    {
        private static readonly McpBridge _instance = new McpBridge();
        public static McpBridge Get() { return _instance; }

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private int _port;
        private string _token;
        private bool _allowDangerous;
        private Func<string, Dictionary<string, object>, string> _invoke;
        private Func<List<ToolDef>> _toolsProvider;

        public const string Version = "0.4.3";

        /// <summary>危险工具：会改变地图文档或写盘，默认不对外暴露。</summary>
        private static readonly HashSet<string> Dangerous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_shapefile", "remove_layer", "save_document"
        };

        public event Action<string> StatusChanged;

        public bool IsRunning { get { return _running; } }
        public int Port { get { return _port; } }
        public bool AllowDangerous { get { return _allowDangerous; } }
        public string TokenMasked
        {
            get
            {
                if (string.IsNullOrEmpty(_token) || _token.Length < 12) return "(未设置)";
                return _token.Substring(0, 6) + "..." + _token.Substring(_token.Length - 4);
            }
        }

        private void Raise(string msg)
        {
            var h = StatusChanged;
            if (h != null) { try { h(msg); } catch { } }
        }

        public void Start(int port, string token, bool allowDangerous,
                          Func<List<ToolDef>> toolsProvider,
                          Func<string, Dictionary<string, object>, string> invoke)
        {
            Stop();
            _port = port; _token = token ?? ""; _allowDangerous = allowDangerous;
            _toolsProvider = toolsProvider; _invoke = invoke;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "ArcMapAgent.MCP" };
                _thread.Start();
                Raise("运行中 · 监听 127.0.0.1:" + port);
            }
            catch (Exception ex)
            {
                _running = false;
                Raise("启动失败: " + ex.Message);
                throw;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            _thread = null;
            Raise("未运行");
        }

        /// <summary>服务运行中热更新「特权工具」开关与令牌。</summary>
        public void UpdatePolicy(bool allowDangerous, string token)
        {
            _allowDangerous = allowDangerous;
            if (!string.IsNullOrEmpty(token)) _token = token;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) { /* Stop() 触发，正常退出 */ }
                catch (ObjectDisposedException) { break; }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(object state)
        {
            var client = state as TcpClient;
            if (client == null) return;
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    ns.ReadTimeout = 15000; ns.WriteTimeout = 15000;
                    var req = ReadRequest(ns);
                    if (req == null) { WriteResponse(ns, 400, "Bad Request", "{\"error\":\"malformed request\"}"); return; }

                    if (req.Method == "GET" && (req.Path == "/health" || req.Path == "/"))
                    {
                        int n = CountExposedTools();
                        WriteResponse(ns, 200, "OK",
                            "{\"status\":\"ok\",\"name\":\"ArcMap Agent\",\"version\":\"" + Version +
                            "\",\"port\":" + _port + ",\"tools\":" + n +
                            ",\"allowDangerous\":" + (_allowDangerous ? "true" : "false") + "}");
                        return;
                    }

                    if (req.Method == "POST" && req.Path == "/mcp")
                    {
                        if (!CheckAuth(req))
                        {
                            WriteResponse(ns, 401, "Unauthorized", "{\"error\":\"invalid or missing token\"}");
                            return;
                        }
                        string body = HandleRpc(req.Body);
                        WriteResponse(ns, 200, "OK", body);
                        return;
                    }

                    WriteResponse(ns, 404, "Not Found", "{\"error\":\"not found\"}");
                }
            }
            catch { try { client.Close(); } catch { } }
        }

        private int CountExposedTools()
        {
            try
            {
                var tools = _toolsProvider != null ? _toolsProvider() : new List<ToolDef>();
                int n = 0;
                foreach (var t in tools) if (_allowDangerous || !Dangerous.Contains(t.name)) n++;
                return n;
            }
            catch { return 0; }
        }

        private bool CheckAuth(HttpReq req)
        {
            if (string.IsNullOrEmpty(_token)) return false;
            string v;
            if (req.Headers.TryGetValue("x-auth-token", out v))
                return string.Equals(v, _token, StringComparison.Ordinal);
            if (req.Headers.TryGetValue("authorization", out v))
            {
                v = v ?? "";
                const string p = "Bearer ";
                if (v.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(v.Substring(p.Length).Trim(), _token, StringComparison.Ordinal);
            }
            return false;
        }

        // ---- JSON-RPC ----
        private string HandleRpc(string body)
        {
            var jss = new JavaScriptSerializer();
            Dictionary<string, object> root;
            try { root = jss.Deserialize<Dictionary<string, object>>(body ?? "{}"); }
            catch { root = null; }
            if (root == null) return Err(null, -32700, "Parse error");

            object id = root.ContainsKey("id") ? root["id"] : null;
            string method = root.ContainsKey("method") ? Convert.ToString(root["method"]) : "";
            var prms = root.ContainsKey("params") ? root["params"] as Dictionary<string, object> : null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Ok(id, new Dictionary<string, object>
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["serverInfo"] = new Dictionary<string, object>
                            {
                                ["name"] = "ArcMap Agent", ["version"] = Version
                            },
                            ["capabilities"] = new Dictionary<string, object>
                            {
                                ["tools"] = new Dictionary<string, object>()
                            }
                        });
                    case "notifications/initialized":
                        return Ok(id, new Dictionary<string, object>());
                    case "ping":
                        return Ok(id, new Dictionary<string, object>());
                    case "tools/list":
                        return Ok(id, new Dictionary<string, object> { ["tools"] = BuildToolList() });
                    case "tools/call":
                        return Ok(id, CallTool(prms));
                    default:
                        return Err(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return Err(id, -32603, ex.Message);
            }
        }

        private List<Dictionary<string, object>> BuildToolList()
        {
            var outList = new List<Dictionary<string, object>>();
            var tools = _toolsProvider != null ? _toolsProvider() : new List<ToolDef>();
            foreach (var t in tools)
            {
                bool danger = Dangerous.Contains(t.name);
                if (danger && !_allowDangerous) continue;
                outList.Add(new Dictionary<string, object>
                {
                    ["name"] = t.name,
                    ["description"] = (danger ? "[特权] " : "") + (t.description ?? ""),
                    ["inputSchema"] = t.parameters ?? new Dictionary<string, object>()
                });
            }
            return outList;
        }

        private Dictionary<string, object> CallTool(Dictionary<string, object> prms)
        {
            string name = prms != null && prms.ContainsKey("name") ? Convert.ToString(prms["name"]) : "";
            var args = new Dictionary<string, object>();
            if (prms != null && prms.ContainsKey("arguments") && prms["arguments"] is Dictionary<string, object>)
                args = (Dictionary<string, object>)prms["arguments"];

            var text = new List<Dictionary<string, object>>();
            var result = new Dictionary<string, object> { ["isError"] = false };

            if (string.IsNullOrEmpty(name))
            {
                result["isError"] = true;
                text.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "缺少工具名 name" });
            }
            else if (Dangerous.Contains(name) && !_allowDangerous)
            {
                result["isError"] = true;
                text.Add(new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = "工具 " + name + " 属于特权工具，当前被拒绝。请在插件「MCP」页勾选“允许外部 Agent 调用特权工具”。"
                });
            }
            else if (_invoke == null)
            {
                result["isError"] = true;
                text.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "插件内未注册工具执行器" });
            }
            else
            {
                try
                {
                    string r = _invoke(name, args);
                    text.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = r ?? "" });
                }
                catch (Exception ex)
                {
                    result["isError"] = true;
                    text.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = "执行异常: " + ex.Message });
                }
            }
            result["content"] = text;
            return result;
        }

        private static string Ok(object id, object result)
        {
            var d = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            return new JavaScriptSerializer().Serialize(d);
        }

        private static string Err(object id, int code, string message)
        {
            var d = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            };
            return new JavaScriptSerializer().Serialize(d);
        }

        // ---- 极简 HTTP 解析 ----
        private class HttpReq
        {
            public string Method = "GET";
            public string Path = "/";
            public string Body = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static HttpReq ReadRequest(NetworkStream ns)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd = -1;
            // 先读到 header 结束
            while (headerEnd < 0)
            {
                int n = ns.Read(chunk, 0, chunk.Length);
                if (n <= 0) return null;
                buffer.Write(chunk, 0, n);
                var arr = buffer.GetBuffer();
                headerEnd = IndexOf(arr, (int)buffer.Length, new byte[] { 13, 10, 13, 10 });
                if (buffer.Length > 1024 * 256) return null;
            }

            var raw = buffer.ToArray();
            string headText = Encoding.UTF8.GetString(raw, 0, headerEnd);
            var lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            var req = new HttpReq();
            var parts = lines[0].Split(' ');
            if (parts.Length >= 2) { req.Method = parts[0].ToUpperInvariant(); req.Path = parts[1]; }
            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                string k = lines[i].Substring(0, c).Trim();
                string v = lines[i].Substring(c + 1).Trim();
                req.Headers[k] = v;
                if (string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(v, out contentLength);
            }

            int bodyStart = headerEnd + 4;
            var body = new MemoryStream();
            int alreadyHave = raw.Length - bodyStart;
            if (alreadyHave > 0) body.Write(raw, bodyStart, Math.Min(alreadyHave, contentLength));
            while (body.Length < contentLength)
            {
                int n = ns.Read(chunk, 0, Math.Min(chunk.Length, contentLength - (int)body.Length));
                if (n <= 0) break;
                body.Write(chunk, 0, n);
            }
            req.Body = Encoding.UTF8.GetString(body.ToArray());
            return req;
        }

        private static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private static void WriteResponse(NetworkStream ns, int code, string reason, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, X-Auth-Token, Authorization\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            ns.Write(bytes, 0, bytes.Length);
            ns.Flush();
        }
    }
}
