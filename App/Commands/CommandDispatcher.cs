using System;
using System.Linq;
using System.Threading.Tasks;

namespace ScriptRunner;

static class CommandDispatcher
{
    public static Task<int> DispatchAsync(string[] args, AppConfiguration config)
    {
        if (args.Length == 0)
        {
            UsagePrinter.ShowUsage();
            return Task.FromResult(1);
        }

        var command = args[0].ToLowerInvariant();
        var commandArgs = args.Skip(1).ToArray();

        return command switch
        {
            "setup" => SetupCommand.ExecuteAsync(config),
            "run" => RunCommand.ExecuteAsync(config, commandArgs),
            "chat" => ChatCommand.ExecuteAsync(config, commandArgs),
            "extract" => ExtractCommand.ExecuteAsync(config, commandArgs),
            "clean" => CleanCommand.ExecuteAsync(),
            "reset" => ResetCommand.ExecuteAsync(config),
            _ => UnknownCommand(command)
        };
    }

    static Task<int> UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        UsagePrinter.ShowUsage();
        return Task.FromResult(1);
    }
}
