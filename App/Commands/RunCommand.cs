using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RagLib;

namespace ScriptRunner;

static class RunCommand
{
    public static Task<int> ExecuteAsync(AppConfiguration config, string[] args)
    {
        try
        {
            var query = args.Length > 0 ? string.Join(" ", args) : "explain underwriting workflow";

            var rag = new RagRuntime(new RagOptions
            {
                DataPath = config.DataPath,
                IndexCachePath = Path.ChangeExtension(config.DataPath, ".tfidf.bin"),
                ModelTag = config.ModelTag,
                Host = config.OllamaHost,
                NumPredict = config.NumPredict,
                IndexThreads = config.Threads
            });

            Console.WriteLine("Loading corpus...");
            rag.LoadCorpus();
            Console.WriteLine($"  Loaded {rag.Texts.Count} documents from {config.DataPath}");

            Console.WriteLine("Building index...");
            rag.BuildIndex(onStatus: msg => Console.WriteLine($"  {msg}"));

            Console.WriteLine($"Retrieving relevant documents for: {query}");
            var hits = rag.Retrieve(query, k: config.RetrievalTopK);

            var ctx = string.Join("\n---\n", hits.Select(h => h.Text));

            Console.WriteLine("Generating answer...");
            var answerTask = rag.GenerateToStringAsync($"Context:\n{ctx}\n\nAnswer clearly:");
            return AnswerAsync(answerTask);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RAG runtime failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    static async Task<int> AnswerAsync(Task<string> answerTask)
    {
        var answer = await answerTask.ConfigureAwait(false);
        Console.WriteLine("\n=== Answer ===");
        Console.WriteLine(answer);
        return 0;
    }
}
