using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OllamaChatLib;
using RagLib;
using SemanticLib;

namespace ScriptRunner;

static class ChatCommand
{
    public static async Task<int> ExecuteAsync(AppConfiguration config, string[] args)
    {
        try
        {
            var envMsg = Environment.GetEnvironmentVariable("MSG");
            var interactive = string.IsNullOrWhiteSpace(envMsg) && args.Length == 0;
            var initialMessage = interactive
                ? null
                : envMsg ?? string.Join(" ", args);

            await using var rag = new RagRuntime(new RagOptions
            {
                DataPath = config.DataPath,
                ModelTag = config.ModelTag,
                Host = config.OllamaHost,
                NumPredict = config.NumPredict,
                IndexThreads = config.Threads
            });

            Console.WriteLine("Loading corpus...");
            rag.LoadCorpus();
            Console.WriteLine($"  Loaded {rag.Texts.Count} documents from {config.DataPath}");

            Console.WriteLine("Building index...");
            rag.BuildIndex(onStatus: status => Console.WriteLine($"  {status}"));

            await using var client = new OllamaChatClient(new ChatOptions
            {
                Host = config.OllamaHost,
                ModelTag = config.ModelTag
            });

            var summaryBuilder = new SemanticSummaryBuilder(config.DataPath, config.SemanticSummaryPath);
            var summaryStore = await summaryBuilder.LoadOrBuildAsync(client).ConfigureAwait(false);

            if (interactive)
            {
                Console.WriteLine("Interactive chat ready. Type /exit to leave.");
                var history = new List<OllamaChatClient.ChatMessage>();
                while (true)
                {
                    Console.Write("You: ");
                    var input = Console.ReadLine();
                    if (input == null) break;
                    input = input.Trim();
                    if (input.Length == 0) continue;
                    if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase)) break;

                    var reply = await SendMessageAsync(input, rag, client, history, config, summaryStore).ConfigureAwait(false);
                    Console.WriteLine($"Assistant: {reply}");
                }
                return 0;
            }
            else
            {
                var history = new List<OllamaChatClient.ChatMessage>();
                var message = initialMessage ?? string.Empty;
                Console.WriteLine($"You: {message}");
                var reply = await SendMessageAsync(message, rag, client, history, config, summaryStore).ConfigureAwait(false);
                Console.WriteLine($"Assistant: {reply}");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Chat failed: {ex.Message}");
            return 1;
        }
    }

    static async Task<string> SendMessageAsync(
        string message,
        RagRuntime rag,
        OllamaChatClient client,
        List<OllamaChatClient.ChatMessage> history,
        AppConfiguration config,
        SemanticSummaryStore summaryStore)
    {
        var expansion = await QueryExpander.ExpandAsync(message, client).ConfigureAwait(false);
        var semanticNodes = summaryStore.Search(expansion, config.RetrievalTopK).ToList();

        if (!string.IsNullOrWhiteSpace(expansion.Rewritten) &&
            !string.Equals(expansion.Rewritten, message, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Expanded query: {expansion.Rewritten}");
        }
        if (expansion.Keywords.Count > 0)
        {
            Console.WriteLine($"  Keywords: {string.Join(", ", expansion.Keywords)}");
        }
        if (semanticNodes.Count > 0)
        {
            Console.WriteLine("  Semantic modules:");
            foreach (var node in semanticNodes)
            {
                Console.WriteLine($"    - {node.Node.Id} [{node.Node.Classification}] score={node.Score:F2}");
            }
        }

        var retrievalQuery = string.IsNullOrWhiteSpace(expansion.Rewritten)
            ? message
            : expansion.Rewritten;

        Console.WriteLine($"Retrieving relevant documents for: {retrievalQuery}");
        var rawHits = rag.Retrieve(retrievalQuery, k: Math.Max(config.RetrievalTopK * 2, config.RetrievalTopK + semanticNodes.Count));
        var prioritized = ReRankHits(rawHits, semanticNodes, config.RetrievalTopK, expansion);

        const float minScore = 0.05f;
        var bestScore = prioritized.Count > 0 ? prioritized.Max(h => h.Score) : 0f;
        var dynamicThreshold = Math.Max(minScore, bestScore * 0.45f);
        var filtered = prioritized
            .Where(h => h.Score >= dynamicThreshold && h.Score > 0f)
            .ToList();

        if (filtered.Count == 0 && prioritized.Count > 0)
        {
            filtered.Add(prioritized[0]);
        }

        if (filtered.Count < config.RetrievalTopK)
        {
            foreach (var extra in prioritized)
            {
                if (filtered.Count >= config.RetrievalTopK) break;
                if (!filtered.Contains(extra)) filtered.Add(extra);
            }
        }

        if (filtered.Count == 0)
        {
            Console.WriteLine("  No relevant documents found in the corpus.");
        }
        else
        {
            Console.WriteLine($"  Using {filtered.Count} document(s) for context (threshold {dynamicThreshold:F3}).");
            for (int i = 0; i < filtered.Count; i++)
            {
                var hit = filtered[i];
                var preview = hit.Text.Length > 160 ? hit.Text[..160] + "..." : hit.Text;
                Console.WriteLine($"    [{i + 1}] score={hit.Score:F3} path={hit.Path} preview={preview.Replace("\n", " ")}");
            }
        }

        var semanticContext = BuildSemanticContext(semanticNodes);
        var excerpts = string.Join("\n---\n", filtered.Select(h => TruncateForContext(h.Text)));

        var systemSections = new List<string>();
        if (!string.IsNullOrWhiteSpace(semanticContext))
        {
            systemSections.Add("Module insights:\n" + semanticContext);
        }
        if (!string.IsNullOrWhiteSpace(excerpts))
        {
            systemSections.Add("Raw excerpts:\n" + excerpts);
        }
        if (expansion.Clarifications.Count > 0)
        {
            systemSections.Add("Clarifications:\n- " + string.Join("\n- ", expansion.Clarifications));
        }

        var messages = new List<OllamaChatClient.ChatMessage>();
        if (systemSections.Count > 0)
        {
            messages.Add(new OllamaChatClient.ChatMessage("system",
                "Use the retrieved summaries and excerpts to answer. Cite uncertainties if context is lacking.\n\n" +
                string.Join("\n\n", systemSections)));
        }

        messages.AddRange(history);
        messages.Add(new OllamaChatClient.ChatMessage("user", message));

        var reply = await client.ChatAsync(messages).ConfigureAwait(false);

        history.Add(new OllamaChatClient.ChatMessage("user", message));
        history.Add(new OllamaChatClient.ChatMessage("assistant", reply));

        return reply;
    }

    static List<RagRuntime.DocHit> ReRankHits(
        IReadOnlyList<RagRuntime.DocHit> hits,
        IReadOnlyList<SemanticSummaryStore.ScoredNode> nodes,
        int desired,
        QueryExpansion expansion)
    {
        if (hits.Count == 0) return new List<RagRuntime.DocHit>();

        var scored = new List<(RagRuntime.DocHit Hit, float Score)>(hits.Count);
        foreach (var hit in hits)
        {
            var score = hit.Score;
            foreach (var node in nodes)
            {
                if (PathMatches(hit.Path, node.Node))
                {
                    score *= 1.0f + MathF.Min(node.Score / 10f, 1.2f);
                    if (node.Node.IsBackend) score *= 1.1f;
                }
            }

            if (string.Equals(expansion.Priority, "backend", StringComparison.OrdinalIgnoreCase) &&
                IsLikelyFrontend(hit.Path))
            {
                score *= 0.7f;
            }

            scored.Add((hit, score));
        }

        var ordered = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Hit.Score)
            .Take(desired)
            .Select(s => s.Hit)
            .ToList();

        return ordered.Count > 0 ? ordered : hits.Take(desired).ToList();
    }

    static string BuildSemanticContext(IReadOnlyList<SemanticSummaryStore.ScoredNode> nodes)
    {
        if (nodes.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i].Node;
            sb.Append('[').Append(node.Id).Append("] ");
            sb.AppendLine(node.Summary);
            if (node.Responsibilities.Count > 0)
            {
                sb.AppendLine("Key responsibilities: " + string.Join(", ", node.Responsibilities));
            }
            if (node.Keywords.Count > 0)
            {
                sb.AppendLine("Keywords: " + string.Join(", ", node.Keywords));
            }
            if (node.Dependencies.Count > 0)
            {
                sb.AppendLine("Dependencies: " + string.Join(", ", node.Dependencies));
            }
            if (i < nodes.Count - 1)
            {
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    static bool PathMatches(string? docPath, SemanticNodeSummary node)
    {
        if (string.IsNullOrWhiteSpace(docPath)) return false;
        var normalizedDoc = Normalize(docPath);
        if (normalizedDoc.StartsWith(Normalize(node.Id), StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var candidate in node.Paths)
        {
            var norm = Normalize(candidate);
            if (normalizedDoc.Equals(norm, StringComparison.OrdinalIgnoreCase)) return true;
            var dir = Normalize(System.IO.Path.GetDirectoryName(candidate) ?? string.Empty);
            if (!string.IsNullOrEmpty(dir) && normalizedDoc.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static string Normalize(string path)
        => path.Replace('\\', '/');

    static bool IsLikelyFrontend(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var lower = path.ToLowerInvariant();
        foreach (var marker in FrontendIndicators)
        {
            if (lower.Contains(marker)) return true;
        }
        return false;
    }

    static string TruncateForContext(string text)
    {
        const int maxChars = 1200;
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    static readonly string[] FrontendIndicators =
    {
        "ui","frontend","front-end","component","css","style","view","react","angular","vue","svelte","tailwind","storybook","design"
    };
}
