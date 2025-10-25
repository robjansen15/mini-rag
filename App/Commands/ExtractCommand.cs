using System;
using System.Threading.Tasks;
using ExtractorLib;

namespace ScriptRunner;

static class ExtractCommand
{
    public static Task<int> ExecuteAsync(AppConfiguration config, string[] args)
    {
        try
        {
            var opts = new ExtractOptions
            {
                ProjectRoot = args.Length > 0 ? args[0] : config.ProjectRoot,
                OutPath = config.DataPath,
                Truncate = config.TruncateOutput,
                Threads = config.Threads
            };

            Console.WriteLine($"Extracting from: {opts.ProjectRoot}");
            Console.WriteLine($"Output to: {opts.OutPath}");
            Console.WriteLine($"Using {opts.Threads} threads");

            new CodeExtractor(opts).Extract();

            Console.WriteLine("Extraction completed successfully");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Extraction failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }
}
