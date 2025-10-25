using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OllamaChatLib;

namespace SemanticLib;

public sealed class SemanticSummaryBuilder
{
    readonly string _corpusPath;
    readonly string _summaryPath;
    readonly SemanticSummaryBuilderOptions _opts;
    readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    static readonly string[] BackendIndicators =
    {
        "domain","service","repository","db","database","infrastructure","data","api","controller",
        "context","entity","usecase","use-case","application","model","manager","handler"
    };

    public SemanticSummaryBuilder(string corpusPath, string summaryPath, SemanticSummaryBuilderOptions? opts = null)
    {
        _corpusPath = corpusPath;
        _summaryPath = summaryPath;
        _opts = opts ?? new SemanticSummaryBuilderOptions();
    }

    public async Task<SemanticSummaryStore> LoadOrBuildAsync(OllamaChatClient client, CancellationToken ct = default)
    {
        var corpusInfo = new FileInfo(_corpusPath);
        if (!corpusInfo.Exists) throw new FileNotFoundException("Corpus not found", _corpusPath);

        var summaryInfo = new FileInfo(_summaryPath);
        if (summaryInfo.Exists && summaryInfo.LastWriteTimeUtc >= corpusInfo.LastWriteTimeUtc)
        {
            return SemanticSummaryStore.Load(_summaryPath);
        }

        var nodes = await BuildAsync(client, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(_summaryPath)!);
        await using (var stream = new FileStream(_summaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var node in nodes)
            {
                var json = JsonSerializer.Serialize(node, _serializerOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
            }
        }

        return new SemanticSummaryStore(nodes);
    }

    async Task<IReadOnlyList<SemanticNodeSummary>> BuildAsync(OllamaChatClient client, CancellationToken ct)
    {
        Console.WriteLine("[semantic] Building semantic summaries...");
        var seeds = CollectSeeds(ct);
        Console.WriteLine($"[semantic] Collected {seeds.Count} groups for summarization");

        var selected = seeds
            .OrderByDescending(s => s.IsBackend)
            .ThenByDescending(s => s.Sample.Length)
            .Take(_opts.MaxGroups)
            .ToList();

        var results = new List<SemanticNodeSummary>(selected.Count);
        int counter = 0;
        foreach (var seed in selected)
        {
            ct.ThrowIfCancellationRequested();
            counter++;
            Console.WriteLine($"[semantic] Summarizing group {counter}/{selected.Count}: {seed.Id}");
            var summary = await SummarizeAsync(seed, client, ct).ConfigureAwait(false);
            results.Add(summary);
        }

        return results;
    }

    List<SummarySeed> CollectSeeds(CancellationToken ct)
    {
        var map = new Dictionary<string, SummarySeedBuilder>(StringComparer.OrdinalIgnoreCase);
        using var fs = new FileStream(_corpusPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, new UTF8Encoding(false));
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument? jd = null;
            try
            {
                jd = JsonDocument.Parse(line);
                var root = jd.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "file") continue;
                if (!root.TryGetProperty("path", out var pathEl)) continue;
                if (!root.TryGetProperty("text", out var textEl)) continue;
                var path = pathEl.GetString();
                var text = textEl.GetString();
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(text)) continue;

                var groupId = DeriveGroupId(path);
                if (groupId == null) continue;

                if (!map.TryGetValue(groupId, out var builder))
                {
                    builder = new SummarySeedBuilder(groupId);
                    map[groupId] = builder;
                }

                builder.Paths.Add(path);
                builder.IsBackend |= IsBackendPath(path);
                builder.AppendSample(text, _opts.MaxSampleChars);
            }
            catch
            {
                // Skip malformed entries
            }
            finally
            {
                jd?.Dispose();
            }
        }

        return map.Values
            .Where(b => b.Sample.Length >= _opts.MinSampleChars)
            .Select(b => b.ToSeed())
            .ToList();
    }

    static string? DeriveGroupId(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var depth = Math.Min(segments.Length, 2);
        return string.Join('/', segments.Take(depth));
    }

    static bool IsBackendPath(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var keyword in BackendIndicators)
        {
            if (lower.Contains(keyword)) return true;
        }
        return false;
    }

    async Task<SemanticNodeSummary> SummarizeAsync(SummarySeed seed, OllamaChatClient client, CancellationToken ct)
    {
        try
        {
            var prompt = BuildSummaryPrompt(seed);
            var messages = new[]
            {
                new OllamaChatClient.ChatMessage("system",
                    "You summarize code modules for retrieval. Respond with strict JSON only. No commentary."),
                new OllamaChatClient.ChatMessage("user", prompt)
            };

            var response = await client.ChatAsync(messages, ct).ConfigureAwait(false);
            var json = ExtractJson(response);
            if (json == null) throw new InvalidOperationException("No JSON in model response");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() ?? seed.Id
                : seed.Id;
            var summary = root.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() ?? seed.FallbackSummary
                : seed.FallbackSummary;
            var responsibilities = root.TryGetProperty("responsibilities", out var rEl)
                ? ReadStringArray(rEl)
                : seed.FallbackResponsibilities;
            var keywords = root.TryGetProperty("keywords", out var kEl)
                ? ReadStringArray(kEl)
                : seed.FallbackKeywords;
            var classification = root.TryGetProperty("classification", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? NormaliseClassification(cEl.GetString())
                : seed.IsBackend ? "backend" : "shared";

            return new SemanticNodeSummary
            {
                Id = seed.Id,
                Title = title,
                Summary = summary,
                Responsibilities = responsibilities,
                Keywords = keywords,
                Classification = classification,
                Paths = seed.Paths.ToArray()
            };
        }
        catch
        {
            return new SemanticNodeSummary
            {
                Id = seed.Id,
                Title = seed.Id,
                Summary = seed.FallbackSummary,
                Responsibilities = seed.FallbackResponsibilities,
                Keywords = seed.FallbackKeywords,
                Classification = seed.IsBackend ? "backend" : "shared",
                Paths = seed.Paths.ToArray()
            };
        }
    }

    static string BuildSummaryPrompt(SummarySeed seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Group: {seed.Id}");
        sb.AppendLine("Representative files:");
        foreach (var path in seed.Paths.Take(5))
        {
            sb.AppendLine("- " + path);
        }
        sb.AppendLine();
        sb.AppendLine("Source excerpts:\n```\n" + seed.Sample.ToString().Trim() + "\n```");
        sb.AppendLine();
        sb.AppendLine("Return JSON with keys: title, summary, responsibilities (array), keywords (array), classification (backend|frontend|shared).");
        sb.AppendLine("Summaries must highlight data flow, dependencies, and main responsibilities. Prefer backend classification when unsure.");
        return sb.ToString();
    }

    static IReadOnlyList<string> ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(value.Trim());
                }
            }
        }
        return list.Count > 0 ? list : Array.Empty<string>();
    }

    static string NormaliseClassification(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "backend" => "backend",
            "front-end" => "frontend",
            "frontend" => "frontend",
            "ui" => "frontend",
            "infra" => "shared",
            "infrastructure" => "shared",
            _ => "shared"
        };
    }

    static string? ExtractJson(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return response[start..(end + 1)];
        }
        return null;
    }

    sealed class SummarySeedBuilder
    {
        public SummarySeedBuilder(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public List<string> Paths { get; } = new();
        public bool IsBackend { get; set; }
        public StringBuilder Sample { get; } = new();

        public void AppendSample(string text, int maxChars)
        {
            if (Sample.Length >= maxChars) return;
            var remaining = maxChars - Sample.Length;
            if (text.Length > remaining)
            {
                Sample.Append(text.AsSpan(0, remaining));
            }
            else
            {
                Sample.Append(text);
            }
            Sample.AppendLine();
        }

        public SummarySeed ToSeed()
        {
            var cleanedPaths = Paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            return new SummarySeed(Id, cleanedPaths, Sample, IsBackend);
        }
    }

    sealed record SummarySeed(string Id, List<string> Paths, StringBuilder Sample, bool IsBackend)
    {
        public string FallbackSummary =>
            $"Module {Id} contains {Paths.Count} file(s) with focus on {(IsBackend ? "backend" : "shared")} responsibilities.";

        public IReadOnlyList<string> FallbackResponsibilities =>
            new[]
            {
                IsBackend
                    ? "Implements backend domain logic and data access"
                    : "Provides shared utilities or mixed responsibilities"
            };

        public IReadOnlyList<string> FallbackKeywords =>
            Paths
                .Select(path => System.IO.Path.GetFileNameWithoutExtension(path) ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(5)
                .ToArray();
    }
}

public sealed class SemanticSummaryBuilderOptions
{
    public int MaxGroups { get; init; } = 40;
    public int MaxSampleChars { get; init; } = 4000;
    public int MinSampleChars { get; init; } = 400;
}
