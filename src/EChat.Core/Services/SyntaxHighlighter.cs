using System.Reflection;
using Jint;

namespace EChat.Core.Services;

public static class SyntaxHighlighter
{
    private static Engine? _js;
    private static readonly object _lock = new();
    private static volatile bool _ready;

    static SyntaxHighlighter()
    {
        // Initialize on a background thread — never blocks the calling thread
        Task.Run(Initialize);
    }

    private static void Initialize()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("EChat.Core.Resources.highlight.min.js")!;
            using var reader = new StreamReader(stream);
            var src = reader.ReadToEnd();

            var js = new Engine();
            js.Execute("var window = {}; var self = {};");
            js.Execute(src);

            lock (_lock)
            {
                _js = js;
                _ready = true;
            }
        }
        catch { /* highlighting unavailable — fallback to plaintext */ }
    }

    // Returns highlighted HTML or null if not ready / language unknown / error
    public static string? Highlight(string code, string lang)
    {
        if (!_ready || string.IsNullOrEmpty(lang)) return null;
        try
        {
            lock (_lock)
            {
                if (_js == null) return null;
                _js.SetValue("__code", code);
                _js.SetValue("__lang", lang.ToLowerInvariant());
                var result = _js.Evaluate(
                    "hljs.getLanguage(__lang) ? hljs.highlight(__code, {language: __lang}).value : null");
                if (result.IsNull() || result.IsUndefined()) return null;
                return result.AsString();
            }
        }
        catch
        {
            return null;
        }
    }
}
