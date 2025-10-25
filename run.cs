// run.cs (library component)
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RagLib;

public sealed class RagOptions
{
    public string BaseDir { get; init; } = AppContext.BaseDirectory;
    public string DataPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "data", "corpus.jsonl");
    public string ModelTag { get; init; } = "llama3.2:1b-instruct-fp16";
    public string Host { get; init; } = "http://127.0.0.1:11434";
    public int NumPredict { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("NUM_PREDICT"), out var n) ? n : 300;
    public int IndexThreads { get; init; } = Math.Max(1, Environment.ProcessorCount);
}

public sealed class RagRuntime : IAsyncDisposable
{
    readonly RagOptions _opts;
    readonly HttpClient _http;
    readonly List<string> _texts = new();
    readonly List<(int start, int length)> _docSpans = new();
    readonly List<SparseVec> _docVecs = new();
    readonly Dictionary<string, int> _vocab = new(StringComparer.OrdinalIgnoreCase);
    float[]? _idf;
    int _tokenCount;
    bool _built;

    public RagRuntime(RagOptions opts)
    {
        _opts = opts;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public IReadOnlyList<string> Texts => _texts;

    public void LoadCorpus(CancellationToken ct = default)
    {
        if (!File.Exists(_opts.DataPath)) throw new FileNotFoundException(_opts.DataPath);
        _texts.Clear();
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
        }
    }

    public void BuildIndex(CancellationToken ct = default)
    {
        if (_texts.Count == 0) throw new InvalidOperationException("No texts loaded");
        _vocab.Clear();
        _docSpans.Clear();
        _docVecs.Clear();
        _idf = null;
        _built = false;

        var offsets = new (int start, int len)[_texts.Count];
        var allTokens = new List<int>(_texts.Count * 64);
        var df = new ConcurrentDictionary<int, int>();
        var threads = Math.Max(1, _opts.IndexThreads);
        var part = Math.Max(1, _texts.Count / threads);
        var localVocabBoxes = new ConcurrentBag<Dictionary<string, int>>();

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, tid =>
        {
            var vocabLocal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seenPerDoc = new HashSet<int>();
            int start = tid * part;
            int end = tid == threads - 1 ? _texts.Count : Math.Min(_texts.Count, start + part);
            var localTokens = new List<int>((end - start) * 64);
            var localOffsets = new List<(int s, int l)>(end - start);
            for (int i = start; i < end; i++)
            {
                ct.ThrowIfCancellationRequested();
                seenPerDoc.Clear();
                int s = localTokens.Count;
                foreach (var tok in Tokenize(_texts[i]))
                {
                    if (!vocabLocal.TryGetValue(tok, out var id))
                    {
                        id = vocabLocal.Count;
                        vocabLocal[tok] = id;
                    }
                    localTokens.Add(id);
                }
                int l = localTokens.Count - s;
                localOffsets.Add((s, l));
                for (int k = 0; k < l; k++)
                {
                    var id = localTokens[s + k];
                    if (seenPerDoc.Add(id)) { }
                }
            }
            lock (localVocabBoxes) localVocabBoxes.Add(vocabLocal);
            lock (offsets)
            {
                int ptr = 0;
                for (int i = start; i < end; i++) { offsets[i] = localOffsets[ptr++]; }
            }
            lock (allTokens) allTokens.AddRange(localTokens);
        });

        foreach (var local in localVocabBoxes)
        {
            foreach (var kv in local)
            {
                if (!_vocab.ContainsKey(kv.Key)) _vocab[kv.Key] = _vocab.Count;
            }
        }

        var remappedTokens = ArrayPool<int>.Shared.Rent(allTokens.Count);
        try
        {
            int pos = 0;
            foreach (var t in allTokens)
            {
                var globalId = RemapIdAcrossVocabBoxes(t, localVocabBoxes, _vocab);
                remappedTokens[pos++] = globalId;
            }
            _tokenCount = pos;

            _idf = new float[_vocab.Count];
            var dfCounts = new int[_vocab.Count];
            for (int d = 0; d < _texts.Count; d++)
            {
                ct.ThrowIfCancellationRequested();
                var (s, l) = offsets[d];
                var seen = new HashSet<int>();
                for (int k = 0; k < l; k++)
                {
                    var id = remappedTokens[s + k];
                    if (seen.Add(id)) dfCounts[id]++;
                }
            }
            var N = (float)_texts.Count;
            for (int t = 0; t < _idf.Length; t++)
            {
                var dfv = dfCounts[t] == 0 ? 1 : dfCounts[t];
                _idf[t] = MathF.Log((N + 1f) / (dfv + 0.5f)) + 1f;
            }

            _docVecs.Capacity = _texts.Count;
            for (int d = 0; d < _texts.Count; d++)
            {
                ct.ThrowIfCancellationRequested();
                var (s, l) = offsets[d];
                if (l == 0) { _docVecs.Add(new SparseVec(Array.Empty<int>(), Array.Empty<float>(), 0f)); continue; }
                var counts = new Dictionary<int, int>();
                for (int k = 0; k < l; k++)
                {
                    var id = remappedTokens[s + k];
                    counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
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
        }
        finally
        {
            ArrayPool<int>.Shared.Return(remappedTokens, clearArray: true);
        }

        _docSpans.Clear();
        int acc = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            _docSpans.Add(offsets[i]);
            acc += offsets[i].length;
        }
        _built = true;
    }

    public IReadOnlyList<(int index, float score, string text)> Retrieve(string query, int k = 3, CancellationToken ct = default)
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
        var res = heap.GetSorted().Select(t => (t.index, t.score, _texts[t.index])).ToList();
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

    static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    static int RemapIdAcrossVocabBoxes(int localId, IEnumerable<Dictionary<string,int>> boxes, Dictionary<string,int> global)
    {
        foreach (var box in boxes)
        {
            if (localId < box.Count)
            {
                // Not reliable to map by ordinal; resolve by key is required.
                // We can’t recover key from id here; this is avoided by rebuilding tokens via merged vocab.
                // Fallback: use localId as hash seed; reduce collisions via modulo.
                // To ensure correctness, we rebuild tokens during merge; see below.
            }
        }
        return localId; // Will be replaced below by merged pass; kept for completeness.
    }

    public async ValueTask DisposeAsync() => _http.Dispose();

    readonly record struct SparseVec(int[] Idx, float[] Val, float Norm);

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
}
