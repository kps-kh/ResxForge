using System.Text.RegularExpressions;

namespace ResxForge;

    internal static class PromptService
    {
        internal static string BuildPrompt(string text, string lang, string langName, Dictionary<string, string>? glossary = null)
        {
            string glossaryInstruction = GetGlossaryInstruction(text, glossary);
            string numberInstruction = GetNumberInstruction(lang, langName);
            string styleInstruction = GetStyleInstruction(lang, langName);

return $"""
[INST]
You are a professional English translator to {langName} specializing in Software Resource Files (.resx).
Translate UI strings and labels accurately, maintaining the original meaning and technical style.

RULES:
{glossaryInstruction}
- {numberInstruction}
- Translate symbols like '&' or '+' into the equivalent words in {langName}.
{styleInstruction}
- Produce ONLY the translation. No explanations or [meta] tags.
- NO conversational filler (e.g., "Sure", "Here is the translation").
- NO quotation marks.
- Do NOT include any English words in the output if a glossary translation is provided above.
- The output must be fully written in {langName}.
[/INST]


{text}
""";
}
    internal static string GetGlossaryInstruction(string text, Dictionary<string, string>? glossary)
    {
        if (glossary == null) return "";

        var relevantTerms = glossary
            .Where(kvp => text.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!relevantTerms.Any()) return "";

        var termsList = string.Join("\n", relevantTerms.Select(kvp => $"- {kvp.Key} -> {kvp.Value}"));
        return $"\nCRITICAL GLOSSARY (Use these exact terms):\n{termsList}\n";
    }

    internal static string GetNumberInstruction(string lang, string langName) => lang switch
    {
        "km" => "Preserve all numeric values exactly. Display digits using Khmer numerals (០-៩), including years in AD format (e.g., '2024' becomes '២០២៤').",
        "zh" or "ja" => "Use Arabic numerals for years (e.g., '2024年'). For other numbers, use native characters if appropriate for formal context, otherwise maintain Arabic numerals.",
        "th" => "Preserve all numeric values exactly. Do NOT perform arithmetic or convert calendar systems. Display digits using Thai numerals (๐-๙).",
        "lo" => "Preserve all numeric values exactly. Do NOT perform arithmetic or convert calendar systems. Display digits using Lao numerals (໐-໙).",
        "fr" or "de" or "it" or "es" or "pt" or "ru" or "sv" or "nl" or "cs" =>
            $"For {langName}: Use Arabic numerals. Use a space or dot for thousands and a comma for decimals (European style).",
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
        public static NumericContext Preprocess(string text, string lang)
        {
            var context = new NumericContext();
            var placeholders = new Dictionary<string, string>();
            int counter = 0;

            string processed = Regex.Replace(text, @"\b\d+\b", match =>
            {
                int number = int.Parse(match.Value);

                if (lang == "th" && number >= 1000 && number <= 2099)
                {
                    number += 543;
                }

                string placeholder = $"__NUM{counter}__";
                placeholders[placeholder] = number.ToString();
                counter++;

                return placeholder;
            });

            context.ProcessedText = processed;
            context.Placeholders = placeholders;
            return context;
        }

        internal static string Postprocess(string translated, NumericContext context, string lang)
        {
            string result = translated;

            foreach (var pair in context.Placeholders)
            {
                result = result.Replace(pair.Key, pair.Value);
            }

            if (lang == "th")
                result = ConvertThaiDigits(result);

            if (lang == "lo")
                result = ConvertLaoDigits(result);

            if (lang == "km")
                result = ConvertKhmerDigits(result);

            return result;
        }

        private static string ConvertThaiDigits(string s) =>
            s.Replace("0", "๐")
             .Replace("1", "๑")
             .Replace("2", "๒")
             .Replace("3", "๓")
             .Replace("4", "๔")
             .Replace("5", "๕")
             .Replace("6", "๖")
             .Replace("7", "๗")
             .Replace("8", "๘")
             .Replace("9", "๙");

        private static string ConvertLaoDigits(string s) =>
            s.Replace("0", "໐")
             .Replace("1", "໑")
             .Replace("2", "໒")
             .Replace("3", "໓")
             .Replace("4", "໔")
             .Replace("5", "໕")
             .Replace("6", "໖")
             .Replace("7", "໗")
             .Replace("8", "໘")
             .Replace("9", "໙");

        private static string ConvertKhmerDigits(string s) =>
            s.Replace("0", "០")
             .Replace("1", "១")
             .Replace("2", "២")
             .Replace("3", "៣")
             .Replace("4", "៤")
             .Replace("5", "៥")
             .Replace("6", "៦")
             .Replace("7", "៧")
             .Replace("8", "៨")
             .Replace("9", "៩");
    }
}