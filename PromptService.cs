using System.Text.RegularExpressions;

namespace Translate;

internal static class PromptService
{
    internal static string BuildPrompt(string text, string lang, string langName, Dictionary<string, string> cache, Dictionary<string, string>? glossary = null)
    {
        string historyInstruction = GetHistoryContext(text, lang, cache);
        string glossaryInstruction = GetGlossaryInstruction(text, glossary);
        string numberInstruction = GetNumberInstruction(lang, langName);
        string styleInstruction = GetStyleInstruction(lang, langName);

return $"""
[INST]
You are a professional English translator to {langName} specializing in Software Resource Files (.resx).
Translate UI strings and labels accurately, maintaining the original meaning and technical style.

RULES:
{historyInstruction}
{glossaryInstruction}
- {numberInstruction}
{styleInstruction}
- CRITICAL: Keep the translation length similar to the source text to prevent UI overflow.
- If the source is a single word or short phrase, the translation MUST also be short.
- Translate symbols like '&' or '+' into the equivalent words in {langName}.
- Produce ONLY the translation. No explanations or [meta] tags.
- NO conversational filler.
- NO quotation marks.
- The output must be fully written in {langName}.
[/INST]


{text}
""";
    }

    private static string GetHistoryContext(string text, string lang, Dictionary<string, string> cache)
        {
            var langPrefix = $"{lang}||";
            
            var examples = cache
                .Where(kvp => kvp.Key.StartsWith(langPrefix))
                .Select(kvp => new { 
                    Original = kvp.Key.Replace(langPrefix, ""), 
                    Translated = kvp.Value 
                })

                .Where(x => IsRelevant(text, x.Original))
                .Take(3)
                .Select(x => $"- Source: {x.Original} -> Translation: {x.Translated}")
                .ToList();

            if (!examples.Any()) return "";

            return $"\nREFERENCE EXAMPLES (Maintain this style and terminology):\n{string.Join("\n", examples)}\n";
        }

    private static bool IsRelevant(string currentText, string cachedKey)
    {

        if (currentText.Contains(cachedKey, StringComparison.OrdinalIgnoreCase)) return true;

        if (currentText.Length > 15)
        {
            var currentWords = currentText.Split(' ').Where(w => w.Length > 3).ToHashSet();
            var cachedWords = cachedKey.Split(' ').Where(w => w.Length > 3).ToHashSet();
            return currentWords.Intersect(cachedWords).Count() >= 2;
        }
        return false;
    }

    internal static string GetGlossaryInstruction(string text, Dictionary<string, string>? langGlossary)
    {

        if (langGlossary == null) return "";

        var relevantTerms = langGlossary
            .Where(kvp => text.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!relevantTerms.Any()) return "";

        var termsList = string.Join("\n", relevantTerms.Select(kvp => $"- {kvp.Key} -> {kvp.Value}"));
        return $"\nCRITICAL GLOSSARY (Use these exact terms):\n{termsList}\n";
    }

    internal static string GetNumberInstruction(string lang, string langName) => lang switch
    {
        "km" => "Use Khmer numerals (០-៩) ONLY for dates and years (e.g., '២០២៦'). For all other technical values, prices, and IDs, maintain Arabic numerals (0-9). Do NOT perform arithmetic or convert calendar systems.",
        "zh" or "ja" => "Use Arabic numerals for years (e.g., '2024年'). For other numbers, use native characters if appropriate for formal context, otherwise maintain Arabic numerals.",
        "th" => "Preserve all numeric values exactly. Do NOT perform arithmetic or convert calendar systems. Display digits using Thai numerals (๐-๙).",
        "lo" => "Preserve all numeric values exactly. Do NOT perform arithmetic or convert calendar systems. Display digits using Lao numerals (໐-໙).",
        "sv" => 
            $"For {langName}: Use Arabic numerals. Use a space for thousands and a comma for decimals (e.g., 1 234,56).",
        "fr" or "de" or "it" or "es" or "pt" or "ru" or "nl" or "cs" =>
            $"For {langName}: Use Arabic numerals. Use a dot (.) for thousands and a comma (,) for decimals (e.g., 1.234,56).",
        "vi" => "Use Arabic numerals: use a dot (.) for thousands and a comma (,) for decimals. For years, always include the word 'năm' (e.g., 'năm 2024').",
        "hi" => "Use standard Arabic numerals (0-9). Devanagari numerals are not required for this modern UI context.",
        _ => "Maintain standard Arabic numerals and original numeric formatting."
    };

    internal static string GetStyleInstruction(string lang, string langName) => lang switch
    {
        "de" or "nl" or "sv" => 
            $"\n- CRITICAL: Do NOT use hyphens (-) to join nouns. {langName} prefers compound words. " +
            "Examples of WRONG: 'Bus-Station', 'Durian-Frucht'. " +
            "Examples of CORRECT: 'Bus Station', 'Durianfrucht'. " +
            "If unsure, use a single space, NEVER a hyphen.",
        _ => ""
    };

    internal static string SanitizeOutput(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        string cleaned = input
            .Replace("\u200B", "")
            .Replace("\u200C", "")
            .Replace("\u200D", "") 
            .Replace("\uFEFF", ""); 

        cleaned = Regex.Replace(cleaned, @"\[.*?\]", "");

        cleaned = cleaned.Replace("➡️", "").Replace("->", "");

        char[] charsToTrim = { ' ', '"', '\'', '„', '“', '”', '「', '」', '\n', '\r', '\t' };
    
        return cleaned.Trim(charsToTrim);
    }

    internal class NumericContext
    {
        public string ProcessedText = "";
        public Dictionary<string, string> Placeholders = new();
    }

    internal static class NumericProcessor
    {
        private static readonly Regex NumRegex = new Regex(@"\d{4}s|\d+(?:,\d{3})*(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex PlaceholderRegex = new Regex(@"\[\[NUM(\d+)\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static NumericContext Preprocess(string text, string lang)
        {
            var context = new NumericContext();
            int counter = 0;

            context.ProcessedText = NumRegex.Replace(text, match =>
            {
                string val = match.Value;

                // Remove trailing 's' for decades
                if (val.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                    val = val.Substring(0, val.Length - 1);

                string cleanNum = val.Replace(",", "");

                if (lang == "th" && long.TryParse(cleanNum, out long number))
                {
                    if (number >= 1000 && number <= 2099)
                        val = (number + 543).ToString();
                }

                string placeholder = $"[[NUM{counter}]]";
                context.Placeholders[placeholder] = val;
                counter++;
                return placeholder;
            });

            return context;
        }

        internal static string Postprocess(string translated, NumericContext context, string lang)
        {
            if (context?.Placeholders == null) return translated;

            return PlaceholderRegex.Replace(translated, match =>
            {
                string key = match.Value;
                var foundKey = context.Placeholders.Keys
                    .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

                if (foundKey == null) return key;

                string val = context.Placeholders[foundKey];

                return lang switch
                {
                    "th" => ConvertDigits(val, "๐๑๒๓๔๕๖๗๘๙"),
                    "lo" => ConvertDigits(val, "໐໑໒໓໔໕໖໗໘໙"), 
                    "km" => ConvertDigits(val, "០១២៣៤៥៦៧៨៩"),
                    _ => val
                };
            });
        }

        private static string ConvertDigits(string input, string nativeDigits)
        {
            if (string.IsNullOrEmpty(input)) return input;
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '0' && chars[i] <= '9')
                    chars[i] = nativeDigits[chars[i] - '0'];
            }
            return new string(chars);
        }
    }
}