using System;
using System.Threading.Tasks;

namespace ScriptRunner;

static class CleanCommand
{
    public static Task<int> ExecuteAsync()
    {
        Console.WriteLine("Clean command is deprecated (legacy Python support removed)");
        Console.WriteLine("To clean build artifacts, use: dotnet clean");
        return Task.FromResult(0);
    }
}
