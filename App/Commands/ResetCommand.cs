using System.Threading.Tasks;

namespace ScriptRunner;

static class ResetCommand
{
    public static Task<int> ExecuteAsync(AppConfiguration config)
    {
        Console.WriteLine("Running setup...");
        return SetupCommand.ExecuteAsync(config);
    }
}
