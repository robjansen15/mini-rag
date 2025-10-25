using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SemanticLib;

public sealed record SemanticNodeSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Classification { get; init; }
    public required IReadOnlyList<string> Responsibilities { get; init; }
    public required IReadOnlyList<string> Keywords { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public string SchemaVersion { get; init; } = SemanticSummaryBuilder.SummarySchemaVersion;

    public bool IsBackend => string.Equals(Classification, "backend", StringComparison.OrdinalIgnoreCase);
}

public sealed class SemanticSummaryStore
{
    readonly List<SemanticNodeSummary> _nodes;
    readonly List<NodeIndexEntry> _index;

    public SemanticSummaryStore(IEnumerable<SemanticNodeSummary> nodes)
    {
        _nodes = nodes.ToList();
        _index = _nodes.Select(BuildIndexEntry).ToList();
    }

    public IReadOnlyList<SemanticNodeSummary> Nodes => _nodes;

    public static SemanticSummaryStore Load(string path)
    {
        if (!File.Exists(path)) return new SemanticSummaryStore(Array.Empty<SemanticNodeSummary>());
        var nodes = new List<SemanticNodeSummary>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var node = JsonSerializer.Deserialize<SemanticNodeSummary>(line);
                if (node != null) nodes.Add(node);
            }
            catch
            {
                // Skip malformed lines
            }
        }
        return new SemanticSummaryStore(nodes);
    }

    public IReadOnlyList<ScoredNode> Search(QueryExpansion expansion, int topN = 5)
    {
        if (_index.Count == 0) return Array.Empty<ScoredNode>();
        var features = BuildQueryFeatures(expansion);
        var termWeights = BuildTermWeights(features);

        var scored = new List<ScoredNode>(_index.Count);
        foreach (var entry in _index)
        {
            var score = ScoreNode(entry, termWeights, features, expansion);
            if (score > 0f) scored.Add(new ScoredNode(entry.Node, score));
        }

        if (scored.Count == 0)
        {
            return _index
                .Select(e => new ScoredNode(e.Node, e.Node.IsBackend ? 1f : 0.5f))
                .OrderByDescending(s => s.Score)
                .Take(topN)
                .ToList();
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Node.Title, StringComparer.OrdinalIgnoreCase)
            .Take(topN)
            .ToList();
    }

    static Dictionary<string, float> BuildTermWeights(QueryFeatures features)
    {
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<string> tokens, float weight)
        {
            foreach (var token in tokens)
            {
                if (token.Length == 0) continue;
                weights[token] = weights.TryGetValue(token, out var existing) ? Math.Max(existing, weight) : weight;
            }
        }

        Add(features.PrimaryTokens, 1.0f);
        Add(features.AdditionalTokens, 1.2f);
        Add(features.PathTokens, 1.1f);
        Add(features.Dependencies, 1.3f);

        return weights;
    }

    static float ScoreNode(NodeIndexEntry entry, Dictionary<string, float> termWeights, QueryFeatures features, QueryExpansion expansion)
    {
        if (entry.Tokens.Count == 0) return 0f;

        float score = 0f;
        foreach (var token in entry.Tokens)
        {
            if (termWeights.TryGetValue(token, out var weight))
            {
                score += weight;
            }
        }

        if (score <= 0f) return 0f;

        var overlap = CountOverlap(features.PrimaryTokens, entry.Tokens);
        var jaccard = overlap > 0 ? overlap / (float)(features.PrimaryTokens.Count + entry.Tokens.Count - overlap) : 0f;
        score += overlap * 0.9f + jaccard * 4f;

        var fuzzy = ComputeFuzzyBonus(features.PrimaryTokens, entry.Tokens);
        score += fuzzy * 1.7f;

        var pathOverlap = CountOverlap(features.PathTokens, entry.PathTokens);
        score += pathOverlap * 2.2f;

        var dependencyOverlap = CountOverlap(features.Dependencies, entry.Dependencies);
        score += dependencyOverlap * 2.5f;

        float substringHits = 0f;
        foreach (var term in features.RawTerms)
        {
            if (term.Length < 3) continue;
            if (entry.Combined.Contains(term, StringComparison.OrdinalIgnoreCase)) substringHits += 0.6f;
        }
        score += substringHits;

        var priority = expansion.Priority;
        if (string.Equals(priority, "backend", StringComparison.OrdinalIgnoreCase))
        {
            score *= entry.Node.IsBackend ? 1.3f : 0.7f;
        }
        else if (string.Equals(priority, "frontend", StringComparison.OrdinalIgnoreCase))
        {
            score *= entry.Node.IsBackend ? 0.7f : 1.3f;
        }

        if (entry.Node.Dependencies.Count > 0)
        {
            foreach (var dep in entry.Node.Dependencies)
            {
                if (features.PathTokens.Contains(dep)) score += 2.0f;
            }
        }

        return score;
    }

    static QueryFeatures BuildQueryFeatures(QueryExpansion expansion)
    {
        var primary = new HashSet<string>(Tokenize(expansion.OriginalQuery), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(expansion.Rewritten))
        {
            foreach (var token in Tokenize(expansion.Rewritten)) primary.Add(token);
        }

        var additional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in expansion.Keywords)
        {
            foreach (var token in Tokenize(keyword)) additional.Add(token);
        }

        var pathTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subsystem in expansion.Subsystems)
        {
            foreach (var token in Tokenize(subsystem)) pathTokens.Add(token);
            foreach (var token in SplitPath(subsystem)) pathTokens.Add(token);
        }

        var dependencyTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in expansion.Keywords)
        {
            var t = keyword.Trim().ToLowerInvariant();
            if (t.Length > 0) dependencyTokens.Add(t);
        }
        foreach (var subsystem in expansion.Subsystems)
        {
            var t = subsystem.Trim().ToLowerInvariant();
            if (t.Length > 0) dependencyTokens.Add(t);
        }

        var rawTerms = new List<string>();
        rawTerms.Add(expansion.OriginalQuery);
        if (!string.IsNullOrWhiteSpace(expansion.Rewritten)) rawTerms.Add(expansion.Rewritten);
        rawTerms.AddRange(expansion.Keywords);
        rawTerms.AddRange(expansion.Subsystems);

        return new QueryFeatures(primary, additional, pathTokens, dependencyTokens, rawTerms);
    }

    static int CountOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var count = 0;
        foreach (var token in a)
        {
            if (b.Contains(token)) count++;
        }
        return count;
    }

    static float ComputeFuzzyBonus(HashSet<string> queryTokens, HashSet<string> nodeTokens)
    {
        if (queryTokens.Count == 0 || nodeTokens.Count == 0) return 0f;
        float total = 0f;
        foreach (var query in queryTokens)
        {
            if (query.Length <= 2) continue;
            float best = 0f;
            foreach (var token in nodeTokens)
            {
                var sim = DiceCoefficient(query, token);
                if (sim > best) best = sim;
                if (best >= 0.95f) break;
            }
            if (best >= 0.45f) total += best;
        }
        return total;
    }

    static float DiceCoefficient(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0f;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return 1f;

        var bigramsA = BuildBigrams(a.ToLowerInvariant());
        var bigramsB = BuildBigrams(b.ToLowerInvariant());
        if (bigramsA.Count == 0 || bigramsB.Count == 0) return 0f;

        int overlap = 0;
        foreach (var bg in bigramsA)
        {
            if (bigramsB.Remove(bg)) overlap++;
        }

        return (2f * overlap) / (bigramsA.Count + bigramsB.Count + overlap);
    }

    static List<string> BuildBigrams(string text)
    {
        var list = new List<string>(Math.Max(text.Length - 1, 0));
        for (int i = 0; i < text.Length - 1; i++)
        {
            list.Add(text.Substring(i, 2));
        }
        return list;
    }

    static NodeIndexEntry BuildIndexEntry(SemanticNodeSummary node)
    {
        var combined = string.Join(' ', new[]
        {
            node.Title,
            node.Summary,
            string.Join(' ', node.Responsibilities),
            string.Join(' ', node.Keywords),
            string.Join(' ', node.Paths),
            string.Join(' ', node.Dependencies)
        });

        var tokens = new HashSet<string>(Tokenize(combined), StringComparer.OrdinalIgnoreCase);
        var pathTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in node.Paths)
        {
            foreach (var token in SplitPath(path))
            {
                pathTokens.Add(token);
                tokens.Add(token);
            }
        }

        var dependencies = new HashSet<string>(node.Dependencies.Select(d => d.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        return new NodeIndexEntry(node, tokens, pathTokens, dependencies, combined);
    }

    static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    static IEnumerable<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        var normalized = path.Replace('\\', '/');
        foreach (var part in normalized.Split(new[] { '/', '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) continue;
            var token = part.ToLowerInvariant();
            yield return token;
            foreach (var camel in SplitCamelCase(part))
            {
                yield return camel.ToLowerInvariant();
            }
        }
    }

    static IEnumerable<string> SplitCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsUpper(ch) && sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    readonly record struct NodeIndexEntry(
        SemanticNodeSummary Node,
        HashSet<string> Tokens,
        HashSet<string> PathTokens,
        HashSet<string> Dependencies,
        string Combined);

    readonly record struct QueryFeatures(
        HashSet<string> PrimaryTokens,
        HashSet<string> AdditionalTokens,
        HashSet<string> PathTokens,
        HashSet<string> Dependencies,
        IReadOnlyList<string> RawTerms);

    public readonly record struct ScoredNode(SemanticNodeSummary Node, float Score);
}

public sealed record QueryExpansion(
    string OriginalQuery,
    string Rewritten,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Subsystems,
    string Priority,
    IReadOnlyList<string> Clarifications)
{
    public static QueryExpansion Empty(string original) => new(
        original,
        original,
        Array.Empty<string>(),
        Array.Empty<string>(),
        "backend",
        Array.Empty<string>());
}
