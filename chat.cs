// chat.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OllamaChatLib;

public sealed class ChatOptions
{
    public string Host { get; init; } = "http://127.0.0.1:11434";
    public string ModelTag { get; init; } = "llama3.2:1b-instruct-fp16";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class OllamaChatClient : IAsyncDisposable
{
    readonly ChatOptions _opts;
    readonly HttpClient _http;

    public OllamaChatClient(ChatOptions opts)
    {
        _opts = opts;
        _http = new HttpClient { Timeout = _opts.Timeout };
    }

    /// <summary>
    /// Sends a single message to the Ollama /api/chat endpoint and returns the text response.
    /// </summary>
    public Task<string> ChatAsync(string message, CancellationToken ct = default)
        => ChatAsync(new[] { new ChatMessage("user", message) }, ct);

    /// <summary>
    /// Sends multiple chat messages to the Ollama model.
    /// </summary>
    public async Task<string> ChatAsync(IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        var url = _opts.Host.TrimEnd('/') + "/api/chat";

        var reqBody = new ChatRequest
        {
            model = _opts.ModelTag,
            stream = false,
            messages = new List<ChatMessage>(messages)
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var obj = JsonSerializer.Deserialize<ChatResponse>(json) ?? new ChatResponse();
        var text = obj.message?.content ?? obj.response ?? "";
        return text.Trim();
    }

    public async ValueTask DisposeAsync() => _http.Dispose();

    // ---- Data Models ----

    public sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; }
        [JsonPropertyName("content")] public string Content { get; set; }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    sealed class ChatRequest
    {
        public string model { get; set; } = "";
        public bool stream { get; set; } = false;
        public List<ChatMessage> messages { get; set; } = new();
    }

    sealed class ChatResponse
    {
        public ChatMessage? message { get; set; }
        public string? response { get; set; }
    }
}
