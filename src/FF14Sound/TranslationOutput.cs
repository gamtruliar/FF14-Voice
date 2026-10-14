using System.Text.Json;
using System.Text.RegularExpressions;

namespace FF14Sound;

/// <summary>Turns model formatting into dialogue before it reaches subtitles or speech.</summary>
public static class TranslationOutput
{
    public static string Normalize(string? output)
    {
        var text = output ?? "";
        for (var depth = 0; depth < 5; depth++)
        {
            text = Regex.Replace(text, @"<think\b[^>]*>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
            if (text.Length == 0 || text.Contains("<think", StringComparison.OrdinalIgnoreCase) || text.Contains("</think", StringComparison.OrdinalIgnoreCase))
                throw Invalid();

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var fence = Regex.Match(text, @"\A```(?:json|text)?\s*\n(?<body>.*?)\n?```\z", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!fence.Success) throw Invalid();
                text = fence.Groups["body"].Value;
                continue;
            }

            var structured = Regex.IsMatch(text, "\\A(?:\\{\\s*\"|\\[\\s*\\{)");
            if (structured || text.StartsWith('"'))
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(text); }
                catch (JsonException)
                {
                    if (structured) throw Invalid();
                    return NormalizeLines(text);
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.String)
                        text = root.GetString()!;
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        // Never concatenate fields: speaker and other metadata must not be spoken.
                        var fields = root.EnumerateObject().Where(p => p.Name is "text" or "translation" or "translated_text").ToArray();
                        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String) throw Invalid();
                        text = fields[0].Value.GetString()!;
                    }
                    else throw Invalid();
                }
                continue;
            }
            return NormalizeLines(text);
        }
        throw Invalid();
    }

    private static string NormalizeLines(string text)
    {
        // Repair literal model-generated line escapes, without general-purpose unescaping.
        text = Regex.Replace(text, @"(?<!\\)\\r\\n|(?<!\\)\\[rn]", "\n");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) throw Invalid();
        return text;
    }

    private static InvalidOperationException Invalid() => new("翻譯回覆格式不完整或冇可用譯文，請重新翻譯。");
}
