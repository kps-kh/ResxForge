using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;

class Program
{
    // ======================
    // CONFIG (HARD-CODED)
    // ======================

    private static string OllamaModel = "aisingapore/Gemma-SEA-LION-v4-27B-IT:latest";
    private const string OllamaUrl = "http://127.0.0.1:11434/api/generate";
    private const string Excluded = "";
//>
    //private const string ResxFolder = @"C:\Users\xxx\source\repos\ResxForge\Resources";
    //private const string ConfigFolder = @"C:\Users\xxx\source\repos\ResxForge\config";
    //private const string CacheFolder = @"C:\Users\xxx\source\repos\ResxForge\cache";

    private static readonly string ProjectRoot = GetProjectRoot();

    private static string GetProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, "config")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
            throw new DirectoryNotFoundException("Project root not found.");

        return dir.FullName;
    }

    private static readonly string ConfigFolder =
        Path.Combine(ProjectRoot, "config");

    private static readonly string CacheFolder =
        Path.Combine(ProjectRoot, "cache");

    private static readonly string ResxFolder =
        Path.Combine(ProjectRoot, "Resources");
//>
    private static readonly HashSet<string> ReviewLogExcludedPages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ""
        };
    private static readonly string ReviewLogPath =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "review.log"
    );

    private static readonly StringBuilder FinalLog = new();
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    private static Process? OllamaProcess;
    private static bool ForceOverwriteCache = false;

    static Program()
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) => StopOllama();
        Console.CancelKeyPress += (s, e) =>
        {
            StopOllama();
            e.Cancel = false;
        };
    }

    // ======================
    // LANGUAGES
    // ======================
    private static readonly List<string> Languages = new()
    {
        // === GROUP 1: SEA-LION-v4-27B ===
        // (Southeast Asian & East Asian focus)
        "km", "zh", "vi", "th", "ja", "lo", "ko", "id", "ms",

        // === GROUP 2: TranslateGemma-27B ===
        // (European & Western focus)
        "fr", "de", "es", "nl", "it", "pt", "cs", "sv", "ru", "hi"
    };

    private static IReadOnlyList<string> TargetLangs => Languages.Where(l => l != "en").ToList();

    private static readonly Dictionary<string, string> LangNames = new()
    {
        ["km"] = "Khmer",
        ["zh"] = "Simplified Chinese",
        ["vi"] = "Vietnamese",
        ["th"] = "Thai",
        ["de"] = "German",
        ["ja"] = "Japanese",
        ["fr"] = "French",
        ["id"] = "Indonesian",
        ["ms"] = "Malay",
        ["ko"] = "Korean",
        ["nl"] = "Dutch",
        ["it"] = "Italian",
        ["es"] = "Spanish",
        ["hi"] = "Hindi",
        ["ru"] = "Russian",
        ["pt"] = "Portuguese",
        ["cs"] = "Czech",
        ["lo"] = "Lao",
        ["sv"] = "Swedish"
    };

    // ======================
    // FIXED TRANSLATIONS FOR SPECIFIC KEYS
    // ======================
    private static readonly Dictionary<string, Dictionary<string, string>> KeyOverrides = new()
    {
        ["km"] = new()
        {
            ["Language"] = "ភាសាអង់គ្លេស"
        },
        ["zh"] = new()
        {
            ["The Khmer language has a unique sound and tone system, which can be difficult for learners to master. Our audio files feature native speakers pronouncing words and phrases correctly, allowing you to learn the correct intonation and rhythm."] = "高棉语拥有独特的语音和声调系统，这对学习者来说可能难以掌握。我们的音频文件由母语人士朗读单词和短语，帮助您学习正确的语调和节奏。",
            ["Additionally, Kampot's natural beauty and proximity to attractions like the Bokor National Park and Kep beach make it an ideal location for those who love outdoor activities. The city's relaxed pace allows expats to enjoy a balanced life, blending work with exploration and leisure."] = "此外，贡布的自然美景以及毗邻博科国家公园和白马海滩等景点的地理位置，使其成为户外运动爱好者的理想之地。这座城市悠闲的生活节奏让外籍人士能够享受平衡的生活，将工作、探索和休闲完美融合。"
        }
    };

    // ======================
    // GLOSSARY + ECHO WATCHERS
    // ======================

    private static FileSystemWatcher? GlossaryWatcher;
    private static FileSystemWatcher? EchoWatcher;


    // ======================
    // GLOSSARY CONFIG
    // ======================
    private static Dictionary<string, Dictionary<string, string>> Glossaries =
        new(StringComparer.OrdinalIgnoreCase);

    private static string GlossaryPath =
        Path.Combine(ConfigFolder, "glossary.json");

    private static void LoadGlossary()
    {
        try
        {
            if (!File.Exists(GlossaryPath))
            {
                Console.WriteLine("⚠ glossary.json not found.");
                return;
            }

            var json = File.ReadAllText(GlossaryPath);

            Glossaries = JsonSerializer.Deserialize<
                Dictionary<string, Dictionary<string, string>>
            >(json) ?? new();

            Console.WriteLine("📘 glossary.json loaded.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Glossary load failed: {ex.Message}");
        }
    }

    // ======================
    // ECHO CONFIG
    // ======================
    private class EchoConfig
    {
        public List<string> Global { get; set; } = new();
        public Dictionary<string, List<string>> Languages { get; set; } = new();
    }

    private static HashSet<string> GlobalEchoExclusions =
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, HashSet<string>> EchoExclusions =
        new(StringComparer.OrdinalIgnoreCase);

    private static string EchoPath =
        Path.Combine(ConfigFolder, "echo.json");

    private static void LoadEchoConfig()
    {
        try
        {
            if (!File.Exists(EchoPath))
            {
                Console.WriteLine("⚠ echo.json not found.");
                return;
            }

            var json = File.ReadAllText(EchoPath);
            var config = JsonSerializer.Deserialize<EchoConfig>(json);

            if (config == null) return;

            GlobalEchoExclusions =
                new HashSet<string>(config.Global ?? new(), StringComparer.OrdinalIgnoreCase);

            EchoExclusions =
                config.Languages?.ToDictionary(
                    k => k.Key,
                    v => new HashSet<string>(v.Value ?? new(), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                ) ?? new();

            Console.WriteLine("📘 echo.json loaded.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Echo load failed: {ex.Message}");
        }
    }

    // ======================
    // GENERIC HOT RELOAD (DEBOUNCED)
    // ======================
    private static void StartHotReload(
        string filePath,
        ref FileSystemWatcher? watcher,
        Action reloadAction,
        string label,
        int debounceMs = 300)
    {
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!)
            {
                Filter = Path.GetFileName(filePath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            Timer? timer = null;

            watcher.Changed += (_, __) =>
            {
                // Debounce rapid events
                timer?.Dispose();
                timer = new Timer(_ =>
                {
                    try
                    {
                        reloadAction();
                        Console.WriteLine($"♻ {label} changed — reloaded.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ {label} reload failed: {ex.Message}");
                    }
                }, null, debounceMs, Timeout.Infinite);
            };

            Console.WriteLine($"👀 {label} hot-reload enabled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ {label} watcher failed: {ex.Message}");
        }
    }

    // ======================
    // CACHE PER LANGUAGE
    // ======================
    private static Dictionary<string, string> Cache = new();
    private static string CurrentCacheFile = "";

    // ======================
    // MAIN
    // ======================
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Contains("-h"))
        {
            Console.WriteLine(@"
                ==============================
                TRANSLATION TOOL - HELP
                ==============================

                -l | translating only one language | Example: -l zh
                -p | Razor Page | -p seahorse or -p seahorse durian
                -d | add directoty to path | -d city or -d city offices
                -f | force overwrite cache, keep existing when used with -p | -f -p seahorse | only seahorse cache change

                ==============================
            ");
            return;
        }

        ForceOverwriteCache = args.Contains("-f");

        List<string> workingResxFolders = new() { ResxFolder };

        var dirArgIndex = Array.FindIndex(args, a => a == "-d");
        if (dirArgIndex >= 0)
        {
            workingResxFolders = new List<string>();

            for (int i = dirArgIndex + 1; i < args.Length && !args[i].StartsWith("-"); i++)
            {
                var inputDir = args[i];

                // Find matching subdirectory (case-insensitive)
                var match = Directory
                    .GetDirectories(ResxFolder)
                    .FirstOrDefault(d =>
                        string.Equals(
                            Path.GetFileName(d),
                            inputDir,
                            StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    workingResxFolders.Add(match);
                    Console.WriteLine($"📂 Using subdirectory: {Path.GetFileName(match)}");
                }
                else
                {
                    Console.WriteLine($"⚠ Subdirectory '{inputDir}' not found inside Resources. Skipping.");
                }
            }

            // Fallback if no valid dirs found
            if (!workingResxFolders.Any())
                workingResxFolders.Add(ResxFolder);
        }

        List<string>? specificResources = null;

        var pathArgIndex = Array.FindIndex(args, a => a == "-p");
        if (pathArgIndex >= 0)
        {
            specificResources = new List<string>();

            for (int i = pathArgIndex + 1; i < args.Length && !args[i].StartsWith("-"); i++)
            {
                specificResources.Add(args[i]);
            }

            if (specificResources.Any())
                Console.WriteLine($"📌 Translating only resources: {string.Join(", ", specificResources)}");
        }

        // Ensure cache/config folders exist
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(ConfigFolder);

        // Load JSON configs
        LoadGlossary();
        LoadEchoConfig();

        StartHotReload(
            GlossaryPath,
            ref GlossaryWatcher,
            LoadGlossary,
            "glossary.json");

        StartHotReload(
            EchoPath,
            ref EchoWatcher,
            LoadEchoConfig,
            "echo.json");

        // Multi-language override
        List<string> targetLangs = TargetLangs.ToList();
        var langArgIndex = Array.FindIndex(args, a => a == "-l");

        if (langArgIndex >= 0)
        {
            var selectedLangs = new List<string>();

            for (int i = langArgIndex + 1; i < args.Length && !args[i].StartsWith("-"); i++)
            {
                var lang = args[i].ToLower();

                if (Languages.Contains(lang))
                {
                    selectedLangs.Add(lang);
                }
                else
                {
                    Console.WriteLine($"⚠ Unknown language '{lang}', skipping.");
                }
            }

            if (selectedLangs.Any())
            {
                targetLangs = selectedLangs;
                Console.WriteLine($"🌍 Translating only to: {string.Join(", ", targetLangs)}");
            }
            else
            {
                Console.WriteLine("⚠ No valid languages provided after -l. Using all target languages.");
            }
        }

        Console.WriteLine("🚀 Starting translation...");

        if (!await IsOllamaRunning())
        {
            await StartOllamaServerAsync();
        }

        var baseFiles = new List<string>();

        foreach (var folder in workingResxFolders)
        {
            baseFiles.AddRange(
                Directory.EnumerateFiles(folder, "*.resx", SearchOption.AllDirectories)
                .Where(f =>
                    string.IsNullOrWhiteSpace(Excluded) ||
                    !f.Split(Path.DirectorySeparatorChar)
                    .Any(dir => dir.Equals(Excluded, StringComparison.OrdinalIgnoreCase))
                )
                .Where(f =>
                    !Languages.Any(l => f.EndsWith($".{l}.resx", StringComparison.OrdinalIgnoreCase))
                )
                .Where(f =>
                {
                    if (specificResources == null || !specificResources.Any())
                        return true;

                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(f);
                    return specificResources
                        .Any(r => fileNameWithoutExt.Equals(r, StringComparison.OrdinalIgnoreCase));
                })
            );
        }

        foreach (var baseFile in baseFiles)
        {
            Console.WriteLine($"\n📄 {Path.GetFileName(baseFile)}");
            var baseDoc = XDocument.Load(baseFile);
            var pageName = Path.GetFileNameWithoutExtension(baseFile);

            string lastModel = "";

            foreach (var lang in targetLangs)
            {
                string activeModel = (lang == "km" || lang == "lo" || lang == "th" || lang == "vi" || 
                                    lang == "zh" || lang == "ja" || lang == "ko" || lang == "id" || lang == "ms") 
                                    ? "aisingapore/Gemma-SEA-LION-v4-27B-IT:latest" 
                                    : "translategemma:27b";

                if (activeModel != lastModel && !string.IsNullOrEmpty(lastModel))
                {
                    Console.WriteLine($"🔄 MODEL SWITCH: Unloading {lastModel} and loading {activeModel}...");
                    Console.WriteLine("⏳ This may take 30-60 seconds ...");
                    Console.WriteLine();
                }
                lastModel = activeModel;

                CurrentCacheFile = Path.Combine(CacheFolder, $"cache_{lang}.json");
                LoadCache();

                Console.WriteLine($"🌍 {lang} (Using: {activeModel})");
                Console.WriteLine();
                
                var stopwatch = Stopwatch.StartNew();

                var newDoc = new XDocument(baseDoc);

                foreach (var data in newDoc.Descendants("data"))
                {
                    var value = data.Element("value");
                    if (value == null || string.IsNullOrWhiteSpace(value.Value))
                        continue;

                    var source = value.Value;
                    var key = data.Attribute("name")?.Value ?? "alt";

                    // Pass the activeModel to the translation function
                    var translated = await TranslateAsync(source, lang, key, pageName, activeModel);
                    if (translated != null)
                    {
                        value.Value = translated;
                    }
                }

                var outPath = baseFile.Replace(".resx", $".{lang}.resx");
                newDoc.Save(outPath);

                stopwatch.Stop();
                Console.WriteLine($"✅ Written {Path.GetFileName(outPath)} ({stopwatch.Elapsed.TotalSeconds:F2} sec)");
                Console.WriteLine();
            }

            if (!string.IsNullOrEmpty(lastModel))
            {
                await UnloadModelAsync(lastModel);
            }
        }

        WriteFinalLog(workingResxFolders, specificResources);
        Console.WriteLine("\n🎉 Done");
        Console.WriteLine("\nPress Enter to exit...");
        Console.ReadLine();
    }

    // ======================
    // TRANSLATE
    // ======================
    private static async Task<string?> TranslateAsync(string text, string lang, string key, string pageName, string modelName)
    {
        // 1. KEY OVERRIDES (Hard-coded in Program.cs)
        if (KeyOverrides.TryGetValue(lang, out var langOverrides) &&
            langOverrides.TryGetValue(key, out var fixedTranslation))
        {
            Console.WriteLine($"🔒 [Override {lang} {key}] {text}\n➡️ {fixedTranslation}");
            FinalLog.AppendLine($"{lang} {key} | {fixedTranslation}\n");
            return fixedTranslation;
        }

        // 2. GLOSSARY CHECK (From glossary.json)
        // We check if the 'key' (e.g., "welcome") exists for this language
        if (Glossaries.TryGetValue(lang, out var glossary) && 
            glossary.TryGetValue(key, out var glossaryValue))
        {
            Console.WriteLine($"📘 [Glossary Hit {lang} {key}] {text}\n➡️ {glossaryValue}");
            Console.WriteLine();
            FinalLog.AppendLine($"{lang} {key} | {glossaryValue} (Glossary)\n");
        
            // Optional: Save to cache so the AI doesn't run if the glossary entry is removed later
            var cleanTxt = text.Replace("\r", "").Replace("\n", " ").Trim();
            Cache[$"{lang}||{cleanTxt}"] = glossaryValue;
            SaveCache();
        
            return glossaryValue;
        }

        // 3. CACHE CHECK
        string cleanText = text.Replace("\r", "").Replace("\n", " ").Trim();
        var cacheKey = $"{lang}||{cleanText}";

        if (Cache.TryGetValue(cacheKey, out var cached) && !ForceOverwriteCache)
        {
            Console.WriteLine($"[Cache hit {lang} {key}] {text}\n➡️ {cached}\n");
            FinalLog.AppendLine($"{lang} {key} | {cached}\n");
            return cached;
        }

        // ---------- UPDATED PAYLOAD FOR HARDWARE OPTIMIZATION ----------
        var payload = new
        {
            model = modelName, // Uses the model passed from the loop
            prompt = BuildPrompt(text, lang),
            options = new {
                temperature = 0,
                num_thread = 8,   // Fixes the 50% CPU usage by saturating physical cores
                num_ctx = 4096    // Keeps memory bandwidth usage efficient
            },
            keep_alive = "5m" // Keeps the model ready for 5 minutes of inactivity
        };

        try
        {
            var response = await Http.PostAsync(
                OllamaUrl,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();

            var raw = await response.Content.ReadAsStringAsync();
            var result = new StringBuilder();

            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("response", out var r))
                        result.Append(r.GetString());
                }
                catch { }
            }

            var translated = result.ToString().Trim();

            // If the source is a single line but the AI dumped a whole list (like your 'Questions and answers' dump)
            if (!text.Contains("\n") && translated.Contains("\n"))
            {
                // Harvest only the first non-empty line
                var lines = translated.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();

                // If the first line is just the key repeated or a header, try to find the actual translation
                if (!string.IsNullOrEmpty(firstLine))
                {
                    translated = firstLine;
                    Console.WriteLine($"✂️ [Auto-Cleaned] Reduced list dump to: {translated}");
                }
            }

            if (!text.EndsWith(".") && !text.EndsWith("!") && !text.EndsWith("?"))
            {
                translated = translated.TrimEnd('.', '!', '?');
            }

            // Logic for Echo/Leakage detection remains the same...
            bool echo = IsEnglishEcho(text, translated);
            bool leak = HasScriptLeakage(lang, translated);

            if ((echo && !IsEchoExcluded(lang, text, translated)) || leak)
            {
                if (!ReviewLogExcludedPages.Contains(pageName))
                {
                    WriteReviewLog(pageName, lang, key, text, translated);
                    FinalLog.AppendLine($"⚠ {pageName} [{lang} {key}]\nSource: {text}\nOutput: {translated}\n");
                }
            }

            Cache[cacheKey] = translated;
            SaveCache();
            FinalLog.AppendLine($"{lang} {key} | {translated}\n");

            Console.WriteLine($"[{(ForceOverwriteCache ? "Rewrite" : "New")} {lang} {key}] {text}\n➡️ {translated}\n");

            return translated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Translation failed [{lang} {key}]: {ex.Message}\n");
            return null;
        }
    }

    // ======================
    // PROMPT
    // ======================
private static string BuildPrompt(string text, string lang)
{
    var langName = LangNames.GetValueOrDefault(lang, lang);

    // --- 1. NUMERIC FORMATTING ---
    string numberInstruction = lang switch
    {
        "km" => "Translate all digits into Khmer numerals (០-៩). For years, use Khmer numerals in AD format (e.g., '2024' to '២០២៤').",
        "zh" or "ja" => "Use Arabic numerals for years (e.g., '2024年'). For other numbers, use native characters if appropriate for formal context, otherwise maintain Arabic numerals.",
        "th" => "Translate all digits into Thai numerals. Convert years to Buddhist Era (add 543) before translating digits.",
        "lo" => "Translate all digits into Lao numerals (໐-໙). Convert years to Buddhist Era (add 543) before translating digits.",
        "fr" or "de" or "it" or "es" or "pt" or "ru" or "sv" or "nl" or "cs" =>
            $"For {langName}: Use Arabic numerals. Use a space or dot for thousands and a comma for decimals (European style).",
        "vi" => "Use Arabic numerals: use a dot (.) for thousands and a comma (,) for decimals. For years, always include the word 'năm' (e.g., 'năm 2024').",
        "hi" => "Use standard Arabic numerals (0-9). Devanagari numerals are not required for this modern UI context.",
        _ => "Maintain standard Arabic numerals and original numeric formatting."
    };

    // --- 2. SCRIPT-SPECIFIC CONSTRAINTS (ZWSP for KM/LO) ---
    string scriptInstruction = lang switch
    {
        "km" => "The output must be in Khmer Unicode. Use Zero Width Spaces (ZWSP) between words to ensure correct word wrapping in the UI.",
        "lo" => "The output must be in Lao Unicode. Use Zero Width Spaces (ZWSP) between words to ensure correct word wrapping in the UI.",
        _ => ""
    };

    // --- 3. STYLE & COMPOUND NOUNS (DE/NL/SV) ---
    string styleInstruction = lang switch
    {
        "de" or "nl" or "sv" => 
            $"- CRITICAL: Do NOT use hyphens (-) to join nouns. {langName} prefers compound words. " +
            "Examples of WRONG: 'Kampot-Bus', 'Durian-Frucht'. " +
            "Examples of CORRECT: 'Kampotbus', 'Durianfrucht'. " +
            "If unsure, use a single space, NEVER a hyphen.",
        _ => ""
    };

return $"""
You are a professional English translator to {langName} specializing in Software Resource Files (.resx).
Translate UI strings and labels accurately, maintaining the original meaning and technical style.

RULES:
- {numberInstruction}
- Translate symbols like '&' or '+' into the equivalent words in {langName} (e.g., 'and' or 'plus').
{(string.IsNullOrWhiteSpace(scriptInstruction) ? "" : "- " + scriptInstruction)}
{styleInstruction}
- Keep translations concise to fit UI elements.
- Do not add any punctuation at the end that is not present in the original text.
- Produce ONLY the translation. No explanations or commentary.
- Do NOT include any English words in the output unless they are proper nouns.
- The output must be fully written in {langName}.


SOURCE TEXT: "{text}"
{langName} TRANSLATION:
""";
}

    // ======================
    // CHECK / START OLLAMA
    // ======================
    private static async Task<bool> IsOllamaRunning()
    {
        try
        {
            var payload = new
            {
                model = OllamaModel,
                prompt = "ping",
                temperature = 0,
                max_tokens = 1
            };

            var response = await Http.PostAsync(
                OllamaUrl,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            );

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartOllamaServerAsync(int timeoutSeconds = 20)
    {
        if (OllamaProcess != null && !OllamaProcess.HasExited)
        {
            Console.WriteLine("⚡ Ollama already running.");
            return;
        }

        Console.WriteLine("⚡ Starting Ollama server...");
        
        OllamaProcess = new Process();
        OllamaProcess.StartInfo.FileName = "cmd.exe";
        OllamaProcess.StartInfo.Arguments = "/c ollama serve";
        OllamaProcess.StartInfo.CreateNoWindow = true;
        OllamaProcess.StartInfo.UseShellExecute = false;
        OllamaProcess.Start();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (await IsOllamaRunning())
            {
                Console.WriteLine("✅ Ollama server is ready.");
                return;
            }
            await Task.Delay(500);
        }

        Console.WriteLine("⚠ Timeout waiting for Ollama server. It may not be ready.");
    }

    private static void StopOllama()
    {
        try
        {
            if (OllamaProcess != null && !OllamaProcess.HasExited)
            {
                Console.WriteLine("🛑 Stopping Ollama server...");
                OllamaProcess.Kill();
                OllamaProcess.WaitForExit(3000); // optional wait for cleanup
            }
        }
        catch { }
    }

    // ======================
    // ECHO DETECTION
    // ======================
    private static bool IsEnglishEcho(string src, string trg)
    {
        string Normalize(string s) => string.Join(" ", s.ToLower().Split());
        var s = Normalize(src);
        var t = Normalize(trg);

        if (s == t)
            return true;

        int sameChars = s.Zip(t, (a, b) => a == b ? 1 : 0).Sum();
        double similarity = (double)sameChars / Math.Max(s.Length, t.Length);

        return similarity > 0.9;
    }

    private static bool HasScriptLeakage(string lang, string text)
    {
        return lang switch
        {
            "km" or "lo" or "th" or "ru" or "hi" or "zh" or "ja" or "ko"
                // Flags English letters [A-Za-z] OR the ampersand [&]
                => Regex.IsMatch(text, "[A-Za-z]|&"), 
            _ => false
        };
    }

    private static bool IsEchoExcluded(string lang, string source, string translated)
    {
        var src = source.Trim();
        var trg = translated.Trim();

        if (!string.Equals(src, trg, StringComparison.OrdinalIgnoreCase))
            return false;

        if (GlobalEchoExclusions.Contains(src))
            return true;

        if (EchoExclusions.TryGetValue(lang, out var set)
            && set.Contains(src))
            return true;

        return false;
    }

    // ======================
    // REVIEW LOG
    // ======================
    private static void WriteReviewLog(string pageName, string lang, string key, string source, string output)
    {
        try
        {
            var entry = new StringBuilder();
            entry.AppendLine($"⚠ {pageName} [{lang} {key}]");
            entry.AppendLine($"Source: {source}");
            entry.AppendLine($"Output: {output}");
            entry.AppendLine(new string('-', 60));

            File.AppendAllText(ReviewLogPath, entry.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Failed to write review log: {ex.Message}");
        }
    }

    // ======================
    // FINAL LOG
    // ======================

    private static void WriteFinalLog(List<string> folders, List<string>? resources)
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            string name;

            if (resources != null && resources.Any())
            {
                name = string.Join("_", resources);
            }
            else if (folders != null && folders.Count == 1)
            {
                name = Path.GetFileName(folders.First());
            }
            else
            {
                name = "FullTranslation";
            }

            var fileName = $"{name}.log";
            var fullPath = Path.Combine(desktopPath, fileName);

            File.WriteAllText(fullPath, FinalLog.ToString(), Encoding.UTF8);

            Console.WriteLine($"📝 Log written to: {fullPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Failed to write final log: {ex.Message}");
        }
    }

    // ======================
    // CACHE HANDLING PER LANGUAGE
    // ======================
    private static void LoadCache()
    {
        // Initialize with OrdinalIgnoreCase so "Kampot" matches "kampot"
        Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(CurrentCacheFile) && File.Exists(CurrentCacheFile))
        {
            try
            {
                var rawJson = File.ReadAllText(CurrentCacheFile);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(rawJson);

                if (loaded != null)
                {
                    // We must move items into the Case-Insensitive dictionary
                    foreach (var kvp in loaded)
                    {
                        // Clean the key (remove newlines/tabs) while loading
                        string cleanKey = kvp.Key.Replace("\r", "").Replace("\n", " ").Trim();
                        Cache[cleanKey] = kvp.Value;
                    }
                    Console.WriteLine($"🗂 Loaded cache [{Path.GetFileName(CurrentCacheFile)}] with {Cache.Count} entries");
                }
            }
            catch
            {
                Console.WriteLine($"⚠ Failed to read cache [{Path.GetFileName(CurrentCacheFile)}], starting fresh");
            }
        }
        else
        {
            Console.WriteLine($"🗂 No existing cache [{Path.GetFileName(CurrentCacheFile)}], starting fresh");
        }
        Console.WriteLine();
    }

    private static void SaveCache()
    {
        if (string.IsNullOrEmpty(CurrentCacheFile)) return;

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                // CRITICAL: Keeps Japanese/Vietnamese characters readable in the file
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            File.WriteAllText(CurrentCacheFile, JsonSerializer.Serialize(Cache, options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Cache save error: {ex.Message}");
        }
    }

    private static async Task UnloadModelAsync(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return;

        try
        {
            var payload = new { model = modelName, keep_alive = 0 };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            
            await Http.PostAsync(OllamaUrl, content);
            
            // Give the i7 and RAM a moment to settle after dropping 15GB+
            Console.WriteLine($"\n🧠 CPU Memory Purged: {modelName}. Waiting for system to stabilize...");
            await Task.Delay(3000); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n⚠ Could not flush CPU memory: {ex.Message}");
        }
    }
}