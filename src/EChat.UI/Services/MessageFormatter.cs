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
            var plain = System.Web.HttpUtility.HtmlEncode(text).Replace("\n", "<br>");
            Console.WriteLine($"MessageFormatter: Plain text: {plain}");
            return new MarkupString(plain);
        }

        // Parse as Markdown
        var html = Markdown.ToHtml(text, Pipeline);
        Console.WriteLine($"MessageFormatter: Formatted HTML: {html}");

        // Additional processing for Telegram-style tags
        html = ProcessTelegramTags(html);

        // Process code blocks for Prism
        html = ProcessCodeBlocks(html);

        return new MarkupString(html);
    }

    private static string ProcessCodeBlocks(string html)
    {
        // Replace <pre><code> with <pre><code class="language-*">
        // Assuming the code block starts with ```language
        // But since Markdig handles it, we can enhance
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