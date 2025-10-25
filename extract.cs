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
    public long MaxBytes { get; init; } = 3 * 1024 * 1024; // 3 MB default limit
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
        
        // Load existing corpus to check for file hashes
        var existingEntries = LoadExistingCorpus();
        Console.WriteLine($"Loaded {existingEntries.Count} existing entries from corpus");
        
        // Always write tree manifest
        var tempTreePath = _opts.OutPath + ".tree.tmp";
        using (var init = new FileStream(tempTreePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var w = new StreamWriter(init, new UTF8Encoding(false)))
        {
            WriteTreeManifest(w, root);
            w.Flush();
        }

        var dfsFiles = EnumerateFilesDFS(root).Where(IsAllowedFile).ToList();
        var totalFiles = dfsFiles.Count;
        Console.WriteLine($"Found {totalFiles} files to scan");
        
        // Calculate total size and filter files > MaxBytes
        var fileInfos = new List<(string path, long size)>();
        var skippedTooLarge = 0;
        long totalBytes = 0;
        
        foreach (var file in dfsFiles)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > _opts.MaxBytes)
                {
                    skippedTooLarge++;
                    continue;
                }
                fileInfos.Add((file, fi.Length));
                totalBytes += fi.Length;
            }
            catch
            {
                // Skip files we can't access
            }
        }
        
        if (skippedTooLarge > 0)
        {
            Console.WriteLine($"Skipped {skippedTooLarge} files larger than {_opts.MaxBytes / (1024 * 1024)} MB");
        }
        
        // Determine which files need processing based on hash
        var filesToProcess = new List<(string path, long size)>();
        var skippedUnchanged = 0;
        long bytesToProcess = 0;
        
        foreach (var (file, size) in fileInfos)
        {
            ct.ThrowIfCancellationRequested();
            var fileHash = ComputeFileHash(file);
            var rel = Path.GetRelativePath(root, file);
            var fileId = BuildIdForFile(rel);
            
            if (existingEntries.TryGetValue(fileId, out var existing) && existing.hash == fileHash)
            {
                skippedUnchanged++;
            }
            else
            {
                filesToProcess.Add((file, size));
                bytesToProcess += size;
            }
        }
        
        Console.WriteLine($"Files unchanged: {skippedUnchanged}, Files to process: {filesToProcess.Count}");
        Console.WriteLine($"Total data to process: {FormatBytes(bytesToProcess)}");
        
        if (filesToProcess.Count == 0 && skippedUnchanged == 0)
        {
            Console.WriteLine("No files to process");
            return;
        }
        
        // Process changed files with progress tracking
        var newEntries = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        var processedCount = 0;
        long processedBytes = 0;
        var startTime = DateTimeOffset.UtcNow;
        var i = 0;

        while (i < filesToProcess.Count)
        {
            ct.ThrowIfCancellationRequested();
            var round = filesToProcess.Skip(i).Take(_scanThreads).ToList();

            Parallel.ForEach(
                source: round,
                new ParallelOptions { MaxDegreeOfParallelism = _scanThreads, CancellationToken = ct },
                item =>
                {
                    var (path, size) = item;
                    try
                    {
                        var entryLines = ProcessFile(root, path).ToList();
                        if (entryLines.Count > 0)
                        {
                            newEntries[path] = entryLines;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error processing {path}: {ex.Message}");
                    }
                });

            // Update progress
            foreach (var (path, size) in round)
            {
                processedBytes += size;
            }
            processedCount += round.Count;
            
            var percentage = (processedCount * 100.0) / filesToProcess.Count;
            var elapsed = DateTimeOffset.UtcNow - startTime;
            var bytesPerSecond = processedBytes / elapsed.TotalSeconds;
            var remainingBytes = bytesToProcess - processedBytes;
            var estimatedSecondsRemaining = remainingBytes / Math.Max(bytesPerSecond, 1);
            var eta = TimeSpan.FromSeconds(estimatedSecondsRemaining);
            
            Console.Write($"\rProgress: {processedCount}/{filesToProcess.Count} files ({percentage:F1}%) | " +
                         $"{FormatBytes(processedBytes)}/{FormatBytes(bytesToProcess)} | " +
                         $"ETA: {FormatTimeSpan(eta)}      ");

            i += round.Count;
        }
        
        Console.WriteLine();
        
        // Merge: write tree, then existing (unchanged) entries, then new entries
        var finalPath = _opts.OutPath;
        var backupPath = _opts.OutPath + ".backup";
        
        if (File.Exists(finalPath))
        {
            File.Copy(finalPath, backupPath, overwrite: true);
        }
        
        try
        {
            using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                // Write tree manifest
                var treeContent = File.ReadAllText(tempTreePath);
                w.Write(treeContent);
                
                // Get all file IDs that were processed
                var processedFileIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (file, _) in filesToProcess)
                {
                    var rel = Path.GetRelativePath(root, file);
                    var fileId = BuildIdForFile(rel);
                    processedFileIds.Add(fileId);
                }
                
                // Write unchanged entries from existing corpus
                foreach (var kvp in existingEntries)
                {
                    var entryId = kvp.Key;
                    var entry = kvp.Value;
                    
                    var isFileEntry = entryId.EndsWith(".file");
                    var isClassEntry = entryId.EndsWith(".class");
                    var isFunctionEntry = !isFileEntry && !isClassEntry && entryId != "__TREE__";
                    
                    if (isFileEntry && processedFileIds.Contains(entryId))
                    {
                        continue;
                    }
                    
                    if (isClassEntry || isFunctionEntry)
                    {
                        var belongsToReprocessedFile = false;
                        foreach (var fileId in processedFileIds)
                        {
                            var filePrefix = fileId.Substring(0, fileId.Length - 5);
                            if (entryId.StartsWith(filePrefix + "."))
                            {
                                belongsToReprocessedFile = true;
                                break;
                            }
                        }
                        
                        if (belongsToReprocessedFile)
                        {
                            continue;
                        }
                    }
                    
                    w.WriteLine(entry.json);
                }
                
                // Write new/updated entries
                foreach (var kvp in newEntries.OrderBy(k => k.Key))
                {
                    foreach (var line in kvp.Value)
                    {
                        w.WriteLine(line);
                    }
                }
            }
            
            // Clean up
            File.Delete(tempTreePath);
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            
            var totalTime = DateTimeOffset.UtcNow - startTime;
            Console.WriteLine($"Extraction complete! Processed {filesToProcess.Count} files, kept {skippedUnchanged} unchanged in {FormatTimeSpan(totalTime)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error writing corpus: {ex.Message}");
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, finalPath, overwrite: true);
                Console.WriteLine("Restored from backup");
            }
            throw;
        }
    }
    
    static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
    
    static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)
            return $"{ts.TotalSeconds:F0}s";
        if (ts.TotalMinutes < 60)
            return $"{ts.TotalMinutes:F1}m";
        return $"{ts.TotalHours:F1}h";
    }
    
    Dictionary<string, (string hash, string json)> LoadExistingCorpus()
    {
        var entries = new Dictionary<string, (string hash, string json)>(StringComparer.Ordinal);
        
        if (!File.Exists(_opts.OutPath))
        {
            return entries;
        }
        
        try
        {
            using var fs = File.OpenRead(_opts.OutPath);
            using var sr = new StreamReader(fs, new UTF8Encoding(false));
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                
                try
                {
                    using var jd = JsonDocument.Parse(line);
                    var root = jd.RootElement;
                    
                    if (!root.TryGetProperty("id", out var idProp)) continue;
                    if (!root.TryGetProperty("hash", out var hashProp)) continue;
                    
                    var id = idProp.GetString();
                    var hash = hashProp.GetString();
                    
                    if (id != null && hash != null)
                    {
                        entries[id] = (hash, line);
                    }
                }
                catch
                {
                    // Skip malformed entries
                }
            }
        }
        catch
        {
            // If we can't read the corpus, just return empty
        }
        
        return entries;
    }
    
    static string ComputeFileHash(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            // If we can't read the file, return empty hash so it gets processed
            return "";
        }
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
            title = ExtractionTools.HeuristicFileTitle(rel, lang, text.Value.Content),
            path = rel.Replace('\\','/'),
            abs_path = abs,
            root = root,
            lang = lang,
            size = text.Value.Size,
            hash = ExtractionTools.Sha256(text.Value.Content),
            text = contentWithCtx
        };
        yield return JsonSerializer.Serialize(fileObj);

        // Extract classes (for object-oriented languages)
        foreach (var cls in ExtractionTools.ExtractClasses(rel, lang, text.Value.Content))
        {
            var clsObj = new JsonObj
            {
                id = BuildIdForClass(rel, cls.name),
                type = "class",
                title = ExtractionTools.HeuristicClassTitle(cls.name, rel, lang, cls.body),
                class_name = cls.name,
                path = rel.Replace('\\','/'),
                abs_path = abs,
                root = root,
                lang = lang,
                size = cls.body.Length,
                hash = ExtractionTools.Sha256(cls.body),
                text = BuildTextWithContext(root, rel, abs, lang, cls.body)
            };
            yield return JsonSerializer.Serialize(clsObj);
        }

        // Extract functions/methods
        foreach (var fn in ExtractionTools.ExtractFunctions(rel, lang, text.Value.Content))
        {
            var fnObj = new JsonObj
            {
                id = BuildIdForFunction(rel, fn.name),
                type = "function",
                title = ExtractionTools.HeuristicFunctionTitle(fn.name, rel, lang, fn.body),
                function = fn.name,
                path = rel.Replace('\\','/'),
                abs_path = abs,
                root = root,
                lang = lang,
                size = fn.body.Length,
                hash = ExtractionTools.Sha256(fn.body),
                text = BuildTextWithContext(root, rel, abs, lang, fn.body)
            };
            yield return JsonSerializer.Serialize(fnObj);
        }
    }

    sealed class JsonObj
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string? title { get; set; }
        public string? class_name { get; set; }
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