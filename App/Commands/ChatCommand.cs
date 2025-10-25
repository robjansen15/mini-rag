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
            var msg = Environment.GetEnvironmentVariable("MSG")
                      ?? (args.Length > 0 ? string.Join(" ", args) : "Hello, confirm you're running llama3.2:1b-instruct-fp16");

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

            Console.WriteLine($"Retrieving relevant documents for: {msg}");
            var hits = rag.Retrieve(msg, k: config.RetrievalTopK);

            if (hits.Count == 0)
            {
                Console.WriteLine("  No relevant documents found in the corpus.");
            }
            else
            {
                Console.WriteLine($"  Found {hits.Count} documents for context.");
            }

            var ctx = string.Join("\n---\n", hits.Select(h => h.text));

            await using var client = new OllamaChatClient(new ChatOptions
            {
                Host = config.OllamaHost,
                ModelTag = config.ModelTag
            });

            var chatMessages = new List<OllamaChatClient.ChatMessage>();
            if (!string.IsNullOrWhiteSpace(ctx))
            {
                chatMessages.Add(new OllamaChatClient.ChatMessage("system",
                    "Use the following retrieved context to answer the user's question.\n" + ctx));
            }
            chatMessages.Add(new OllamaChatClient.ChatMessage("user", msg));

            Console.WriteLine($"You: {msg}");
            var reply = await client.ChatAsync(chatMessages).ConfigureAwait(false);
            Console.WriteLine($"Assistant: {reply}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Chat failed: {ex.Message}");
            return 1;
        }
    }
}
