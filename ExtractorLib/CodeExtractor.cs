// CodeExtractor.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExtractorLib.Internal;

namespace ExtractorLib;

public sealed class ExtractOptions
{
    public required string ProjectRoot { get; init; }
    public string OutPath { get; init; } = Path.Combine("current", "Data", "corpus.jsonl");
    public long MaxBytes { get; init; } = 3 * 1024 * 1024; // 3 MB default limit
    public bool Truncate { get; init; } = false;
    public int Threads { get; init; } = Environment.ProcessorCount;
}

public sealed class CodeExtractor
{
    readonly ExtractOptions _opts;
    readonly int _scanThreads;
    const string PartitionKeyRoot = "<root>";

    public CodeExtractor(ExtractOptions opts)
    {
        _opts = opts;
        _scanThreads = Math.Max(1, opts.Threads);
    }

    public void Extract(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_opts.ProjectRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var corpusPath = Path.GetFullPath(_opts.OutPath);
        Console.WriteLine($"[extract] Project root: {root}");
        Console.WriteLine($"[extract] Corpus file: {corpusPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(corpusPath)!);

        // Load existing corpus to check for file hashes
        Console.WriteLine("[extract] Loading existing corpus...");
        var existingEntries = LoadExistingCorpus();
        Console.WriteLine($"[extract] Loaded {existingEntries.Count} entries from {corpusPath}");

        // Always write tree manifest
        var tempTreePath = _opts.OutPath + ".tree.tmp";
        using (var init = new FileStream(tempTreePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var w = new StreamWriter(init, new UTF8Encoding(false)))
        {
            WriteTreeManifest(w, root);
            w.Flush();
        }

        Console.WriteLine("[extract] Scanning filesystem for candidate files...");
        var dfsFiles = EnumerateFilesDFS(root).Where(FileRules.IsAllowedFile).ToList();
        var totalFiles = dfsFiles.Count;
        Console.WriteLine($"[extract] Found {totalFiles} files to check");
        
        // Calculate total size and filter files > MaxBytes
        var fileInfos = new List<(string path, long size)>();
        var skippedTooLarge = 0;
        long totalBytes = 0;
        
        foreach (var file in dfsFiles)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!_opts.Truncate && fi.Length > _opts.MaxBytes)
                {
                    skippedTooLarge++;
                    continue;
                }
                var effectiveSize = _opts.Truncate ? Math.Min(fi.Length, _opts.MaxBytes) : fi.Length;
                fileInfos.Add((file, effectiveSize));
                totalBytes += effectiveSize;
            }
            catch
            {
                // Skip files we can't access
            }
        }
        
        if (skippedTooLarge > 0)
        {
            Console.WriteLine($"[extract] Skipped {skippedTooLarge} files larger than {_opts.MaxBytes / (1024 * 1024)} MB");
        }
        
        // Determine which files need processing based on hash
        var filesToProcess = new List<(string path, long size)>();
        var skippedUnchanged = 0;
        long bytesToProcess = 0;
        
        Console.WriteLine("[extract] Calculating hashes to detect changes...");
        var totalToHash = fileInfos.Count;
        var hashedCount = 0;
        foreach (var (file, size) in fileInfos)
        {
            ct.ThrowIfCancellationRequested();
            var fileHash = ComputeFileHash(file);
            var rel = Path.GetRelativePath(root, file);
            var fileId = CorpusIdFactory.ForFile(rel);
            
            if (existingEntries.TryGetValue(fileId, out var existing) && existing.hash == fileHash)
            {
                skippedUnchanged++;
            }
            else
            {
                filesToProcess.Add((file, size));
                bytesToProcess += size;
            }

            hashedCount++;
            if (totalToHash >= 50 && (hashedCount % 50 == 0 || hashedCount == totalToHash))
            {
                Console.Write($"\r[extract] Hashed {hashedCount}/{totalToHash} files   ");
            }
        }
        if (totalToHash >= 50)
        {
            Console.WriteLine();
        }
        
        Console.WriteLine($"[extract] Files unchanged: {skippedUnchanged}, files to process: {filesToProcess.Count}");
        Console.WriteLine($"[extract] Total data to process: {FormatBytes(bytesToProcess)}");

        if (filesToProcess.Count == 0 && skippedUnchanged == 0)
        {
            Console.WriteLine("[extract] No files to process");
            return;
        }
        
        // Process changed files with progress tracking
        var newEntries = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        var processedCount = 0;
        long processedBytes = 0;
        var startTime = DateTimeOffset.UtcNow;

        var partitions = filesToProcess
            .GroupBy(item => PartitionKey(root, item.path))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in partitions)
        {
            var displayName = group.Key == PartitionKeyRoot ? "(root)" : group.Key;
            Console.WriteLine($"[extract] Processing folder {displayName} ({group.Count()} files)");

            var batch = group.ToList();
            var localIndex = 0;
            while (localIndex < batch.Count)
            {
                ct.ThrowIfCancellationRequested();
                var round = batch.Skip(localIndex).Take(_scanThreads).ToList();

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

                foreach (var (path, size) in round)
                {
                    processedBytes += size;
                }
                processedCount += round.Count;

                var percentage = (processedCount * 100.0) / filesToProcess.Count;
                var elapsed = DateTimeOffset.UtcNow - startTime;
                var bytesPerSecond = processedBytes / Math.Max(elapsed.TotalSeconds, 1);
                var remainingBytes = bytesToProcess - processedBytes;
                var estimatedSecondsRemaining = remainingBytes / Math.Max(bytesPerSecond, 1);
                var eta = TimeSpan.FromSeconds(estimatedSecondsRemaining);

                Console.Write($"\r[extract] Progress: {processedCount}/{filesToProcess.Count} files ({percentage:F1}%) | " +
                             $"{FormatBytes(processedBytes)}/{FormatBytes(bytesToProcess)} | " +
                             $"ETA: {FormatTimeSpan(eta)}      ");

                localIndex += round.Count;
            }

            Console.WriteLine();
        }
        
        // Merge: write tree, then existing (unchanged) entries, then new entries
        var finalPath = _opts.OutPath;
        var backupPath = _opts.OutPath + ".backup";
        
        if (File.Exists(finalPath))
        {
            File.Copy(finalPath, backupPath, overwrite: true);
        }
        
        try
        {
            Console.WriteLine($"[extract] Writing merged corpus to {Path.GetFullPath(finalPath)}");
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
                    var fileId = CorpusIdFactory.ForFile(rel);
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
            Console.WriteLine($"[extract] Complete. Processed {filesToProcess.Count} files, kept {skippedUnchanged} unchanged in {FormatTimeSpan(totalTime)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[extract] Error writing corpus: {ex.Message}");
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, finalPath, overwrite: true);
                Console.WriteLine("[extract] Restored from backup");
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
            const ulong offsetBasis = 1469598103934665603;
            const ulong prime = 1099511628211;
            ulong hash = offsetBasis;

            using var fs = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[8192];
            int read;
            while ((read = fs.Read(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    hash ^= buffer[i];
                    hash *= prime;
                }
            }

            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
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
                if (FileRules.IsIgnoredDirectory(name)) continue;
                stack.Push(d);
            }
        }
    }

    static string PartitionKey(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        if (string.IsNullOrEmpty(rel) || rel == ".")
            return PartitionKeyRoot;

        var normalized = rel.Replace('\\', '/');
        if (normalized.StartsWith("../", StringComparison.Ordinal))
            return PartitionKeyRoot;

        var slash = normalized.IndexOf('/');
        return slash == -1 ? PartitionKeyRoot : normalized.Substring(0, slash);
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

            foreach (var d in dirs.Where(d => !FileRules.IsIgnoredDirectory(Path.GetFileName(d))).OrderByDescending(s => s, StringComparer.Ordinal))
                stack.Push((d, depth + 1));
        }
    }

    IEnumerable<string> ProcessFile(string root, string file)
    {
        if (!FileRules.IsAllowedFile(file)) yield break;

        var text = DocumentBuilder.ReadTextFile(file, _opts.MaxBytes, _opts.Truncate);
        if (text == null) yield break;

        var lang = FileRules.DetectLanguage(file);
        var rel = Path.GetRelativePath(root, file);
        var abs = Path.GetFullPath(file);
        var contentWithCtx = DocumentBuilder.BuildTextWithContext(root, rel, abs, lang, text.Value.Content);

        var fileObj = new JsonObj
        {
            id = CorpusIdFactory.ForFile(rel),
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
                id = CorpusIdFactory.ForClass(rel, cls.name),
                type = "class",
                title = ExtractionTools.HeuristicClassTitle(cls.name, rel, lang, cls.body),
                class_name = cls.name,
                path = rel.Replace('\\','/'),
                abs_path = abs,
                root = root,
                lang = lang,
                size = cls.body.Length,
                hash = ExtractionTools.Sha256(cls.body),
                text = DocumentBuilder.BuildTextWithContext(root, rel, abs, lang, cls.body)
            };
            yield return JsonSerializer.Serialize(clsObj);
        }

        // Extract functions/methods
        foreach (var fn in ExtractionTools.ExtractFunctions(rel, lang, text.Value.Content))
        {
            var fnObj = new JsonObj
            {
                id = CorpusIdFactory.ForFunction(rel, fn.name),
                type = "function",
                title = ExtractionTools.HeuristicFunctionTitle(fn.name, rel, lang, fn.body),
                function = fn.name,
                path = rel.Replace('\\','/'),
                abs_path = abs,
                root = root,
                lang = lang,
                size = fn.body.Length,
                hash = ExtractionTools.Sha256(fn.body),
                text = DocumentBuilder.BuildTextWithContext(root, rel, abs, lang, fn.body)
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
