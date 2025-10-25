using System;
using System.Text.Json;
using System.Threading.Tasks;
using SetupLib;

namespace ScriptRunner;

static class SetupCommand
{
    public static async Task<int> ExecuteAsync(AppConfiguration config)
    {
        try
        {
            var runner = new SetupRunner(new SetupOptions
            {
                BaseDir = config.BaseDir,
                ModelTag = config.ModelTag,
                OllamaHost = config.OllamaHost,
                CurrentLinkName = config.CurrentLinkName
            });

            Console.WriteLine("Running setup...");
            var summary = await runner.RunAsync();

            Console.WriteLine("\n=== Setup Summary ===");
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Setup failed: {ex.Message}");
            return 1;
        }
    }
}
