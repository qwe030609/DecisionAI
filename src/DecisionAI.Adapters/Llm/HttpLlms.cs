// ============================================================================
//  LLM 轉接器（純 HttpClient）
//   - AnthropicLlm         Anthropic Messages API
//   - OpenAiCompatibleLlm  任何 OpenAI 相容端點（其他廠商 / 本機）
// ============================================================================

using System.Text;
using System.Text.Json.Nodes;
using DecisionAI.Core.Ports;

namespace DecisionAI.Adapters.Llm;

public sealed class AnthropicLlm : ILlm
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _model, _apiKey;
    public string Name => "anthropic:" + _model;

    public AnthropicLlm(string model, string? apiKey = null)
    {
        _model = model;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new InvalidOperationException("ANTHROPIC_API_KEY 未設定");
    }

    public async Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"]       = _model,
            ["max_tokens"]  = 2048,
            ["temperature"] = req.Temperature,
            ["system"]      = req.System,
            ["messages"]    = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = req.User }),
        };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        msg.Headers.Add("x-api-key", _apiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");
        msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await Http.SendAsync(msg, ct);
        string raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode}: {raw}");

        var content = JsonNode.Parse(raw)!["content"]!.AsArray();
        return string.Concat(content.Where(b => (string?)b!["type"] == "text").Select(b => (string?)b!["text"]));
    }
}

public sealed class OpenAiCompatibleLlm : ILlm
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _baseUrl, _model;
    private readonly string? _apiKey;
    public string Name { get; }

    public OpenAiCompatibleLlm(string vendor, string baseUrl, string model, string? apiKey)
    {
        Name = $"{vendor}:{model}"; _baseUrl = baseUrl.TrimEnd('/'); _model = model; _apiKey = apiKey;
    }

    public async Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"]       = _model,
            ["temperature"] = req.Temperature,
            ["messages"]    = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = req.System },
                new JsonObject { ["role"] = "user",   ["content"] = req.User }),
        };
        using var msg = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (_apiKey is not null) msg.Headers.Add("Authorization", "Bearer " + _apiKey);
        msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var res = await Http.SendAsync(msg, ct);
        string raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode}: {raw}");
        return (string?)JsonNode.Parse(raw)!["choices"]![0]!["message"]!["content"] ?? "";
    }
}
