using System;
using System.Threading.Tasks;
using OllamaChatLib;

namespace ScriptRunner;

static class ChatCommand
{
    public static async Task<int> ExecuteAsync(AppConfiguration config, string[] args)
    {
        try
        {
            var msg = Environment.GetEnvironmentVariable("MSG")
                      ?? (args.Length > 0 ? string.Join(" ", args) : "Hello, confirm you're running llama3.2:1b-instruct-fp16");

            await using var client = new OllamaChatClient(new ChatOptions
            {
                Host = config.OllamaHost,
                ModelTag = config.ModelTag
            });

            Console.WriteLine($"You: {msg}");
            var reply = await client.ChatAsync(msg).ConfigureAwait(false);
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
