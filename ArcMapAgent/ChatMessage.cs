using System.Collections.Generic;

namespace ArcMapAgent
{
    /// <summary>模型发起的一次工具调用（对应 OpenAI 规范里的 tool_calls 元素）。</summary>
    public class ToolCallRef
    {
        public string id { get; set; }
        public string name { get; set; }
        public string argumentsJson { get; set; }
    }

    /// <summary>
    /// 一条对话消息。
    ///
    /// 关键：assistant 消息必须带上本轮发起的 tool_calls，随后紧跟的 tool 消息必须带 tool_call_id，
    /// 两者一一对应。缺了这层关联，服务端（vLLM / OpenAI）要么报错，要么把工具上下文丢掉，
    /// 模型就只剩下"自己编工具结果"这一条路——这正是之前出现假对话的根因之一。
    /// </summary>
    public class ChatMessage
    {
        public string role { get; set; }              // "system" | "user" | "assistant" | "tool"
        public string content { get; set; }
        public string name { get; set; }              // tool 消息：工具名
        public string tool_call_id { get; set; }      // tool 消息：对应的调用 id
        public List<ToolCallRef> tool_calls { get; set; }   // assistant 消息：本轮发起的调用
    }
}
