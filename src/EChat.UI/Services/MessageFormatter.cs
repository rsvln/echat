using Microsoft.AspNetCore.Components;
using System.Text;

namespace EChat.UI.Services;

public class MessageFormatter
{
    public static MarkupString Format(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(string.Empty);

        var result = FormatToString(text);
        return new MarkupString(result);
    }

    public static string FormatToString(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (!HasFormatting(text))
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br>");

        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        text = ProcessCodeBlocks(text);
        text = Alternate(text, "**", "<strong>", "</strong>");
        text = Alternate(text, "*", "<em>", "</em>");
        text = Alternate(text, "~~", "<del>", "</del>");
        text = Alternate(text, "||", "<span class='spoiler'>", "</span>");
        text = Alternate(text, "`", "<code>", "</code>");
        text = Alternate(text, "--", "<u>", "</u>");
        text = text.Replace("\n", "<br>");

        return text;
    }

    private static bool HasFormatting(string text)
    {
        foreach (char c in text)
        {
            if (c == '*' || c == '`' || c == '~' || c == '|' || c == '-')
                return true;
        }
        return false;
    }

    private static string ProcessCodeBlocks(string text)
    {
        while (text.Contains("```"))
        {
            int start = text.IndexOf("```");
            int end = text.IndexOf("```", start + 3);
            if (end < 0) break;

            string block = text.Substring(start, end - start + 3);
            string inner = block.Substring(3);
            string lang = "", code = inner;

            int nl = inner.IndexOf('\n');
            if (nl > 0 && nl < 20)
            {
                string potential = inner.Substring(0, nl).Trim();
                bool isLang = true;
                foreach (char c in potential) { if (!char.IsLetterOrDigit(c)) { isLang = false; break; } }
                if (isLang && potential.Length > 0) { lang = potential; code = inner.Substring(nl + 1); }
            }

            string html = "<pre class='code-block'><code class='language-" + (lang.Length > 0 ? lang : "plaintext") + "'>" + code.Trim() + "</code></pre>";
            text = text.Replace(block, html);
        }
        return text;
    }

    private static string Alternate(string text, string marker, string open, string close)
    {
        int pos = 0;
        bool isOpen = true;
        var sb = new StringBuilder();

        while (true)
        {
            int idx = text.IndexOf(marker, pos);
            if (idx < 0)
            {
                sb.Append(text.Substring(pos));
                break;
            }

            sb.Append(text.Substring(pos, idx - pos));
            sb.Append(isOpen ? open : close);
            pos = idx + marker.Length;
            isOpen = !isOpen;
        }

        return sb.ToString();
    }
}