using System;

namespace ScriptRunner;

static class UsagePrinter
{
    public static void ShowUsage()
    {
        Console.WriteLine("usage: program {setup|run|chat|extract|clean|reset} [args]");
    }
}
