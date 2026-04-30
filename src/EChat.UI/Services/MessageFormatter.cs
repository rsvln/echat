using Markdig;
using Microsoft.AspNetCore.Components;

namespace EChat.UI.Services;

public class MessageFormatter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // Для таблиц, ссылок и т.д.
        .Build();

    public static MarkupString Format(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(string.Empty);

        // Check if text contains formatting tags
        if (!ContainsFormattingTags(text))
        {
            // Plain text, escape HTML and replace newlines
            return new MarkupString(System.Web.HttpUtility.HtmlEncode(text).Replace("\n", "<br>"));
        }

        // Parse as Markdown
        var html = Markdown.ToHtml(text, Pipeline);

        // Additional processing for Telegram-style tags
        html = ProcessTelegramTags(html);

        // Process code blocks for Prism
        html = ProcessCodeBlocks(html);

        return new MarkupString(html);
    }

    private static string ProcessCodeBlocks(string html)
    {
        // Ensure code blocks have Prism-compatible class format: language-*
        // Replace <code> without class with <code class="language-plaintext">
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<code>",
            @"<code class=""language-plaintext"">"
        );
        
        // Ensure language class format is correct for Prism: language-*
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<code class=""([^""]*)"">",
            match =>
            {
                var className = match.Groups[1].Value;
                if (!className.StartsWith("language-"))
                {
                    // If it's just the language name, add language- prefix
                    return $@"<code class=""language-{className}"">";
                }
                return match.Value;
            }
        );
        
        return html;
    }

    private static bool ContainsFormattingTags(string text)
    {
        return text.Contains("**") || text.Contains("*") || text.Contains("~~") ||
               text.Contains("__") || text.Contains("||") || text.Contains("`") ||
               text.Contains("[") && text.Contains("](") && text.Contains(")");
    }

    private static string ProcessTelegramTags(string html)
    {
        // Обработка ||spoiler|| -> <span class="spoiler">
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\|{2}(.*?)\|{2}", @"<span class=""spoiler"">$1</span>");

        // Обработка __underline__ -> <u>
        html = System.Text.RegularExpressions.Regex.Replace(html, @"_{2}(.*?)_{2}", @"<u>$1</u>");

        return html;
    }
}