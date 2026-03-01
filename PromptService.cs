using System.Text.Json;
using System.Text.RegularExpressions;

namespace Translate;

internal static class PromptService
{
    internal static string BuildPrompt(string text, string lang, string langName, Dictionary<string, string> cache, Dictionary<string, string>? glossary = null)
    {
        var relevantGlossary = GetRelevantGlossary(text, glossary);
        var relevantNT = NoTranslateList.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        string historyInstruction = GetHistoryContext(text, lang, cache, relevantGlossary);
        string glossaryInstruction = FormatGlossaryInstruction(relevantGlossary);
        string numberInstruction = GetNumberInstruction(lang, langName);
        string styleInstruction = GetStyleInstruction(lang, langName);
        string glossaryPart = glossaryInstruction.Trim();
        string historyPart = historyInstruction.Trim();
        string stylePart = styleInstruction.Trim();
        string ntInstruction = relevantNT.Any() 
            ? $"\nSTRICT: Do NOT translate or modify these terms: {string.Join(", ", relevantNT)}" 
            : "";
        string numberLine = $"- {numberInstruction.Trim()}\n";
        string styleLine = !string.IsNullOrEmpty(stylePart) ? $"- {stylePart}\n" : "";
        string placeholderLine = (text.Contains("[[TERM") || text.Contains("[[NUM"))
            ? "- CRITICAL: Do NOT translate text inside [[brackets]].\n- Keep [[NUM0]] exactly as it is.\n"
            : "";

return $"""
[INST]
You are a professional English to {langName} translator for .resx software files.

RULES:
1. GLOSSARY: {(string.IsNullOrEmpty(glossaryPart) ? "None provided." : glossaryPart)}{(string.IsNullOrEmpty(ntInstruction) ? "" : "\n" + ntInstruction.Trim())}
2. CONTEXT: {(string.IsNullOrEmpty(historyPart) ? "No previous examples." : historyPart)}

CONSTRAINTS:
{numberLine}{styleLine}{placeholderLine}- Keep translation length similar to source.
- NO explanations, NO quotes, NO [meta] tags.
- The output MUST be written in {langName}.
[/INST]


{text}
""";
    }

    private static Dictionary<string, string> GetRelevantGlossary(string text, Dictionary<string, string>? glossary)
    {
        if (glossary == null) return new Dictionary<string, string>();

        return glossary
            .Where(kvp => text.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static string FormatGlossaryInstruction(Dictionary<string, string> relevantTerms)
    {
        if (!relevantTerms.Any()) return "";
        var termsList = string.Join("\n", relevantTerms.Select(kvp => $"* {kvp.Key} == {kvp.Value}"));
        return $"\nCRITICAL TERM MAPPING (Mandatory):\n{termsList}\n- You MUST use these values.\n";
    }

    private static string GetHistoryContext(string text, string lang, Dictionary<string, string> cache, Dictionary<string, string> relevantGlossary)
    {
        var langPrefix = $"{lang}||";
        var currentWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => w.Length > 3)
                            .ToList();

        var examples = cache
            .Where(kvp => kvp.Key.StartsWith(langPrefix))
            .Select(kvp => new { 
                Original = kvp.Key.Replace(langPrefix, ""), 
                Translated = kvp.Value 
            })
            .Where(x => {
                bool sharesGlossary = relevantGlossary.Keys.Any(gk => x.Original.Contains(gk, StringComparison.OrdinalIgnoreCase));
                bool sharesWords = currentWords.Any(cw => x.Original.Contains(cw, StringComparison.OrdinalIgnoreCase));
                bool isSubstring = text.Contains(x.Original, StringComparison.OrdinalIgnoreCase) || x.Original.Contains(text, StringComparison.OrdinalIgnoreCase);
                
                return sharesGlossary || sharesWords || isSubstring;
            })
            .OrderByDescending(x => {
                int glossaryScore = relevantGlossary.Keys.Count(gk => x.Original.Contains(gk, StringComparison.OrdinalIgnoreCase));
            
                int wordScore = currentWords.Count(cw => x.Original.Contains(cw, StringComparison.OrdinalIgnoreCase));
                
                return (glossaryScore * 10000) + (wordScore * 1000) + x.Original.Length;
            })
            .Take(5) 
            .Select(x => {
                string orig = x.Original.Length > 203 ? x.Original.Substring(0, 200) + "..." : x.Original;
                string trans = x.Translated.Length > 203 ? x.Translated.Substring(0, 200) + "..." : x.Translated;
                return $"- {orig} => {trans}";
            })
            .ToList();

        if (!examples.Any()) return "No previous examples.";
        return $"\nPREVIOUS STYLE EXAMPLES:\n{string.Join("\n", examples)}\n";
    }

    internal static string GetNumberInstruction(string lang, string langName) => lang switch
    {
        "km" => "Use Khmer numerals (០-៩) ONLY for dates and years (e.g., '២០២៦'). For all other technical values, prices, and IDs, maintain Arabic numerals (0-9). Do NOT perform arithmetic or convert calendar systems.",
        "zh" => "For Chinese: Use Arabic numerals for years (e.g., '2026年'). Use standard Arabic numerals for most technical contexts. Maintain original formatting for measurements.",
        "th" => "The year/number in [[NUM0]] is already formatted for Thai context. Keep it exactly as provided. Display digits using Thai numerals (๐-๙).",
        "ja" => "For Japanese: Use Arabic numerals for years and centuries (e.g., '2026年', '21世紀'). Use Arabic numerals for all technical values, counts, and measurements (e.g., '5MB', '12人'). Only use Kanji numerals (一, 二, 三) if they are part of a fixed formal name or idiom.",
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
        "ja" => 
            "STYLE: Use a half-width space (standard space) between Japanese characters and English words or Arabic numerals (e.g., '20 世紀' or 'Windows 11').\n" +
            "- TERMINOLOGY: Use Katakana for technical loanwords.\n" +
            "- PUNCTUATION: Use Japanese full-width punctuation (。 and 、) instead of (. and ,).",
        "zh" =>
            "STYLE: Do NOT use spaces between Chinese characters and English/Numbers. " +
            "\n- PUNCTUATION: Use Chinese full-width punctuation (。，？！).",

        _ => ""
    };

    internal static string SanitizeOutput(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        string cleaned = input
            .Replace("\u200B", "").Replace("\u200C", "")
            .Replace("\u200D", "").Replace("\uFEFF", ""); 

        cleaned = Regex.Replace(cleaned, @"(?<!\[)\[[^\[\]]+\](?!\])", "");

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

        private static readonly string[] Units = { 
            "MB", "GB", "KB", "TB", "PB", "BYTE", "BYTES", "BIT", "BITS",
            "M", "KM", "CM", "MM", "NM", "METER", "METERS", "HECTARE", "HECTARES", 
            "INCH", "INCHES", "FT", "FEET", "MILE", "MILES",
            "KG", "G", "MG", "LB", "OZ", "L", "ML",
            "MS", "S", "SEC", "MIN", "HR", "HOUR", "HOURS", "DAY", "DAYS", 
            "WEEK", "WEEKS", "MONTH", "MONTHS", "HZ", "KHZ", "GHZ", "FPS",
            "PX", "PT", "DPI", "PPI", "VH", "VW", "REM", "EM", "DP", "SP",
            "$", "€", "£", "¥", "฿", "%", "PERCENT"
        };

        public static NumericContext Preprocess(string text, string lang)
        {
            var context = new NumericContext();
            int counter = 0;

            context.ProcessedText = NumRegex.Replace(text, match =>
            {
                string val = match.Value;
        
                if (val.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                    val = val.Substring(0, val.Length - 1);

                string cleanNum = val.Replace(",", "");

                if (lang == "th" && long.TryParse(cleanNum, out long number))
                {
                    bool hasUnit = Units.Any(u => 
                        (u.Length == 1 && text.Contains(u)) || 

                        Regex.IsMatch(text, $@"\b{Regex.Escape(u)}\b", RegexOptions.IgnoreCase)
                    );

                    if (number >= 1000 && number <= 2099 && !hasUnit)
                    {
                        val = (number + 543).ToString();
                    }
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

    private static readonly HashSet<string> NoTranslateList = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string NoTranslatePath = Path.Combine(Program.ConfigFolder, "no_translate.json");

    static PromptService()
    {
        LoadNoTranslateRules();
    }

    private static void LoadNoTranslateRules()
    {
        try
        {
            if (!File.Exists(NoTranslatePath)) 
            {
                Console.WriteLine($"⚠️ NT File not found at: {NoTranslatePath}");
                return;
            }

            var json = File.ReadAllText(NoTranslatePath);
            var data = JsonSerializer.Deserialize<NoTranslateData>(json);
            
            if (data?.no_translate != null)
            {
                NoTranslateList.Clear();
                foreach (var term in data.no_translate) NoTranslateList.Add(term);
                //Console.WriteLine($"🛡️ PromptService: {NoTranslateList.Count} no_translate loaded");
            }
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"⚠️ NT Load Error: {ex.Message}"); 
        }
    }

    private class NoTranslateData { public List<string>? no_translate { get; set; } }
}