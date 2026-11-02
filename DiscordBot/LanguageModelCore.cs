using NetMQ;
using NetMQ.Sockets;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordBot;

public sealed class ThinkingResponse
{
    public volatile string? Tokens;
    public volatile string? Thinking;
    public volatile string? Response;
    public volatile bool IsCompleted;
    public volatile Exception? Error;
}

/// <summary>Local LLM (ZMQ / OpenAI-compatible HTTP). Not used by any command yet; kept for future use.</summary>
public static class LanguageModelCore
{
    private const string ZmqEndpoint = "tcp://127.0.0.1:5555";
    private const string HttpEndpoint = "http://127.0.0.1:1234/v1/chat/completions";
    private const string DefaultModel = "baidu/ernie-4.5-21b-a3b";
    private const int MaxToken = 1000, MaxPromptLength = MaxToken * 4;

    // Previously a new HttpClient was created per request (sockets aren't released immediately after use)
    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature = 0.7,
        [property: JsonPropertyName("max_tokens")] int MaxTokens = -1,
        [property: JsonPropertyName("stream")] bool Stream = true);

    private sealed record PromptResponse(string Prompt, string Response)
    {
        public int TotalLength => Prompt.Length + Response.Length;
    }

    private static readonly ConcurrentDictionary<ulong, List<PromptResponse>> _history = new();

    public static bool CacheChatHistory { get; set; } = false;

    public static string GetModelResponse(string prompt, ulong uid)
    {
        string response = SendByZmq(BuildMergedPrompt(uid, prompt));
        StorePromptContext(uid, prompt, response);
        return response;
    }

    /// <summary>Returns immediately while a background stream fills Tokens; IsCompleted = true when done, Error is set on failure (failures used to be swallowed by fire-and-forget).</summary>
    public static ThinkingResponse GetModelResponseToken(string prompt, ulong uid, string model = DefaultModel)
    {
        var result = new ThinkingResponse();

        _ = Task.Run(async () =>
        {
            try
            {
                var full = new StringBuilder();
                await foreach (var token in StreamByHttpAsync(BuildRequest(uid, prompt, model)))
                {
                    full.Append(token);
                    result.Tokens = full.ToString();
                }

                string response = full.ToString();
                int end = response.IndexOf("</think>", StringComparison.Ordinal);
                if (end >= 0)
                {
                    string thinking = response[..end];
                    int start = thinking.IndexOf("<think>", StringComparison.Ordinal);
                    result.Thinking = start >= 0 ? thinking.Remove(start, "<think>".Length) : thinking;
                    result.Response = response[(end + "</think>".Length)..];
                }
                else
                {
                    result.Response = response;
                }

                StorePromptContext(uid, prompt, result.Response);
            }
            catch (Exception ex)
            {
                Log.Error("LLM streaming request failed", ex);
                result.Error = ex;
            }
            finally
            {
                result.IsCompleted = true;
            }
        });

        return result;
    }

    private static string SendByZmq(string prompt)
    {
        using var requester = new RequestSocket();
        requester.Connect(ZmqEndpoint);
        requester.SendFrame(prompt);
        return requester.ReceiveFrameString();
    }

    private static async IAsyncEnumerable<string> StreamByHttpAsync(ChatRequest payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, HttpEndpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.GetString() is { } text)
            {
                yield return text;
            }
        }
    }

    private static ChatRequest BuildRequest(ulong uid, string newPrompt, string model)
    {
        var messages = new List<ChatMessage>();

        if (CacheChatHistory && _history.TryGetValue(uid, out var prompts))
        {
            lock (prompts)
            {
                foreach (var p in prompts)
                {
                    messages.Add(new ChatMessage("user", p.Prompt));
                    messages.Add(new ChatMessage("assistant", p.Response));
                }
            }
        }

        messages.Add(new ChatMessage("user", newPrompt));
        return new ChatRequest(model, messages);
    }

    private static string BuildMergedPrompt(ulong uid, string newPrompt)
    {
        if (!CacheChatHistory || !_history.TryGetValue(uid, out var prompts))
            return newPrompt;

        var sb = new StringBuilder().AppendLine("Chat History : ");
        lock (prompts)
        {
            foreach (var p in prompts)
                sb.AppendLine($"User : {p.Prompt} , Response : {p.Response}");
        }
        return sb.AppendLine("New prompt : ").AppendLine(newPrompt).ToString();
    }

    private static void StorePromptContext(ulong uid, string prompt, string response)
    {
        if (!CacheChatHistory) return;

        var prompts = _history.GetOrAdd(uid, _ => new List<PromptResponse>());
        lock (prompts)
        {
            prompts.Add(new PromptResponse(prompt, response));

            int total = prompts.Sum(p => p.TotalLength);
            while (total > MaxPromptLength && prompts.Count > 0)
            {
                total -= prompts[0].TotalLength;
                prompts.RemoveAt(0);
            }
        }
    }

    public static void ClearChatHistory(ulong uid) => _history.TryRemove(uid, out _);
}
