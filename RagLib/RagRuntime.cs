// RagRuntime.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagLib;

public sealed class RagOptions
{
    public string BaseDir { get; init; }
    public string DataPath { get; init; }
    public string IndexCachePath { get; init; }
    public string ModelTag { get; init; }
    public string Host { get; init; }
    public int NumPredict { get; init; }
    public int IndexThreads { get; init; }

    public RagOptions()
    {
        BaseDir = AppContext.BaseDirectory;
        var defaultDataPath = Path.GetFullPath(Path.Combine(BaseDir, "..", "..", "..", "current", "Data", "corpus.jsonl"));
        DataPath = defaultDataPath;
        IndexCachePath = Path.ChangeExtension(defaultDataPath, ".tfidf.bin") ?? defaultDataPath + ".tfidf.bin";
        ModelTag = "llama3.2:1b-instruct-fp16";
        Host = "http://127.0.0.1:11434";
        NumPredict = int.TryParse(Environment.GetEnvironmentVariable("NUM_PREDICT"), out var n) ? n : 300;
        IndexThreads = Math.Max(1, Environment.ProcessorCount);
    }
}

public sealed class RagRuntime : IAsyncDisposable
{
    readonly RagOptions _opts;
    readonly HttpClient _http;
    readonly List<string> _texts = new();
    readonly List<string> _paths = new();
    readonly List<SparseVec> _docVecs = new();
    readonly Dictionary<string, int> _vocab = new(StringComparer.OrdinalIgnoreCase);
    float[]? _idf;
    bool _built;
    const int CacheFormatVersion = 1;

    public RagRuntime(RagOptions opts)
    {
        _opts = opts;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public IReadOnlyList<string> Texts => _texts;
    public IReadOnlyList<string> Paths => _paths;

    public void LoadCorpus(CancellationToken ct = default)
    {
        if (!File.Exists(_opts.DataPath)) throw new FileNotFoundException(_opts.DataPath);
        _texts.Clear();
        _paths.Clear();
        using var fs = File.OpenRead(_opts.DataPath);
        using var sr = new StreamReader(fs, new UTF8Encoding(false));
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            using var jd = JsonDocument.Parse(line);
            if (!jd.RootElement.TryGetProperty("text", out var te)) continue;
            var t = te.GetString() ?? "";
            _texts.Add(t);
            var path = jd.RootElement.TryGetProperty("path", out var pe) ? pe.GetString() ?? string.Empty : string.Empty;
            _paths.Add(path);
        }
    }

    public void BuildIndex(CancellationToken ct = default, Action<string>? onStatus = null)
    {
        if (_texts.Count == 0) throw new InvalidOperationException("No texts loaded");
        var fingerprint = ComputeCorpusFingerprint(_texts, _paths);
        if (TryLoadIndexFromCache(fingerprint, ct, onStatus))
        {
            _built = true;
            return;
        }

        _vocab.Clear();
        _docVecs.Clear();
        _idf = null;
        _built = false;
        _docVecs.Capacity = Math.Max(_docVecs.Capacity, _texts.Count);
        var docTermCounts = new List<Dictionary<int, int>>(_texts.Count);
        var dfCounts = new List<int>();

        onStatus?.Invoke($"Indexing {_texts.Count} documents...");

        var docCounter = 0;
        foreach (var text in _texts)
        {
            ct.ThrowIfCancellationRequested();
            var counts = new Dictionary<int, int>();
            var seen = new HashSet<int>();
            foreach (var token in Tokenize(text))
            {
                ct.ThrowIfCancellationRequested();
                var id = EnsureTokenId(token, dfCounts);
                counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
                if (seen.Add(id))
                {
                    dfCounts[id] = dfCounts[id] + 1;
                }
            }
            docTermCounts.Add(counts);
            docCounter++;
            if (docCounter % 100 == 0 || docCounter == _texts.Count)
            {
                onStatus?.Invoke($"Tokenized {docCounter}/{_texts.Count} documents");
            }
        }

        var vocabSize = _vocab.Count;
        _idf = new float[vocabSize];
        var totalDocs = (float)_texts.Count;
        onStatus?.Invoke($"Vocabulary size: {vocabSize} unique tokens");
        for (int i = 0; i < vocabSize; i++)
        {
            var dfv = dfCounts[i] == 0 ? 1 : dfCounts[i];
            _idf[i] = MathF.Log((totalDocs + 1f) / (dfv + 0.5f)) + 1f;
        }

        onStatus?.Invoke("Building TF-IDF vectors...");
        foreach (var counts in docTermCounts)
        {
            if (counts.Count == 0)
            {
                _docVecs.Add(new SparseVec(Array.Empty<int>(), Array.Empty<float>(), 0f));
                continue;
            }

            var idx = counts.Keys.OrderBy(x => x).ToArray();
            var vals = new float[idx.Length];
            float norm = 0f;
            for (int i = 0; i < idx.Length; i++)
            {
                var tf = counts[idx[i]];
                var w = (1f + MathF.Log(tf)) * _idf[idx[i]];
                vals[i] = w;
                norm += w * w;
            }
            norm = MathF.Sqrt(norm) + 1e-8f;
            for (int i = 0; i < vals.Length; i++) vals[i] /= norm;
            _docVecs.Add(new SparseVec(idx, vals, norm));
        }

        _built = true;
        onStatus?.Invoke("Index ready");
        SaveIndexToCache(fingerprint, ct, onStatus);
    }

    public IReadOnlyList<DocHit> Retrieve(string query, int k = 3, CancellationToken ct = default)
    {
        if (!_built) throw new InvalidOperationException("Index not built");
        var q = ToSparse(query, ct);
        var heap = new TopK(k);
        for (int i = 0; i < _docVecs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = Cosine(q, _docVecs[i]);
            heap.Add((i, s));
        }
        var res = heap.GetSorted().Select(t => new DocHit(t.index, t.score, _texts[t.index], _paths[t.index])).ToList();
        return res;
    }

    public async IAsyncEnumerable<string> StreamGenerateAsync(string prompt, int? numPredict = null, string? model = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{_opts.Host.TrimEnd('/')}/api/generate";
        var req = new GenerateRequest
        {
            model = model ?? _opts.ModelTag,
            prompt = prompt,
            stream = true,
            options = new GenerateOptions { num_predict = numPredict ?? _opts.NumPredict }
        };
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var sr = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await sr.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            GenerateStreamEvent? ev = null;
            try { ev = JsonSerializer.Deserialize<GenerateStreamEvent>(line); } catch { }
            if (ev?.response is string chunk && chunk.Length > 0) yield return chunk;
            if (ev?.done == true) yield break;
        }
    }

    public async Task<string> GenerateToStringAsync(string prompt, Action<GenProgress>? onProgress = null, int? numPredict = null, string? model = null, CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        int tokens = 0;
        await foreach (var chunk in StreamGenerateAsync(prompt, numPredict, model, ct))
        {
            sb.Append(chunk);
            tokens += Math.Max(1, CountApproxTokens(chunk));
            onProgress?.Invoke(new GenProgress
            {
                Tokens = tokens,
                Elapsed = DateTimeOffset.UtcNow - start,
                Fraction = numPredict.HasValue && numPredict.Value > 0 ? Math.Clamp(tokens / (float)numPredict.Value, 0f, 1f) : null
            });
        }
        return sb.ToString().Trim();
    }

    SparseVec ToSparse(string text, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        foreach (var tok in Tokenize(text))
        {
            ct.ThrowIfCancellationRequested();
            if (_vocab.TryGetValue(tok, out var id)) counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
        }
        if (counts.Count == 0) return new SparseVec(Array.Empty<int>(), Array.Empty<float>(), 1f);
        var idx = counts.Keys.OrderBy(x => x).ToArray();
        var vals = new float[idx.Length];
        float norm = 0f;
        for (int i = 0; i < idx.Length; i++)
        {
            var tf = counts[idx[i]];
            var w = (1f + MathF.Log(tf)) * _idf![idx[i]];
            vals[i] = w;
            norm += w * w;
        }
        norm = MathF.Sqrt(norm) + 1e-8f;
        for (int i = 0; i < vals.Length; i++) vals[i] /= norm;
        return new SparseVec(idx, vals, norm);
    }

    static float Cosine(in SparseVec a, in SparseVec b)
    {
        float s = 0f;
        int i = 0, j = 0;
        var ia = a.Idx; var va = a.Val;
        var ib = b.Idx; var vb = b.Val;
        while (i < ia.Length && j < ib.Length)
        {
            var da = ia[i]; var db = ib[j];
            if (da == db) { s += va[i] * vb[j]; i++; j++; }
            else if (da < db) i++;
            else j++;
        }
        return s;
    }

    int EnsureTokenId(string token, List<int> dfCounts)
    {
        if (!_vocab.TryGetValue(token, out var id))
        {
            id = _vocab.Count;
            _vocab[token] = id;
            dfCounts.Add(0);
        }
        return id;
    }

    static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (sb.Length > 0 && char.IsUpper(ch) && char.IsLower(sb[^1]))
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    bool TryLoadIndexFromCache(string fingerprint, CancellationToken ct, Action<string>? onStatus)
    {
        try
        {
            if (string.IsNullOrEmpty(_opts.IndexCachePath)) return false;
            if (!File.Exists(_opts.IndexCachePath)) return false;

            using var fs = new FileStream(_opts.IndexCachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            var version = br.ReadInt32();
            if (version != CacheFormatVersion) return false;

            var storedFingerprint = br.ReadString();
            if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal)) return false;

            var docCount = br.ReadInt32();
            if (docCount != _texts.Count) return false;

            var vocabCount = br.ReadInt32();
            if (vocabCount < 0) return false;
            var vocabList = new string[vocabCount];
            for (int i = 0; i < vocabCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                vocabList[i] = br.ReadString();
            }

            var idfCount = br.ReadInt32();
            if (idfCount != vocabCount) return false;
            var idf = new float[idfCount];
            for (int i = 0; i < idfCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                idf[i] = br.ReadSingle();
            }

            var docVecs = new List<SparseVec>(docCount);
            for (int doc = 0; doc < docCount; doc++)
            {
                ct.ThrowIfCancellationRequested();
                var idxLen = br.ReadInt32();
                if (idxLen < 0) return false;
                var idx = new int[idxLen];
                for (int j = 0; j < idxLen; j++) idx[j] = br.ReadInt32();

                var valLen = br.ReadInt32();
                if (valLen != idxLen) return false;
                var vals = new float[valLen];
                for (int j = 0; j < valLen; j++) vals[j] = br.ReadSingle();

                var norm = br.ReadSingle();
                docVecs.Add(new SparseVec(idx, vals, norm));
            }

            _vocab.Clear();
            for (int i = 0; i < vocabList.Length; i++)
            {
                _vocab[vocabList[i]] = i;
            }

            _docVecs.Clear();
            _docVecs.AddRange(docVecs);
            _idf = idf;
            onStatus?.Invoke("Loaded cached TF-IDF index");
            return true;
        }
        catch
        {
            return false;
        }
    }

    void SaveIndexToCache(string fingerprint, CancellationToken ct, Action<string>? onStatus)
    {
        try
        {
            if (string.IsNullOrEmpty(_opts.IndexCachePath)) return;
            var dir = Path.GetDirectoryName(_opts.IndexCachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(_opts.IndexCachePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

            bw.Write(CacheFormatVersion);
            bw.Write(fingerprint);
            bw.Write(_docVecs.Count);

            var vocabList = new string[_vocab.Count];
            foreach (var kvp in _vocab)
            {
                vocabList[kvp.Value] = kvp.Key;
            }

            bw.Write(vocabList.Length);
            for (int i = 0; i < vocabList.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                bw.Write(vocabList[i]);
            }

            var idf = _idf ?? Array.Empty<float>();
            bw.Write(idf.Length);
            for (int i = 0; i < idf.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                bw.Write(idf[i]);
            }

            foreach (var vec in _docVecs)
            {
                ct.ThrowIfCancellationRequested();
                bw.Write(vec.Idx.Length);
                foreach (var idx in vec.Idx) bw.Write(idx);
                bw.Write(vec.Val.Length);
                foreach (var val in vec.Val) bw.Write(val);
                bw.Write(vec.Norm);
            }

            onStatus?.Invoke("Saved TF-IDF index cache");
        }
        catch
        {
            // Cache persistence failures are non-fatal.
        }
    }

    static string ComputeCorpusFingerprint(IReadOnlyList<string> texts, IReadOnlyList<string> paths)
    {
        using var sha = SHA256.Create();
        var newline = new byte[] { (byte)'\n' };
        for (int i = 0; i < texts.Count; i++)
        {
            var pathBytes = Encoding.UTF8.GetBytes(paths[i] ?? string.Empty);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            sha.TransformBlock(newline, 0, newline.Length, null, 0);
            var textBytes = Encoding.UTF8.GetBytes(texts[i] ?? string.Empty);
            sha.TransformBlock(textBytes, 0, textBytes.Length, null, 0);
            sha.TransformBlock(newline, 0, newline.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    readonly record struct SparseVec(int[] Idx, float[] Val, float Norm);

    public readonly record struct DocHit(int Index, float Score, string Text, string Path)
    {
        public bool HasPath => !string.IsNullOrWhiteSpace(Path);
    }

    public sealed record GenProgress
    {
        public int Tokens { get; init; }
        public TimeSpan Elapsed { get; init; }
        public float? Fraction { get; init; }
    }

    sealed class GenerateOptions
    {
        [JsonPropertyName("num_predict")] public int num_predict { get; set; }
    }

    sealed class GenerateRequest
    {
        public string model { get; set; } = "";
        public string prompt { get; set; } = "";
        public bool stream { get; set; } = true;
        public GenerateOptions options { get; set; } = new();
    }

    sealed class GenerateStreamEvent
    {
        public string? response { get; set; }
        public bool done { get; set; }
    }

    static int CountApproxTokens(string s)
    {
        int c = 0, inTok = 0;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (inTok == 1) { c++; inTok = 0; } }
            else inTok = 1;
        }
        if (inTok == 1) c++;
        return Math.Max(1, c);
    }

    // TopK heap structure for efficient top-k retrieval
    sealed class TopK
    {
        readonly (int index, float score)[] _heap;
        int _size;
        readonly int _k;

        public TopK(int k)
        {
            _k = k;
            _heap = new (int index, float score)[k];
            _size = 0;
        }

        public void Add((int index, float score) item)
        {
            if (_size < _k)
            {
                _heap[_size++] = item;
                if (_size == _k) Heapify();
            }
            else if (item.score > _heap[0].score)
            {
                _heap[0] = item;
                SiftDown(0);
            }
        }

        public List<(int index, float score)> GetSorted()
        {
            var result = new List<(int index, float score)>(_size);
            for (int i = 0; i < _size; i++)
            {
                result.Add(_heap[i]);
            }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            return result;
        }

        void Heapify()
        {
            for (int i = _size / 2 - 1; i >= 0; i--)
            {
                SiftDown(i);
            }
        }

        void SiftDown(int pos)
        {
            var item = _heap[pos];
            while (pos < _size / 2)
            {
                int child = 2 * pos + 1;
                if (child + 1 < _size && _heap[child + 1].score < _heap[child].score)
                {
                    child++;
                }
                if (item.score <= _heap[child].score) break;
                _heap[pos] = _heap[child];
                pos = child;
            }
            _heap[pos] = item;
        }
    }
}
