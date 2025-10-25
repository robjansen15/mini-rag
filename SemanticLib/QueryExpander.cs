using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OllamaChatLib;

namespace SemanticLib;

public static class QueryExpander
{
    public static async Task<QueryExpansion> ExpandAsync(string query, OllamaChatClient client, CancellationToken ct = default)
    {
        try
        {
            var messages = new List<OllamaChatClient.ChatMessage>
            {
                new("system",
                    "You rewrite user questions into focused retrieval briefs for a RAG system. " +
                    "Respond with strict JSON. Do not include commentary or code fences."),
                new("user",
                    BuildPrompt(query))
            };

            var raw = await client.ChatAsync(messages, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return QueryExpansion.Empty(query);
            if (!TryParse(raw, query, out var expansion))
            {
                // Try to salvage by extracting the first JSON object
                var jsonSpan = ExtractFirstJson(raw);
                if (jsonSpan != null && TryParse(jsonSpan, query, out expansion))
                {
                    return expansion;
                }
                return QueryExpansion.Empty(query);
            }
            return expansion;
        }
        catch
        {
            return QueryExpansion.Empty(query);
        }
    }

    static string BuildPrompt(string query)
    {
        return "Question: " + query + "\n\n" +
               "Return JSON with the following shape:\n" +
               "{\n" +
               "  \"rewritten\": string,           // concise retrieval query (<= 120 chars)\n" +
               "  \"keywords\": string[],          // key technical terms or entities\n" +
               "  \"subsystems\": string[],        // relevant subsystems, modules, or directories\n" +
               "  \"priority\": \"backend\" | \"frontend\" | \"mixed\",\n" +
               "  \"clarifications\": string[]    // important details to remember during generation\n" +
               "}\n\n" +
               "Guidance:\n" +
               "- Prefer backend/data/infra components when uncertain.\n" +
               "- Keep arrays short (<= 5 items) and omit duplicates.\n" +
               "- Use lowercase kebab-case identifiers for subsystems when possible.";
    }

    static bool TryParse(string json, string original, out QueryExpansion expansion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var rewritten = root.TryGetProperty("rewritten", out var rewEl) && rewEl.ValueKind == JsonValueKind.String
                ? rewEl.GetString() ?? original
                : original;

            var keywords = root.TryGetProperty("keywords", out var kwEl)
                ? ReadStringArray(kwEl)
                : Array.Empty<string>();

            var subsystems = root.TryGetProperty("subsystems", out var subsEl)
                ? ReadStringArray(subsEl)
                : Array.Empty<string>();

            var clarifications = root.TryGetProperty("clarifications", out var clarEl)
                ? ReadStringArray(clarEl)
                : Array.Empty<string>();

            var priority = root.TryGetProperty("priority", out var priEl) && priEl.ValueKind == JsonValueKind.String
                ? priEl.GetString() ?? "backend"
                : "backend";

            expansion = new QueryExpansion(
                original,
                rewritten,
                keywords,
                subsystems,
                priority,
                clarifications);
            return true;
        }
        catch
        {
            expansion = QueryExpansion.Empty(original);
            return false;
        }
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
        return list;
    }

    static string? ExtractFirstJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }
        return null;
    }
}
