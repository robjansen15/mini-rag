using System;
using System.IO;

namespace ScriptRunner;

/// <summary>
/// Centralized configuration shared across CLI commands.
/// </summary>
public sealed class AppConfiguration
{
    public string OllamaHost { get; init; } = "http://127.0.0.1:11434";
    public string ModelTag { get; init; } = "qwen2.5:7b-instruct";

    public string ProjectRoot { get; init; }
    public string CurrentPath { get; init; }
    public string DataDirectory { get; init; }
    public string BaseDir { get; init; }
    public string DataPath { get; init; }
    public string SemanticSummaryPath { get; init; }

    public string CurrentLinkName { get; init; } = "llama1b";
    public int Threads { get; init; } = 16;
    public int NumPredict { get; init; } = 300;
    public int RetrievalTopK { get; init; } = 5;
    public bool TruncateOutput { get; init; } = true;

    public AppConfiguration()
    {
        var projectRoot = LocateProjectRoot();
        ProjectRoot = projectRoot;
        CurrentPath = Path.Combine(projectRoot, "current");
        DataDirectory = Path.Combine(CurrentPath, "Data");
        BaseDir = projectRoot;
        DataPath = Path.Combine(DataDirectory, "corpus.jsonl");
        SemanticSummaryPath = Path.Combine(DataDirectory, "semantic-summaries.jsonl");
    }

    static string LocateProjectRoot()
    {
        var assemblyPath = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(assemblyPath);

        while (dir != null && dir.Parent != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0 || dir.GetFiles("Program.cs").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
