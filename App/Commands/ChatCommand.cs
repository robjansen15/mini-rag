using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OllamaChatLib;
using RagLib;

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

                    var reply = await SendMessageAsync(input, rag, client, history, config).ConfigureAwait(false);
                    Console.WriteLine($"Assistant: {reply}");
                }
                return 0;
            }
            else
            {
                var history = new List<OllamaChatClient.ChatMessage>();
                var message = initialMessage ?? string.Empty;
                Console.WriteLine($"You: {message}");
                var reply = await SendMessageAsync(message, rag, client, history, config).ConfigureAwait(false);
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
        AppConfiguration config)
    {
        Console.WriteLine($"Retrieving relevant documents for: {message}");
        var hits = rag.Retrieve(message, k: config.RetrievalTopK);

        const float minScore = 0.08f;
        var bestScore = hits.Count > 0 ? hits[0].score : 0f;
        var dynamicThreshold = Math.Max(minScore, bestScore * 0.5f);
        var filtered = hits
            .Where(h => h.score >= dynamicThreshold && h.score > 0f)
            .ToList();

        if (filtered.Count == 0 && hits.Count > 0)
        {
            filtered.Add(hits[0]);
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
                var preview = hit.text.Length > 160 ? hit.text[..160] + "..." : hit.text;
                Console.WriteLine($"    [{i + 1}] score={hit.score:F3} preview={preview.Replace("\n", " ")}");
            }
        }

        var ctx = string.Join("\n---\n", filtered.Select(h => TruncateForContext(h.text)));

        var messages = new List<OllamaChatClient.ChatMessage>();
        if (!string.IsNullOrWhiteSpace(ctx))
        {
            messages.Add(new OllamaChatClient.ChatMessage("system",
                "Leverage the retrieved context to answer. Prioritize accuracy and mention when context is insufficient.\n" + ctx));
        }

        messages.AddRange(history);
        messages.Add(new OllamaChatClient.ChatMessage("user", message));

        var reply = await client.ChatAsync(messages).ConfigureAwait(false);

        history.Add(new OllamaChatClient.ChatMessage("user", message));
        history.Add(new OllamaChatClient.ChatMessage("assistant", reply));

        return reply;
    }

    static string TruncateForContext(string text)
    {
        const int maxChars = 1200;
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }
}
