using Microsoft.AspNetCore.Components;

namespace EChat.UI.Services;

public class MessageFormatter
{
    public static MarkupString Format(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(string.Empty);

        // Simple string replaces for speed
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        
        // Code blocks ```lang\ncode```
        while (text.Contains("```"))
        {
            int start = text.IndexOf("```");
            int end = text.IndexOf("```", start + 3);
            if (end < 0) break;
            
            string block = text.Substring(start, end - start + 3);
            string content = block.Substring(3).TrimStart('\n').TrimEnd('`');
            
            string lang = "";
            int nl = content.IndexOf('\n');
            if (nl > 0 && nl < 15)
            {
                string potential = content.Substring(0, nl).Trim();
                bool isWord = true;
                foreach (char c in potential) { if (!char.IsLetterOrDigit(c)) { isWord = false; break; } }
                if (isWord && potential.Length > 0) { lang = potential; content = content.Substring(nl + 1); }
            }
            
            string html = "<pre class='code-block'><code class='language-" + (lang.Length > 0 ? lang : "plaintext") + "'>" + content + "</code></pre>";
            text = text.Replace(block, html);
        }

        // Simple replaces
        text = text.Replace("**", "<strong>").Replace("**", "</strong>");
        text = text.Replace("~~", "<del>").Replace("~~", "</del>");
        text = text.Replace("||", "<span class='spoiler'>").Replace("||", "</span>");
        text = text.Replace("`", "<code>").Replace("`", "</code>");
        text = text.Replace("\n", "<br>");

        return new MarkupString(text);
    }
}