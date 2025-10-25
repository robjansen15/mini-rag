using System;
using System.Threading.Tasks;

namespace ScriptRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = new AppConfiguration();
        try
        {
            return await CommandDispatcher.DispatchAsync(args, config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
