// setup.cs (library component)
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SetupLib;

public sealed class SetupOptions
{
    public string BaseDir { get; init; } = AppContext.BaseDirectory;
    public string ModelTag { get; init; } = "llama3.2:1b-instruct-fp16";
    public string OllamaHost { get; init; } = "http://127.0.0.1:11434";
    public string CurrentLinkName { get; init; } = "llama1b";
    public string? SeedCorpusJsonl { get; init; } = null;
}

public sealed class SetupRunner : IAsyncDisposable
{
    readonly SetupOptions _opts;
    readonly HttpClient _http;

    readonly string _root;
    readonly string _cur;
    readonly string _models;
    readonly string _modelsCurrent;
    readonly string _modelsReleases;
    readonly string _data;
    readonly string _releaseDir;
    readonly string _symPath;

    public SetupRunner(SetupOptions options)
    {
        _opts = options;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        _root = Path.GetFullPath(_opts.BaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Path.GetFileName(_root).Equals("current", StringComparison.OrdinalIgnoreCase))
            _root = Directory.GetParent(_root)!.FullName;

        _cur = Path.Combine(_root, "current");
        _models = Path.Combine(_root, "models");
        _modelsCurrent = Path.Combine(_models, "current");
        _modelsReleases = Path.Combine(_models, "releases");
        _data = Path.Combine(_cur, "data");

        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var safeTag = _opts.ModelTag.Replace(':', '-');
        _releaseDir = Path.Combine(_modelsReleases, $"{safeTag}-{ts}");
        _symPath = Path.Combine(_modelsCurrent, _opts.CurrentLinkName);
    }

    public async Task<SetupSummary> RunAsync(CancellationToken ct = default)
    {
        EnsureDirs();
        EnsureCorpus();
        await EnsureOllamaReachableAsync(ct);
        await EnsureModelAsync(ct);
        await SnapshotAsync(ct);
        return new SetupSummary
        {
            Root = _root,
            Models = _models,
            Current = _cur,
            Release = _releaseDir,
            ModelTag = _opts.ModelTag,
            Host = _opts.OllamaHost
        };
    }

    void EnsureDirs()
    {
        Directory.CreateDirectory(_cur);
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_models);
        Directory.CreateDirectory(_modelsCurrent);
        Directory.CreateDirectory(_modelsReleases);
        Directory.CreateDirectory(_releaseDir);
    }

    void EnsureCorpus()
    {
        var path = Path.Combine(_data, "corpus.jsonl");
        if (File.Exists(path)) return;

        string contents = _opts.SeedCorpusJsonl ??
            string.Join('\n', new[]
            {
                """{"id":"doc1","title":"Underwriting Engine Overview","text":"The TRS Underwriting Engine evaluates business credit risk using a rule-based system, scorecards, and historical deal performance."}""",
                """{"id":"doc2","title":"Deal Status Change Workflow","text":"Deal status transitions are triggered by rule evaluations and underwriting outcomes. The new system replaces MEF with dependency injection for deterministic rule execution."}""",
                """{"id":"doc3","title":"Auto Decline Rules","text":"The auto-decline module checks for OFAC hits, mismatched tax IDs, and insufficient credit consent before declining a deal automatically."}"""
            }) + "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    async Task EnsureOllamaReachableAsync(CancellationToken ct)
    {
        var url = $"{_opts.OllamaHost.TrimEnd('/')}/api/tags";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Ollama API is not reachable at /api/tags. Ensure `ollama serve` is running.", ex);
        }
    }

    async Task EnsureModelAsync(CancellationToken ct)
    {
        var list = await GetTagsAsync(ct);
        var have = false;
        foreach (var m in list.models)
        {
            if (string.Equals(m.name, _opts.ModelTag, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
        }
        if (have) return;

        var url = $"{_opts.OllamaHost.TrimEnd('/')}/api/pull";
        var payload = JsonSerializer.Serialize(new { name = _opts.ModelTag, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var sr = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await sr.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            try
            {
                using var jd = JsonDocument.Parse(line);
                if (jd.RootElement.TryGetProperty("status", out var st))
                {
                    var s = st.GetString() ?? "";
                    if (s.Contains("success", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            catch { }
        }
    }

    async Task SnapshotAsync(CancellationToken ct)
    {
        var show = await GetShowAsync(ct);
        var tags = await GetTagsRawAsync(ct);

        Directory.CreateDirectory(_releaseDir);
        await File.WriteAllTextAsync(Path.Combine(_releaseDir, "manifest.txt"), show, new UTF8Encoding(false), ct);
        await File.WriteAllTextAsync(Path.Combine(_releaseDir, "ollama-tags.json"), tags, new UTF8Encoding(false), ct);

        Directory.CreateDirectory(Path.GetDirectoryName(_symPath)!);
        TryDeleteLinkOrDir(_symPath);
        if (!TryCreateSymlink(_symPath, _releaseDir))
        {
            MirrorDirectory(_releaseDir, _symPath);
        }
    }

    async Task<string> GetShowAsync(CancellationToken ct)
    {
        var url = $"{_opts.OllamaHost.TrimEnd('/')}/api/show";
        var payload = JsonSerializer.Serialize(new { name = _opts.ModelTag });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    async Task<string> GetTagsRawAsync(CancellationToken ct)
    {
        var url = $"{_opts.OllamaHost.TrimEnd('/')}/api/tags";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    async Task<TagsResponse> GetTagsAsync(CancellationToken ct)
    {
        var raw = await GetTagsRawAsync(ct);
        return JsonSerializer.Deserialize<TagsResponse>(raw) ?? new TagsResponse { models = Array.Empty<TagItem>() };
    }

    static void TryDeleteLinkOrDir(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(FileAttributes.ReparsePoint) || File.Exists(path))
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }
        catch { }
    }

    static bool TryCreateSymlink(string linkPath, string targetDir)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var cmd = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = $"/c mklink /D \"{linkPath}\" \"{targetDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(5000);
                return Directory.Exists(linkPath);
            }
            else
            {
                System.IO.Directory.CreateSymbolicLink(linkPath, targetDir);
                return Directory.Exists(linkPath);
            }
        }
        catch { return false; }
    }

    static void MirrorDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    public async ValueTask DisposeAsync() => _http.Dispose();

    sealed class TagsResponse { public TagItem[] models { get; set; } = Array.Empty<TagItem>(); }
    sealed class TagItem { public string name { get; set; } = ""; }

    public sealed record SetupSummary
    {
        public string Root { get; init; } = "";
        public string Models { get; init; } = "";
        public string Current { get; init; } = "";
        public string Release { get; init; } = "";
        public string ModelTag { get; init; } = "";
        public string Host { get; init; } = "";
    }
}
