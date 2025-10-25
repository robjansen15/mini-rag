using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ExtractorLib;
using RagLib;
using SetupLib;
using OllamaChatLib;

namespace ScriptRunner
{
    /// <summary>
    /// Common configuration used across all commands
    /// </summary>
    public class Config
    {
        // Ollama Configuration
        public string OllamaHost { get; set; } = "http://127.0.0.1:11434";
        public string ModelTag { get; set; } = "llama3.2:1b-instruct-fp16";
        
        // Path Configuration - Use project root instead of bin directory
        private static string GetProjectRoot()
        {
            // Start from the assembly location and walk up to find the project root
            var assemblyPath = AppContext.BaseDirectory;
            var dir = new DirectoryInfo(assemblyPath);
            
            // Walk up from bin/Debug/net9.0 to project root
            while (dir != null && dir.Parent != null)
            {
                // Look for .csproj file or other project markers
                if (dir.GetFiles("*.csproj").Length > 0 || 
                    dir.GetFiles("Program.cs").Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            
            // Fallback to current directory
            return Directory.GetCurrentDirectory();
        }
        
        public string BaseDir { get; set; } = GetProjectRoot();
        public string DataPath { get; set; } = Path.Combine(GetProjectRoot(), "data", "corpus.jsonl");
        public string ProjectRoot { get; set; } = ".";
        
        // Setup Configuration
        public string CurrentLinkName { get; set; } = "llama1b";
        
        // Performance Configuration
        public int Threads { get; set; } = 16;
        
        // RAG Configuration
        public int NumPredict { get; set; } = 300;
        public int RetrievalTopK { get; set; } = 3;
        
        // Extract Configuration
        public bool TruncateOutput { get; set; } = true;
    }

    class Program
    {
        private static Config config;

        static async Task<int> Main(string[] args)
        {
            // Initialize configuration
            config = new Config();

            if (args.Length == 0)
            {
                ShowUsage();
                return 1;
            }

            string command = args[0].ToLower();
            string[] commandArgs = args.Skip(1).ToArray();

            try
            {
                switch (command)
                {
                    case "setup":
                        return await CmdSetup();
                    case "run":
                        return await CmdRun(commandArgs);
                    case "chat":
                        return await CmdChat(commandArgs);
                    case "extract":
                        return CmdExtract(commandArgs);
                    case "clean":
                        return CmdClean();
                    case "reset":
                        return await CmdReset();
                    default:
                        ShowUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static void ShowUsage()
        {
            Console.WriteLine("usage: program {setup|run|chat|extract|clean|reset} [args]");
        }

        private static async Task<int> CmdSetup()
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

        private static async Task<int> CmdRun(string[] args)
        {
            try
            {
                string query = args.Length > 0 ? string.Join(" ", args) : "explain underwriting workflow";
                
                var rag = new RagRuntime(new RagOptions
                {
                    DataPath = config.DataPath,
                    ModelTag = config.ModelTag,
                    Host = config.OllamaHost,
                    NumPredict = config.NumPredict,
                    IndexThreads = config.Threads
                });

                Console.WriteLine("Loading corpus...");
                rag.LoadCorpus();
                
                Console.WriteLine("Building index...");
                rag.BuildIndex();
                
                Console.WriteLine($"Retrieving relevant documents for: {query}");
                var hits = rag.Retrieve(query, k: config.RetrievalTopK);
                
                var ctx = string.Join("\n---\n", hits.Select(h => h.text));
                
                Console.WriteLine("Generating answer...");
                var answer = await rag.GenerateToStringAsync($"Context:\n{ctx}\n\nAnswer clearly:");
                
                Console.WriteLine("\n=== Answer ===");
                Console.WriteLine(answer);
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RAG runtime failed: {ex.Message}");
                return 1;
            }
        }

        private static async Task<int> CmdChat(string[] args)
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
                var reply = await client.ChatAsync(msg);
                Console.WriteLine($"Assistant: {reply}");
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Chat failed: {ex.Message}");
                return 1;
            }
        }

        private static int CmdExtract(string[] args)
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
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Extraction failed: {ex.Message}");
                return 1;
            }
        }

        private static int CmdClean()
        {
            Console.WriteLine("Clean command is deprecated (legacy Python support removed)");
            Console.WriteLine("To clean build artifacts, use: dotnet clean");
            return 0;
        }

        private static async Task<int> CmdReset()
        {
            Console.WriteLine("Running setup...");
            return await CmdSetup();
        }
    }
}