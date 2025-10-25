// extract.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExtractorLib;

public sealed class ExtractOptions
{
    public required string ProjectRoot { get; init; }
    public string OutPath { get; init; } = Path.Combine("data", "corpus.jsonl");
    public long MaxBytes { get; init; } = 2 * 1024 * 1024;
    public bool Truncate { get; init; } = false;
    public int Threads { get; init; } = Environment.ProcessorCount;
}

public sealed class CodeExtractor
{
    readonly ExtractOptions _opts;
    readonly int _scanThreads;

    public CodeExtractor(ExtractOptions opts)
    {
        _opts = opts;
        _scanThreads = Math.Max(1, opts.Threads);
    }

    static readonly HashSet<string> AllowedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",".ipynb",".js",".mjs",".cjs",".ts",".tsx",".jsx",".vue",".svelte",".java",".kt",".kts",".scala",".go",".rs",
        ".c",".h",".cpp",".cc",".cxx",".hpp",".hh",".m",".mm",".cs",".fs",".fsx",".php",".rb",".swift",".lua",".pl",".pm",".r",
        ".dart",".groovy",".gradle",".sql",".proto",".graphql",".gql",
        ".json",".json5",".toml",".ini",".cfg",".conf",".yaml",".yml",".env",".properties",".xml",
        ".html",".htm",".css",".scss",".sass",".less",
        ".md",".markdown",".rst",".adoc",".txt",".csv",".tsv",".log",".org",
    };

    static readonly Dictionary<string,string> SpecialBasenames = new(StringComparer.Ordinal)
    {
        ["Dockerfile"]="dockerfile",["Makefile"]="make",["CMakeLists.txt"]="cmake",
        ["BUILD"]="bazel",["WORKSPACE"]="bazel",["Podfile"]="cocoapods",["Gemfile"]="ruby-gems",
        ["requirements.txt"]="python-reqs",["environment.yml"]="conda-env",["Pipfile"]="pipenv",["Pipfile.lock"]="pipenv-lock",
        ["package.json"]="npm",["pnpm-lock.yaml"]="pnpm-lock",["yarn.lock"]="yarn-lock",["poetry.lock"]="poetry-lock",["pyproject.toml"]="pyproject",
        ["Cargo.toml"]="cargo",["Cargo.lock"]="cargo-lock",["go.mod"]="gomod",["go.sum"]="gosum",
        ["composer.json"]="composer",["composer.lock"]="composer-lock",["pom.xml"]="maven",
        ["build.gradle.kts"]="gradle-kts",["build.gradle"]="gradle",
        [".gitignore"]="git",[ ".gitattributes"]="git",[ ".editorconfig"]="editor",
    };

    static readonly HashSet<string> IgnoreDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",".hg",".svn",".bzr","node_modules","dist","build","out","target",
        ".idea",".vscode",".vs","__pycache__",".venv","venv",".mypy_cache",".pytest_cache",".gradle",".next",".nuxt",".parcel-cache"
    };

    public void Extract(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_opts.ProjectRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_opts.OutPath))!);
        using (var init = new FileStream(_opts.OutPath, _opts.Truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var w = new StreamWriter(init, new UTF8Encoding(false)))
        {
            WriteTreeManifest(w, root);
            w.Flush();
        }

        var dfsFiles = EnumerateFilesDFS(root).Where(IsAllowedFile).ToList();
        var totalFiles = dfsFiles.Count;
        Console.WriteLine($"Found {totalFiles} files to process");
        
        var index = new ConcurrentDictionary<string,int>(StringComparer.Ordinal);
        var processedCount = 0;
        var i = 0;

        while (i < dfsFiles.Count)
        {
            ct.ThrowIfCancellationRequested();
            var round = dfsFiles.Skip(i).Take(_scanThreads).ToList();
            var results = new (int order, string line) [round.Count];

            Parallel.ForEach(
                source: round.Select((path, order) => (path, order)),
                new ParallelOptions { MaxDegreeOfParallelism = _scanThreads, CancellationToken = ct },
                tuple =>
                {
                    var (path, order) = tuple;
                    var owner = Thread.CurrentThread.ManagedThreadId;
                    if (!index.TryAdd(path, owner)) return;
                    try
                    {
                        var entryLines = ProcessFile(root, path);
                        var sb = new StringBuilder();
                        foreach (var line in entryLines) sb.AppendLine(line);
                        results[order] = (order, sb.ToString());
                    }
                    finally
                    {
                        index.TryRemove(path, out _);
                    }
                });

            using (var fs = new FileStream(_opts.OutPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                foreach (var r in results.OrderBy(r => r.order))
                {
                    if (!string.IsNullOrEmpty(r.line)) w.Write(r.line);
                }
            }

            processedCount += round.Count;
            var percentage = (processedCount * 100.0) / totalFiles;
            Console.Write($"\rProgress: {processedCount}/{totalFiles} files ({percentage:F1}%)");

            Array.Clear(results, 0, results.Length);
            i += round.Count;
        }
        
        Console.WriteLine(); // New line after progress is complete
        Console.WriteLine("Extraction complete!");
    }

    static IEnumerable<string> EnumerateFilesDFS(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(cur); } catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(cur); } catch { dirs = Array.Empty<string>(); }
            foreach (var d in dirs.OrderByDescending(s => s, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(d);
                if (IgnoreDirs.Contains(name)) continue;
                stack.Push(d);
            }
        }
    }

    static void WriteTreeManifest(StreamWriter w, string root)
    {
        var lines = new List<string> { Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) };
        foreach (var (depth, dir, file) in TreeWalk(root))
        {
            if (dir != null) lines.Add(new string(' ', Math.Max(0, (depth - 1) * 4)) + "└── " + Path.GetFileName(dir));
            if (file != null) lines.Add(new string(' ', depth * 4) + "├── " + Path.GetFileName(file));
        }
        var manifest = new { id = "__TREE__", type = "tree", path = "__TREE__", root, text = string.Join('\n', lines) };
        w.WriteLine(JsonSerializer.Serialize(manifest));
    }

    static IEnumerable<(int depth, string? dir, string? file)> TreeWalk(string root)
    {
        var stack = new Stack<(string path, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (cur, depth) = stack.Pop();
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(cur); } catch { dirs = Array.Empty<string>(); }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(cur); } catch { files = Array.Empty<string>(); }

            if (depth > 0) yield return (depth, cur, null);
            foreach (var f in files) yield return (depth, null, f);

            foreach (var d in dirs.Where(d => !IgnoreDirs.Contains(Path.GetFileName(d))).OrderByDescending(s => s, StringComparer.Ordinal))
                stack.Push((d, depth + 1));
        }
    }

    IEnumerable<string> ProcessFile(string root, string file)
    {
        if (!IsAllowedFile(file)) yield break;

        var text = ReadTextFile(file, _opts.MaxBytes);
        if (text == null) yield break;

        var lang = DetectLang(file);
        var rel = Path.GetRelativePath(root, file);
        var abs = Path.GetFullPath(file);
        var contentWithCtx = BuildTextWithContext(root, rel, abs, lang, text.Value.Content);

        var fileObj = new JsonObj
        {
            id = BuildIdForFile(rel),
            type = "file",
            title = HeuristicFileTitle(rel, lang, text.Value.Content),
            path = rel.Replace('\\','/'),
            abs_path = abs,
            root = root,
            lang = lang,
            size = text.Value.Size,
            hash = Sha256(text.Value.Content),
            text = contentWithCtx
        };
        yield return JsonSerializer.Serialize(fileObj);

        foreach (var fn in ExtractFunctions(rel, lang, text.Value.Content))
        {
            var fnObj = new JsonObj
            {
                id = BuildIdForFunction(rel, fn.name),
                type = "function",
                title = HeuristicFunctionTitle(fn.name, rel, lang, fn.body),
                function = fn.name,
                path = rel.Replace('\\','/'),
                abs_path = abs,
                root = root,
                lang = lang,
                size = fn.body.Length,
                hash = Sha256(fn.body),
                text = BuildTextWithContext(root, rel, abs, lang, fn.body)
            };
            yield return JsonSerializer.Serialize(fnObj);
        }
    }

    static IEnumerable<(string name, string body)> ExtractFunctions(string rel, string lang, string content)
    {
        // Simple pattern matching for common function definitions
        // This is a heuristic approach - can be enhanced with proper parsing
        
        var patterns = new Dictionary<string, string>
        {
            // C#, Java, C, C++, JavaScript, TypeScript
            ["cs"] = @"(?:public|private|protected|internal|static|\s)+(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["java"] = @"(?:public|private|protected|static|\s)+(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["c"] = @"(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["cpp"] = @"(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{",
            ["js"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["ts"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["jsx"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            ["tsx"] = @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:function|\([^)]*\)\s*=>))",
            
            // Python
            ["py"] = @"def\s+(\w+)\s*\([^)]*\)\s*:",
            
            // Go
            ["go"] = @"func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\s*\([^)]*\)",
            
            // Rust
            ["rs"] = @"fn\s+(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)",
            
            // PHP
            ["php"] = @"function\s+(\w+)\s*\([^)]*\)",
            
            // Ruby
            ["rb"] = @"def\s+(\w+)",
            
            // Swift
            ["swift"] = @"func\s+(\w+)\s*(?:<[^>]*>)?\s*\([^)]*\)",
        };

        if (!patterns.TryGetValue(lang, out var pattern))
            yield break;

        var regex = new Regex(pattern, RegexOptions.Multiline);
        var matches = regex.Matches(content);

        foreach (Match match in matches)
        {
            var functionName = match.Groups[1].Success ? match.Groups[1].Value : 
                              match.Groups[2].Success ? match.Groups[2].Value : null;
            
            if (string.IsNullOrWhiteSpace(functionName))
                continue;

            // Extract function body - simple approach: find matching braces or end of block
            var startIndex = match.Index;
            var endIndex = FindFunctionEnd(content, startIndex, lang);
            
            if (endIndex > startIndex)
            {
                var body = content.Substring(startIndex, endIndex - startIndex);
                if (body.Length > 10 && body.Length < 50000) // Skip very small or very large functions
                {
                    yield return (functionName, body);
                }
            }
        }
    }

    static int FindFunctionEnd(string content, int startIndex, string lang)
    {
        // For brace-based languages, find matching closing brace
        if (new[] { "cs", "java", "c", "cpp", "js", "ts", "jsx", "tsx", "go", "rs", "php", "swift" }.Contains(lang))
        {
            var braceCount = 0;
            var foundOpenBrace = false;
            
            for (int i = startIndex; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    braceCount++;
                    foundOpenBrace = true;
                }
                else if (content[i] == '}')
                {
                    braceCount--;
                    if (foundOpenBrace && braceCount == 0)
                    {
                        return i + 1;
                    }
                }
            }
        }
        // For Python/Ruby, find next function definition or end of indentation
        else if (lang == "py" || lang == "rb")
        {
            var lines = content.Substring(startIndex).Split('\n');
            if (lines.Length == 0) return startIndex + 100;
            
            var firstLine = lines[0];
            var baseIndent = firstLine.Length - firstLine.TrimStart().Length;
            
            var bodyLength = firstLine.Length;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    bodyLength += line.Length + 1;
                    continue;
                }
                
                var indent = line.Length - line.TrimStart().Length;
                if (indent <= baseIndent && line.TrimStart().StartsWith("def "))
                {
                    break;
                }
                
                bodyLength += line.Length + 1;
                
                if (bodyLength > 10000) break; // Limit function size
            }
            
            return Math.Min(startIndex + bodyLength, content.Length);
        }
        
        // Default: take next 500 characters
        return Math.Min(startIndex + 500, content.Length);
    }

    static bool IsAllowedFile(string file)
    {
        var name = Path.GetFileName(file);
        if (SpecialBasenames.ContainsKey(name)) return true;
        return AllowedExts.Contains(Path.GetExtension(file));
    }

    static string DetectLang(string file)
    {
        var name = Path.GetFileName(file);
        if (SpecialBasenames.TryGetValue(name, out var tag)) return tag;
        var ext = Path.GetExtension(file);
        if (string.IsNullOrEmpty(ext)) return "plain";
        return ext.TrimStart('.').ToLowerInvariant();
    }

    static (string Content, int Size)? ReadTextFile(string file, long maxBytes)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            if (fi.Length > maxBytes)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[maxBytes];
                var read = fs.Read(buf, 0, (int)maxBytes);
                return (Encoding.UTF8.GetString(buf, 0, read), (int)maxBytes);
            }
            try { return (File.ReadAllText(file, new UTF8Encoding(false, true)), (int)fi.Length); } catch {}
            try { return (File.ReadAllText(file, new UnicodeEncoding(false, true, true)), (int)fi.Length); } catch {}
            try { return (File.ReadAllText(file, Encoding.Latin1), (int)fi.Length); } catch {}
            var b = File.ReadAllBytes(file);
            return (Encoding.UTF8.GetString(b), b.Length);
        }
        catch { return null; }
    }

    static string BuildTextWithContext(string root, string rel, string abs, string lang, string content)
    {
        var sb = new StringBuilder();
        sb.Append("PATH: ").Append(rel).Append('\n');
        sb.Append("ABS_PATH: ").Append(abs).Append('\n');
        sb.Append("ROOT: ").Append(root).Append('\n');
        sb.Append("LANG: ").Append(lang).Append('\n');
        sb.Append("---\n");
        sb.Append(content);
        return sb.ToString();
    }

    static string Sha256(string s)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static string BuildIdForFile(string rel)
    {
        var parts = rel.Replace('\\','/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) parts.Add(rel);
        var file = parts.Last();
        var fileNoExt = Path.GetFileNameWithoutExtension(file);
        parts[^1] = fileNoExt;
        return string.Join('.', parts) + ".file";
    }

    static string BuildIdForFunction(string rel, string fn)
    {
        var parts = rel.Replace('\\','/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) parts.Add(rel);
        var file = parts.Last();
        var fileNoExt = Path.GetFileNameWithoutExtension(file);
        parts[^1] = fileNoExt;
        return string.Join('.', parts) + "." + SanitizeToken(fn);
    }

    static string SanitizeToken(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var t = new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        t = Regex.Replace(t, "_{2,}", "_");
        return t.Trim('_');
    }

    static string HeuristicFileTitle(string rel, string lang, string content)
    {
        var tokens = new List<string> { Path.GetFileName(rel), lang };
        var hints = ExtractTopNIdentifiers(content, 5);
        if (hints.Count > 0) tokens.Add(string.Join("/", hints));
        return string.Join(" • ", tokens);
    }

    static string HeuristicFunctionTitle(string fn, string rel, string lang, string body)
    {
        var verbs = Regex.Match(body, @"\b(get|set|build|create|update|delete|fetch|render|compute|calculate|process|handle|validate)\b", RegexOptions.IgnoreCase);
        var hint = verbs.Success ? verbs.Value.ToLowerInvariant() : "function";
        return $"{fn} — {hint} ({Path.GetFileName(rel)} {lang})";
    }

    static List<string> ExtractTopNIdentifiers(string content, int n)
    {
        var rx = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]{3,})\b");
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in rx.Matches(content))
        {
            var t = m.Groups[1].Value;
            if (IsNoise(t)) continue;
            freq[t] = freq.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        return freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(n).Select(kv => kv.Key).ToList();
    }

    static bool IsNoise(string t)
    {
        var k = t.ToLowerInvariant();
        return k is "the" or "and" or "else" or "null" or "true" or "false" or "class" or "public" or "private" or "protected" or "static" or "void" or "int" or "string" or "return" or "if" or "for" or "while" or "this" or "var" or "let" or "const" or "function";
    }

    sealed class JsonObj
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string? title { get; set; }
        public string? function { get; set; }
        public string path { get; set; } = "";
        public string abs_path { get; set; } = "";
        public string root { get; set; } = "";
        public string lang { get; set; } = "";
        public int size { get; set; }
        public string hash { get; set; } = "";
        public string text { get; set; } = "";
    }
}