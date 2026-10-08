using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ArcMapAgent
{
    /// <summary>Markdown 渲染样式（由主题派生）。</summary>
    public sealed class MdStyle
    {
        public Font Base;          // 正文
        public Font Mono;          // 代码 / 表格
        public Color Text;
        public Color Sub;
        public Color Rule;
        public Color CodeBg;
        public Color CodeFg;
        public Color TableHeadBg;
    }

    /// <summary>
    /// 把 Markdown 渲染进 RichTextBox。
    ///
    /// 为什么不用 RTF 拼串：中文需要 \uNNNN? 转义、嵌套样式容易算错；直接用
    /// SelectionFont / SelectionColor / SelectionBackColor 逐段写入，既避开转义又天然支持中文。
    ///
    /// 覆盖：标题、加粗、斜体、删除线、行内代码、围栏代码块、有序/无序列表、
    /// 引用块、分隔线、链接、表格（按显示宽度等宽对齐重排）。
    /// </summary>
    internal static class MarkdownRenderer
    {
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        private static readonly object _lock = new object();

        /// <summary>按 (字族, 像素高, 字形) 取字体。RichTextBox 会长期持有 Font 引用，故不释放。</summary>
        private static Font F(string family, float px, FontStyle style)
        {
            if (px < 4f) px = 4f;
            string key = family + "|" + px.ToString("0.##") + "|" + (int)style;
            lock (_lock)
            {
                Font f;
                if (_fonts.TryGetValue(key, out f)) return f;
                f = new Font(family, px, style, GraphicsUnit.Pixel);
                _fonts[key] = f;
                return f;
            }
        }

        private const string MonoFamily = "Consolas";

        private static Font Body(MdStyle st) { return st.Base; }
        private static Font BodyStyled(MdStyle st, FontStyle s) { return F(st.Base.FontFamily.Name, st.Base.Size, s); }
        private static Font CodeFont(MdStyle st) { return F(MonoFamily, st.Mono.Size, FontStyle.Regular); }

        // ---------- 入口 ----------

        /// <summary>把一段 Markdown 追加到 rtb 末尾。</summary>
        public static void Render(RichTextBox rtb, string md, MdStyle st)
        {
            if (rtb == null || string.IsNullOrEmpty(md)) return;

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];

                if (IsFence(line))
                {
                    i++;
                    var code = new StringBuilder();
                    while (i < lines.Length && !IsFence(lines[i])) { code.AppendLine(lines[i]); i++; }
                    if (i < lines.Length) i++;
                    AppendCodeBlock(rtb, code.ToString(), st);
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s{0,3}((-\s*){3,}|(\*\s*){3,}|(_\s*){3,})$"))
                {
                    AppendRule(rtb, st);
                    i++;
                    continue;
                }

                var h = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
                if (h.Success)
                {
                    AppendHeading(rtb, h.Groups[2].Value, h.Groups[1].Value.Length, st);
                    i++;
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s{0,3}>"))
                {
                    var quote = new StringBuilder();
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s{0,3}>"))
                    {
                        quote.Append(Regex.Replace(lines[i], @"^\s{0,3}>\s?", "")).Append('\n');
                        i++;
                    }
                    AppendQuote(rtb, quote.ToString(), st);
                    continue;
                }

                if (IsTableRow(line))
                {
                    var rows = new List<string>();
                    while (i < lines.Length && IsTableRow(lines[i])) { rows.Add(lines[i]); i++; }
                    AppendTable(rtb, rows, st);
                    continue;
                }

                var li = Regex.Match(line, @"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$");
                if (li.Success)
                {
                    int level = li.Groups[1].Value.Replace("\t", "    ").Length / 2;
                    AppendListItem(rtb, li.Groups[3].Value, li.Groups[2].Value, level, st);
                    i++;
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    AppendBlank(rtb, st, 0.45f);
                    i++;
                    continue;
                }

                var para = new StringBuilder();
                while (i < lines.Length && lines[i].Trim().Length > 0
                       && !IsFence(lines[i]) && !IsTableRow(lines[i])
                       && !Regex.IsMatch(lines[i], @"^\s{0,3}(#{1,6})\s")
                       && !Regex.IsMatch(lines[i], @"^\s{0,3}>")
                       && !Regex.IsMatch(lines[i], @"^(\s*)([-*+]|\d{1,3}[.)])\s+"))
                {
                    if (para.Length > 0) para.Append(' ');
                    para.Append(lines[i].Trim());
                    i++;
                }
                AppendParagraph(rtb, para.ToString(), st);
            }
        }

        // ---------- 块级 ----------

        private static bool IsFence(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("```") || t.StartsWith("~~~");
        }

        private static bool IsTableRow(string line)
        {
            string t = line.Trim();
            return t.Length > 1 && t.StartsWith("|") && t.EndsWith("|");
        }

        private static void Reset(RichTextBox rtb, MdStyle st, Color bg)
        {
            rtb.SelectionFont = st.Base;
            rtb.SelectionColor = st.Text;
            rtb.SelectionBackColor = bg;
            rtb.SelectionIndent = 0;
            rtb.SelectionHangingIndent = 0;
            rtb.SelectionRightIndent = 0;
            rtb.SelectionAlignment = HorizontalAlignment.Left;
        }

        private static void AppendBlank(RichTextBox rtb, MdStyle st, float factor)
        {
            Reset(rtb, st, Color.White);
            rtb.SelectionFont = F(st.Base.FontFamily.Name, Math.Max(4f, st.Base.Size * factor), FontStyle.Regular);
            rtb.AppendText("\n");
        }

        private static void AppendHeading(RichTextBox rtb, string text, int level, MdStyle st)
        {
            AppendBlank(rtb, st, 0.25f);
            Reset(rtb, st, Color.White);
            float px = st.Base.Size + (level <= 1 ? 5f : level == 2 ? 3f : 1.5f);
            Font f = F(st.Base.FontFamily.Name, px, FontStyle.Bold);
            Color c = level <= 2 ? UiTheme.AccentDark : st.Text;
            rtb.SelectionFont = f;
            rtb.SelectionColor = c;
            foreach (var r in ParseInline(text)) Emit(rtb, r, f, c, st, Color.White);
            rtb.AppendText("\n");
        }

        private static void AppendParagraph(RichTextBox rtb, string text, MdStyle st)
        {
            Reset(rtb, st, Color.White);
            foreach (var r in ParseInline(text)) Emit(rtb, r, st.Base, st.Text, st, Color.White);
            rtb.AppendText("\n");
        }

        private static void AppendListItem(RichTextBox rtb, string text, string marker, int level, MdStyle st)
        {
            Reset(rtb, st, Color.White);
            rtb.SelectionIndent = 8 + level * 16 + 14;
            rtb.SelectionHangingIndent = -14;
            bool ordered = marker.Length > 0 && char.IsDigit(marker[0]);
            rtb.SelectionFont = st.Base;
            rtb.SelectionColor = UiTheme.AccentDark;
            rtb.AppendText(ordered ? marker : "•");
            rtb.SelectionFont = st.Base;
            rtb.SelectionColor = st.Text;
            rtb.AppendText(" ");
            foreach (var r in ParseInline(text)) Emit(rtb, r, st.Base, st.Text, st, Color.White);
            rtb.AppendText("\n");
        }

        private static void AppendQuote(RichTextBox rtb, string text, MdStyle st)
        {
            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) continue;
                Reset(rtb, st, Color.White);
                rtb.SelectionIndent = 10;
                rtb.SelectionFont = st.Base;
                rtb.SelectionColor = UiTheme.Accent;
                rtb.AppendText("▎ ");
                foreach (var r in ParseInline(raw)) Emit(rtb, r, st.Base, st.Sub, st, Color.White);
                rtb.AppendText("\n");
            }
        }

        private static void AppendRule(RichTextBox rtb, MdStyle st)
        {
            AppendBlank(rtb, st, 0.3f);
            Reset(rtb, st, Color.White);
            rtb.SelectionFont = F(st.Base.FontFamily.Name, Math.Max(5f, st.Base.Size * 0.7f), FontStyle.Regular);
            rtb.SelectionColor = st.Rule;
            rtb.AppendText(new string('─', 40));
            rtb.AppendText("\n");
        }

        private static void AppendCodeBlock(RichTextBox rtb, string code, MdStyle st)
        {
            AppendBlank(rtb, st, 0.25f);
            code = code.TrimEnd('\n', '\r');
            foreach (var raw in code.Split('\n'))
            {
                Reset(rtb, st, Color.White);
                rtb.SelectionIndent = 8;
                rtb.SelectionFont = CodeFont(st);
                rtb.SelectionColor = st.CodeFg;
                rtb.SelectionBackColor = st.CodeBg;
                rtb.AppendText("  " + raw.Replace("\t", "    ") + "  ");
                rtb.SelectionBackColor = Color.White;
                rtb.AppendText("\n");
            }
            AppendBlank(rtb, st, 0.25f);
        }

        /// <summary>把 | a | b | 重排成按显示宽度对齐的等宽表格（比原样输出可读得多）。</summary>
        private static void AppendTable(RichTextBox rtb, List<string> rows, MdStyle st)
        {
            var cells = new List<List<string>>();
            foreach (var row in rows)
            {
                var parts = SplitRow(row);
                bool sep = parts.Count > 0;
                foreach (var p in parts)
                    if (!Regex.IsMatch(p, @"^:?-{2,}:?$")) { sep = false; break; }
                if (sep) continue;                 // 丢掉 |---|---| 分隔行
                cells.Add(parts);
            }
            if (cells.Count == 0) return;

            int cols = 0;
            foreach (var r in cells) cols = Math.Max(cols, r.Count);
            var w = new int[cols];
            for (int c = 0; c < cols; c++)
                foreach (var r in cells)
                    if (c < r.Count) w[c] = Math.Max(w[c], DisplayWidth(r[c]));
            int total = 0; foreach (var v in w) total += v;
            for (int c = 0; c < cols; c++) w[c] = Math.Min(w[c], 24);

            AppendBlank(rtb, st, 0.2f);
            for (int ri = 0; ri < cells.Count; ri++)
            {
                bool head = ri == 0;
                var sb = new StringBuilder("  ");
                for (int c = 0; c < cols; c++)
                {
                    string v = c < cells[ri].Count ? cells[ri][c] : "";
                    sb.Append(Pad(v, w[c]));
                    if (c < cols - 1) sb.Append(" │ ");
                }
                Reset(rtb, st, head ? st.TableHeadBg : Color.White);
                rtb.SelectionIndent = 8;
                rtb.SelectionFont = CodeFont(st);
                rtb.SelectionColor = head ? UiTheme.AccentDark : st.Text;
                rtb.SelectionBackColor = head ? st.TableHeadBg : Color.White;
                rtb.AppendText(sb.ToString() + "  ");
                rtb.SelectionBackColor = Color.White;
                rtb.AppendText("\n");

                if (head)
                {
                    Reset(rtb, st, Color.White);
                    rtb.SelectionIndent = 8;
                    rtb.SelectionFont = F(st.Base.FontFamily.Name, Math.Max(4f, st.Base.Size * 0.55f), FontStyle.Regular);
                    rtb.SelectionColor = st.Rule;
                    int lineW = 0; foreach (var v in w) lineW += v;
                    rtb.AppendText("  " + new string('─', Math.Min(70, lineW + 3 * (cols - 1))) + "\n");
                }
            }
            AppendBlank(rtb, st, 0.25f);
        }

        private static List<string> SplitRow(string line)
        {
            string t = line.Trim();
            t = t.Substring(1, t.Length - 2);          // 去掉首尾 |
            var res = new List<string>();
            foreach (var p in t.Split('|')) res.Add(p.Trim());
            return res;
        }

        /// <summary>中文按 2 个字符宽度计，用于表格对齐。</summary>
        private static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char ch in s) w += (ch > 0x2E80) ? 2 : 1;
            return w;
        }

        private static string Pad(string s, int width)
        {
            int w = DisplayWidth(s);
            if (w > width)
            {
                var sb = new StringBuilder();
                int acc = 0;
                foreach (char ch in s)
                {
                    int cw = (ch > 0x2E80) ? 2 : 1;
                    if (acc + cw > width - 1) { sb.Append('…'); break; }
                    sb.Append(ch); acc += cw;
                }
                s = sb.ToString();
                w = DisplayWidth(s);
            }
            return s + new string(' ', Math.Max(0, width - w));
        }

        // ---------- 行内 ----------

        private sealed class Run
        {
            public string Text;
            public bool Bold, Italic, Code, Strike, Link;
        }

        private static void Emit(RichTextBox rtb, Run r, Font baseFont, Color fg, MdStyle st, Color bg)
        {
            if (r == null || string.IsNullOrEmpty(r.Text)) return;

            if (r.Code)
            {
                rtb.SelectionFont = CodeFont(st);
                rtb.SelectionColor = UiTheme.AccentDark;
                rtb.SelectionBackColor = UiTheme.AccentSoft;
                rtb.AppendText(r.Text);
                rtb.SelectionBackColor = bg;
                return;
            }

            var style = baseFont.Style;
            if (r.Bold) style |= FontStyle.Bold;
            if (r.Italic) style |= FontStyle.Italic;
            if (r.Strike) style |= FontStyle.Strikeout;
            rtb.SelectionFont = F(baseFont.FontFamily.Name, baseFont.Size, style);
            rtb.SelectionColor = r.Link ? UiTheme.Info : fg;
            rtb.SelectionBackColor = bg;
            rtb.AppendText(r.Text);
        }

        /// <summary>极简行内解析：`code` → **粗** → ~~删~~ → *斜*/_斜_ → [文字](链接)。</summary>
        private static List<Run> ParseInline(string s)
        {
            var runs = new List<Run>();
            if (string.IsNullOrEmpty(s)) return runs;

            int i = 0;
            var plain = new StringBuilder();
            Action flush = () =>
            {
                if (plain.Length > 0) { runs.Add(new Run { Text = plain.ToString() }); plain.Clear(); }
            };

            while (i < s.Length)
            {
                char c = s[i];

                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        flush();
                        runs.Add(new Run { Text = s.Substring(i + 1, end - i - 1), Code = true });
                        i = end + 1;
                        continue;
                    }
                }

                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    string mark = new string(c, 2);
                    int end = s.IndexOf(mark, i + 2, StringComparison.Ordinal);
                    if (end > i)
                    {
                        flush();
                        foreach (var inner in ParseInline(s.Substring(i + 2, end - i - 2)))
                        { inner.Bold = true; runs.Add(inner); }
                        i = end + 2;
                        continue;
                    }
                }

                if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
                {
                    int end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (end > i)
                    {
                        flush();
                        foreach (var inner in ParseInline(s.Substring(i + 2, end - i - 2)))
                        { inner.Strike = true; runs.Add(inner); }
                        i = end + 2;
                        continue;
                    }
                }

                if (c == '*' || c == '_')
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end > i + 1)
                    {
                        flush();
                        foreach (var inner in ParseInline(s.Substring(i + 1, end - i - 1)))
                        { inner.Italic = true; runs.Add(inner); }
                        i = end + 1;
                        continue;
                    }
                }

                if (c == '[')
                {
                    int rb = s.IndexOf(']', i + 1);
                    if (rb > i && rb + 1 < s.Length && s[rb + 1] == '(')
                    {
                        int rp = s.IndexOf(')', rb + 2);
                        if (rp > rb)
                        {
                            flush();
                            runs.Add(new Run { Text = s.Substring(i + 1, rb - i - 1), Link = true });
                            i = rp + 1;
                            continue;
                        }
                    }
                }

                plain.Append(c);
                i++;
            }
            flush();
            return runs;
        }
    }
}
