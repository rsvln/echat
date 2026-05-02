using System.Text;

namespace EChat.Core.Services;

public static class HtmlFormatter
{
    public static string Format(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!HasFormatting(text)) return EscapeHtml(text);

        // Extract code blocks from raw text → placeholders, then escape the rest
        var blocks = new List<string>();
        text = ExtractCodeBlocks(text, blocks);
        text = EscapeHtml(text);
        text = Alternate(text, "**", "<strong>", "</strong>");
        text = Alternate(text, "*", "<em>", "</em>");
        text = Alternate(text, "~~", "<del>", "</del>");
        text = Alternate(text, "||", "<span class='spoiler'>", "</span>");
        text = Alternate(text, "`", "<code>", "</code>");
        text = Alternate(text, "--", "<u>", "</u>");
        text = text.Replace("\n", "<br>");

        // Restore highlighted code blocks
        for (int i = 0; i < blocks.Count; i++)
            text = text.Replace(Placeholder(i), blocks[i]);

        return text;
    }

    private static string Placeholder(int i) => $"\x01{i}\x02";

    private static bool HasFormatting(string text)
    {
        foreach (char c in text)
            if (c == '*' || c == '`' || c == '~' || c == '|' || c == '-')
                return true;
        return false;
    }

    public static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string ExtractCodeBlocks(string text, List<string> blocks)
    {
        var sb = new StringBuilder();
        int pos = 0;
        while (pos < text.Length)
        {
            int start = text.IndexOf("```", pos);
            if (start < 0)
            {
                sb.Append(text[pos..]);
                break;
            }
            sb.Append(text[pos..start]);

            int end = text.IndexOf("```", start + 3);
            if (end < 0)
            {
                sb.Append(text[start..]);
                break;
            }

            string inner = text[(start + 3)..end];
            string lang = "", code = inner;
            // Try newline separator first (```lang\ncode```)
            int nl = inner.IndexOf('\n');
            if (nl >= 0 && nl < 20)
            {
                string potential = inner[..nl].Trim();
                if (potential.Length > 0 && potential.All(char.IsLetterOrDigit))
                {
                    lang = potential;
                    code = inner[(nl + 1)..];
                }
            }
            else
            {
                // Inline form: ```lang code``` (no newline — like Telegram)
                int sp = inner.IndexOf(' ');
                if (sp > 0 && sp < 20)
                {
                    string potential = inner[..sp].Trim();
                    if (potential.Length > 0 && potential.All(char.IsLetterOrDigit))
                    {
                        lang = potential;
                        code = inner[(sp + 1)..];
                    }
                }
            }

            string? highlighted = lang.Length > 0 ? SyntaxHighlighter.Highlight(code.Trim(), lang) : null;
            string innerHtml = highlighted ?? EscapeHtml(code.Trim());
            string langAttr = lang.Length > 0 ? EscapeHtml(lang.ToLowerInvariant()) : "plaintext";
            string blockHtml = $"<pre class='code-block'><code class='language-{langAttr}'>{innerHtml}</code></pre>";

            sb.Append(Placeholder(blocks.Count));
            blocks.Add(blockHtml);

            pos = end + 3;
            if (pos < text.Length && text[pos] == '\n') pos++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strips markdown-style formatting markers for use in plain-text contexts
    /// (OS notifications, chat list preview). Code blocks → "[code]".
    /// </summary>
    public static string StripFormatting(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Replace code blocks with [code] placeholder
        var sb = new StringBuilder();
        int pos = 0;
        while (pos < text.Length)
        {
            int start = text.IndexOf("```", pos);
            if (start < 0) { sb.Append(text[pos..]); break; }
            sb.Append(text[pos..start]);
            sb.Append("[code]");
            int end = text.IndexOf("```", start + 3);
            pos = end >= 0 ? end + 3 : text.Length;
        }
        text = sb.ToString();

        return text
            .Replace("**", "").Replace("~~", "").Replace("||", "")
            .Replace("--", "").Replace("`", "").Replace("*", "");
    }

    private static string Alternate(string text, string marker, string open, string close)
    {
        int pos = 0;
        bool isOpen = true;
        var sb = new StringBuilder();
        while (true)
        {
            int idx = text.IndexOf(marker, pos);
            if (idx < 0) { sb.Append(text[pos..]); break; }
            sb.Append(text[pos..idx]);
            sb.Append(isOpen ? open : close);
            pos = idx + marker.Length;
            isOpen = !isOpen;
        }
        return sb.ToString();
    }
}
