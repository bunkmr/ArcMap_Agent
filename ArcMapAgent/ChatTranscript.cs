using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>
    /// 对话记录视图：一问一答分块 + Markdown 渲染。
    ///
    /// 结构：用户消息右对齐（浅青底色，读起来像气泡），助手消息左对齐并带「Agent · 时间」抬头，
    /// 工具调用/结果另起一段等宽块。用**原生 RichTextBox** 承载全部文字——自绘文字在双缓冲位图上
    /// 会丢失 ClearType 伽马标定而发虚，原生控件才有系统级锐利度。
    ///
    /// 流式策略：边收边以原始文本追加（保证"在打字"的观感），一轮结束后把这段区间回删、
    /// 再用 Markdown 重新排版，兼顾流畅与最终排版质量。
    /// </summary>
    public class ChatTranscript : RichTextBox
    {
        private readonly MdStyle _md;
        private readonly StringBuilder _pending = new StringBuilder();
        private int _assistantStart = -1;
        private bool _assistantActive;
        private int _toolResultCount;

        private const int MaxToolResultChars = 1600;

        public ChatTranscript()
        {
            ReadOnly = true;
            BorderStyle = BorderStyle.None;
            BackColor = Color.White;
            ForeColor = UiTheme.Text;
            Font = UiTheme.Body;
            WordWrap = true;
            ScrollBars = RichTextBoxScrollBars.Vertical;
            DetectUrls = true;
            HideSelection = false;
            ShortcutsEnabled = true;

            _md = new MdStyle
            {
                Base = UiTheme.Body,
                Mono = UiTheme.Mono(11f),
                Text = UiTheme.Text,
                Sub = UiTheme.TextSub,
                Rule = UiTheme.BorderStrong,
                CodeBg = Color.FromArgb(241, 245, 249),
                CodeFg = Color.FromArgb(51, 65, 85),
                TableHeadBg = Color.FromArgb(240, 253, 250)
            };
        }

        // ---------- 主题色 ----------
        private static readonly Color UserBg = Color.FromArgb(236, 253, 250);      // teal-50
        private static readonly Color UserFg = Color.FromArgb(15, 118, 110);      // teal-700
        private static readonly Color ToolBg = Color.FromArgb(248, 250, 252);
        private static readonly Color ToolBar = Color.FromArgb(13, 148, 136);

        // ---------- 内部工具 ----------

        /// <summary>ReadOnly 的 RichTextBox 在部分版本上会拒绝程序化改动，这里临时解锁。</summary>
        private void Edit(Action a)
        {
            bool ro = ReadOnly;
            try
            {
                if (ro) ReadOnly = false;
                a();
            }
            finally
            {
                if (ro) ReadOnly = true;
            }
        }

        private void Blank(float factor)
        {
            SelectionFont = UiTheme.UI(Math.Max(4f, 12f * factor));
            SelectionColor = Color.White;
            SelectionBackColor = Color.White;
            SelectionIndent = 0;
            SelectionHangingIndent = 0;
            SelectionAlignment = HorizontalAlignment.Left;
            AppendText("\n");
        }

        private void Line(string text, Font f, Color fg, Color bg, int indent, HorizontalAlignment align)
        {
            SelectionFont = f;
            SelectionColor = fg;
            SelectionBackColor = bg;
            SelectionIndent = indent;
            SelectionHangingIndent = 0;
            SelectionAlignment = align;
            AppendText(text);
            SelectionAlignment = HorizontalAlignment.Left;
            SelectionIndent = 0;
            AppendText("\n");
        }

        private void ScrollToEnd()
        {
            try
            {
                SelectionStart = TextLength;
                ScrollToCaret();
            }
            catch { }
        }

        /// <summary>只在用户本来就贴着底部时才自动滚动，避免打断向上翻阅。</summary>
        private void AutoScroll()
        {
            try
            {
                var pos = GetPositionFromCharIndex(TextLength);
                if (pos.Y >= ClientSize.Height - 24) ScrollToEnd();
            }
            catch { ScrollToEnd(); }
        }

        // ---------- 对外 API ----------

        /// <summary>顶部欢迎语（只在空对话里出现）。</summary>
        public void ShowWelcome(string model)
        {
            Edit(() =>
            {
                Line("👋  您好，我是 ArcMap Agent", UiTheme.Header, UiTheme.Text, Color.White, 0, HorizontalAlignment.Left);
                Line("用自然语言描述您想做的地图操作即可，例如：",
                    UiTheme.Small, UiTheme.TextSub, Color.White, 0, HorizontalAlignment.Left);
                Line("•  列出当前地图的所有图层", UiTheme.Small, UiTheme.TextSub, Color.White, 10, HorizontalAlignment.Left);
                Line("•  按「分公司」字段给图层分类着色", UiTheme.Small, UiTheme.TextSub, Color.White, 10, HorizontalAlignment.Left);
                Line("•  统计某图层的要素数量并缩放到它", UiTheme.Small, UiTheme.TextSub, Color.White, 10, HorizontalAlignment.Left);
                Line("当前模型：" + model + "　（可在「模型」页切换）",
                    UiTheme.Small, UiTheme.TextFaint, Color.White, 0, HorizontalAlignment.Left);
                Blank(0.3f);
            });
            ScrollToEnd();
        }

        public void AddUser(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Edit(() =>
            {
                Blank(0.4f);
                Line("你 · " + DateTime.Now.ToString("HH:mm"),
                    UiTheme.SmallBold, UiTheme.TextSub, Color.White, 0, HorizontalAlignment.Right);

                foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    SelectionFont = UiTheme.Body;
                    SelectionColor = UserFg;
                    SelectionBackColor = UserBg;
                    SelectionIndent = 12;
                    SelectionRightIndent = 12;
                    SelectionAlignment = HorizontalAlignment.Right;
                    AppendText("  " + raw + "  ");
                    SelectionAlignment = HorizontalAlignment.Left;
                    SelectionRightIndent = 0;
                    SelectionBackColor = Color.White;
                    AppendText("\n");
                }
            });
            ScrollToEnd();
        }

        public void BeginAssistant(string who)
        {
            Edit(() =>
            {
                Blank(0.4f);
                Line(who + " · " + DateTime.Now.ToString("HH:mm"),
                    UiTheme.SmallBold, UiTheme.Accent, Color.White, 0, HorizontalAlignment.Left);
                _assistantStart = TextLength;
                _pending.Clear();
                _assistantActive = true;
                // 光标指示，回答结束时连同正文一起回删重排
                SelectionFont = UiTheme.Body;
                SelectionColor = UiTheme.Accent;
                SelectionBackColor = Color.White;
                AppendText("▍");
            });
            ScrollToEnd();
        }

        /// <summary>流式增量（原始 Markdown 文本）。</summary>
        public void AppendDelta(string delta)
        {
            if (!_assistantActive || string.IsNullOrEmpty(delta)) return;
            _pending.Append(delta);
            Edit(() =>
            {
                // 先删掉尾部光标指示，再追加正文，然后补回光标
                if (TextLength > _assistantStart)
                {
                    Select(TextLength - 1, 1);
                    SelectedText = "";
                }
                SelectionFont = UiTheme.Body;
                SelectionColor = UiTheme.Text;
                SelectionBackColor = Color.White;
                SelectionIndent = 0;
                AppendText(delta);
                SelectionFont = UiTheme.Body;
                SelectionColor = UiTheme.Accent;
                AppendText("▍");
            });
            AutoScroll();
        }

        /// <summary>一轮回答结束：回删原始文本，按 Markdown 重排。</summary>
        public void EndAssistant()
        {
            if (!_assistantActive) return;
            string raw = _pending.ToString();
            _pending.Clear();
            _assistantActive = false;

            Edit(() =>
            {
                if (_assistantStart >= 0 && TextLength > _assistantStart)
                {
                    Select(_assistantStart, TextLength - _assistantStart);
                    SelectedText = "";
                }
                SelectionStart = TextLength;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    AppendText("(无内容)\n");
                }
                else
                {
                    MarkdownRenderer.Render(this, raw.Trim(), _md);
                }
            });
            _assistantStart = -1;
            ScrollToEnd();
        }

        /// <summary>工具调用提示块。</summary>
        public void AddToolCall(string name, string argsJson)
        {
            Edit(() =>
            {
                Blank(0.25f);
                SelectionFont = UiTheme.Mono(11f);
                SelectionColor = Color.White;
                SelectionBackColor = ToolBar;
                SelectionIndent = 8;
                SelectionAlignment = HorizontalAlignment.Left;
                AppendText(" ⚙ " + (name ?? "") + " ");
                SelectionBackColor = Color.White;
                SelectionFont = UiTheme.Mono(11f);
                SelectionColor = UiTheme.TextSub;
                AppendText("  " + Compact(argsJson));
                SelectionIndent = 0;
                AppendText("\n");
            });
            AutoScroll();
        }

        /// <summary>工具返回结果（等宽块，过长自动截断显示，完整内容仍写入 panel.log）。</summary>
        public void AddToolResult(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _toolResultCount++;
            string shown = text;
            bool cut = false;
            if (shown.Length > MaxToolResultChars)
            {
                shown = shown.Substring(0, MaxToolResultChars);
                cut = true;
            }
            Edit(() =>
            {
                SelectionIndent = 8;
                foreach (var raw in shown.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                {
                    SelectionFont = UiTheme.Mono(11f);
                    SelectionColor = _md.CodeFg;
                    SelectionBackColor = ToolBg;
                    SelectionIndent = 8;
                    SelectionAlignment = HorizontalAlignment.Left;
                    AppendText("  " + raw + "  ");
                    SelectionBackColor = Color.White;
                    SelectionIndent = 0;
                    AppendText("\n");
                }
                if (cut)
                {
                    SelectionFont = UiTheme.Small;
                    SelectionColor = UiTheme.TextFaint;
                    SelectionIndent = 8;
                    AppendText("  …（结果较长，此处仅显示前 " + MaxToolResultChars + " 字，完整内容见 panel.log）\n");
                    SelectionIndent = 0;
                }
                SelectionIndent = 0;
            });
            AutoScroll();
        }

        /// <summary>系统/斜杠命令输出（走 Markdown 渲染）。</summary>
        public void AddSystem(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Edit(() =>
            {
                Blank(0.3f);
                MarkdownRenderer.Render(this, text.Trim(), _md);
            });
            ScrollToEnd();
        }

        /// <summary>错误提示块。</summary>
        public void AddError(string text)
        {
            Edit(() =>
            {
                Blank(0.3f);
                foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    SelectionFont = UiTheme.Body;
                    SelectionColor = Color.FromArgb(153, 27, 27);
                    SelectionBackColor = Color.FromArgb(254, 242, 242);
                    SelectionIndent = 8;
                    SelectionAlignment = HorizontalAlignment.Left;
                    AppendText("  ⚠ " + raw + "  ");
                    SelectionBackColor = Color.White;
                    SelectionIndent = 0;
                    AppendText("\n");
                }
            });
            ScrollToEnd();
        }

        public void ClearAll()
        {
            Edit(() =>
            {
                Clear();
                _assistantStart = -1;
                _assistantActive = false;
                _pending.Clear();
                _toolResultCount = 0;
            });
        }

        /// <summary>把 JSON 参数压成一行，过长截断。</summary>
        private static string Compact(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
            if (s == "{}" || s == "null") return "";
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }
    }
}
