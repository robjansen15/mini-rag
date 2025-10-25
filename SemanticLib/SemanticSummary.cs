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

    public SemanticSummaryStore(IEnumerable<SemanticNodeSummary> nodes)
    {
        _nodes = nodes.ToList();
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
        if (_nodes.Count == 0) return Array.Empty<ScoredNode>();
        var termWeights = BuildTermWeights(expansion);
        var scored = new List<ScoredNode>(_nodes.Count);
        foreach (var node in _nodes)
        {
            var score = ScoreNode(node, termWeights, expansion);
            if (score > 0f) scored.Add(new ScoredNode(node, score));
        }

        if (scored.Count == 0)
        {
            // Fallback: pick top backend nodes
            var fallback = _nodes
                .Select(n => new ScoredNode(n, n.IsBackend ? 1f : 0.5f))
                .OrderByDescending(s => s.Score)
                .Take(topN)
                .ToList();
            return fallback;
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Node.Title, StringComparer.OrdinalIgnoreCase)
            .Take(topN)
            .ToList();
    }

    static Dictionary<string, float> BuildTermWeights(QueryExpansion expansion)
    {
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        void AddTerms(IEnumerable<string> terms, float weight)
        {
            foreach (var t in terms)
            {
                var token = t.Trim();
                if (token.Length == 0) continue;
                weights[token] = weights.TryGetValue(token, out var existing) ? Math.Max(existing, weight) : weight;
            }
        }

        AddTerms(Tokenize(expansion.OriginalQuery), 1.0f);
        if (!string.IsNullOrWhiteSpace(expansion.Rewritten)) AddTerms(Tokenize(expansion.Rewritten), 1.2f);
        AddTerms(expansion.Keywords, 1.5f);
        AddTerms(expansion.Subsystems, 1.3f);
        return weights;
    }

    static float ScoreNode(SemanticNodeSummary node, Dictionary<string, float> termWeights, QueryExpansion expansion)
    {
        var haystack = string.Join(' ', new[]
        {
            node.Title,
            node.Summary,
            string.Join(' ', node.Responsibilities),
            string.Join(' ', node.Keywords),
            string.Join(' ', node.Paths),
            string.Join(' ', node.Dependencies)
        });

        if (haystack.Length == 0) return 0f;

        var score = 0f;
        foreach (var token in Tokenize(haystack))
        {
            if (termWeights.TryGetValue(token, out var weight))
            {
                score += weight;
            }
        }

        if (score <= 0f) return 0f;

        var priority = expansion.Priority;
        if (string.Equals(priority, "backend", StringComparison.OrdinalIgnoreCase))
        {
            score *= node.IsBackend ? 1.3f : 0.7f;
        }
        else if (string.Equals(priority, "frontend", StringComparison.OrdinalIgnoreCase))
        {
            score *= node.IsBackend ? 0.7f : 1.3f;
        }

        if (node.Dependencies.Count > 0 && expansion.Subsystems.Count > 0)
        {
            foreach (var dep in node.Dependencies)
            {
                if (expansion.Subsystems.Contains(dep, StringComparer.OrdinalIgnoreCase))
                {
                    score += 2.5f;
                }
            }
        }

        return score;
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
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

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
