using System.Text.Json;
using Erp.Api.Grpc.Ai;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Erp.Api.Services;

/// <summary>
/// AiService:* settings. Transport "grpc" (default): GrpcUrl is http://host:50051, or dns:///host:50051 to
/// round-robin across replicas. Transport "http": REST POST {BaseUrl}/ai/chat, for hosts whose private network
/// can't reach the AI service (e.g. a Render free instance, which only receives public traffic).
/// </summary>
public sealed class AiServiceOptions
{
    public const string Grpc = "grpc";
    public const string Http = "http";

    public string Transport { get; set; } = Grpc;
    public string GrpcUrl { get; set; } = "http://localhost:50051";
    /// <summary>AI service origin for Transport=http, e.g. https://erpwithfastapi-ai.onrender.com.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";
    /// <summary>Whole-call deadline. A question can take several LLM round trips (agent_max_steps).</summary>
    public int TimeoutSeconds { get; set; } = 90;
}

public record AiChatTurn(string Role, string Content);
public record AiChatAnswer(
    string Answer, IReadOnlyList<string> Sql, IReadOnlyList<string> Columns, JsonElement Rows, bool Cached, int LatencyMs);

/// <summary>The AI service refused or failed; Status is the HTTP status to answer the client with.</summary>
public sealed class AiServiceException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Call to the AI service: internal gRPC (erp.ai.v1.AiAssistant, see Protos/ai_assistant.proto) or its REST
/// /ai/chat, chosen by AiService:Transport. Both end in the AI service's chat_flow.answer().
/// </summary>
public interface IAiAssistantGateway
{
    /// <param name="accessToken">The caller's own JWT: the AI service re-checks session, rights and tenant from it.</param>
    Task<AiChatAnswer> ChatAsync(string accessToken, string question, IReadOnlyList<AiChatTurn> history, CancellationToken ct);
}

public sealed class GrpcAiAssistantGateway(
    AiAssistant.AiAssistantClient client, IOptions<AiServiceOptions> options, ILogger<GrpcAiAssistantGateway> log)
    : IAiAssistantGateway
{
    public async Task<AiChatAnswer> ChatAsync(
        string accessToken, string question, IReadOnlyList<AiChatTurn> history, CancellationToken ct)
    {
        var request = new ChatRequest { Question = question };
        request.History.AddRange(history.Select(t => new HistoryTurn { Role = t.Role, Content = t.Content }));
        var headers = new Metadata { { "authorization", $"Bearer {accessToken}" } };
        var deadline = DateTime.UtcNow.AddSeconds(options.Value.TimeoutSeconds);

        ChatReply reply;
        try
        {
            reply = await client.ChatAsync(request, headers, deadline, ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // the client went away
        }
        catch (RpcException e)
        {
            var mapped = Map(e);
            if (mapped.Status >= 500) log.LogWarning("AI gRPC call failed: {Code} {Detail}", e.StatusCode, e.Status.Detail);
            throw mapped;
        }

        using var rows = JsonDocument.Parse(string.IsNullOrEmpty(reply.RowsJson) ? "[]" : reply.RowsJson);
        return new AiChatAnswer(reply.Answer, reply.Sql.ToList(), reply.Columns.ToList(), rows.RootElement.Clone(),
            reply.Cached, reply.LatencyMs);
    }

    /// <summary>
    /// Client errors carry the AI service's own message (it is written for users). Server-side failures get a
    /// fixed message: UNAVAILABLE / INTERNAL details can be transport internals (addresses, socket errors).
    /// </summary>
    internal static AiServiceException Map(RpcException e) => e.StatusCode switch
    {
        StatusCode.InvalidArgument => new(StatusCodes.Status400BadRequest, Detail(e, "The question is not valid.")),
        StatusCode.Unauthenticated => new(StatusCodes.Status401Unauthorized, Detail(e, "Session is no longer active")),
        StatusCode.PermissionDenied => new(StatusCodes.Status403Forbidden, Detail(e, "You don't have access to the AI assistant.")),
        StatusCode.ResourceExhausted => new(StatusCodes.Status429TooManyRequests, Detail(e, "Rate limit exceeded. Try again in a minute.")),
        StatusCode.Unavailable => new(StatusCodes.Status503ServiceUnavailable, "The AI assistant is busy or unavailable, please retry shortly."),
        StatusCode.DeadlineExceeded => new(StatusCodes.Status504GatewayTimeout, "The AI assistant took too long to answer."),
        _ => new(StatusCodes.Status502BadGateway, "AI provider error."),
    };

    private static string Detail(RpcException e, string fallback) =>
        string.IsNullOrWhiteSpace(e.Status.Detail) ? fallback : e.Status.Detail;
}

/// <summary>
/// REST transport: POST {BaseUrl}/ai/chat with the caller's own bearer token. Errors are mapped exactly like the
/// gRPC ones: client errors keep the AI service's message (FastAPI's "detail"), server-side failures get a fixed one.
/// </summary>
public sealed class HttpAiAssistantGateway(
    HttpClient http, IOptions<AiServiceOptions> options, ILogger<HttpAiAssistantGateway> log) : IAiAssistantGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Turn(string Role, string Content);
    private sealed record Request(string Question, IReadOnlyList<Turn> History);
    private sealed record Reply(
        string Answer, List<string>? Sql, List<string>? Columns, JsonElement Rows, bool Cached,
        [property: System.Text.Json.Serialization.JsonPropertyName("latency_ms")] int LatencyMs);

    public async Task<AiChatAnswer> ChatAsync(
        string accessToken, string question, IReadOnlyList<AiChatTurn> history, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, "ai/chat")
        {
            Content = JsonContent.Create(
                new Request(question, history.Select(t => new Turn(t.Role, t.Content)).ToList()), options: Json),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var mapped = Map((int)response.StatusCode, Detail(body));
                if (mapped.Status >= 500) log.LogWarning("AI HTTP call failed: {Status} {Body}", (int)response.StatusCode, body);
                throw mapped;
            }

            var reply = await response.Content.ReadFromJsonAsync<Reply>(Json, timeout.Token)
                ?? throw new AiServiceException(StatusCodes.Status502BadGateway, "AI provider error.");
            var rows = reply.Rows.ValueKind == JsonValueKind.Array ? reply.Rows.Clone() : JsonDocument.Parse("[]").RootElement;
            return new AiChatAnswer(reply.Answer, reply.Sql ?? [], reply.Columns ?? [], rows, reply.Cached, reply.LatencyMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // the client went away
        }
        catch (OperationCanceledException)
        {
            throw new AiServiceException(StatusCodes.Status504GatewayTimeout, "The AI assistant took too long to answer.");
        }
        catch (HttpRequestException e)
        {
            log.LogWarning(e, "AI HTTP call could not connect");
            throw new AiServiceException(StatusCodes.Status503ServiceUnavailable,
                "The AI assistant is busy or unavailable, please retry shortly.");
        }
        catch (JsonException e)
        {
            log.LogWarning(e, "AI HTTP call returned an unreadable answer");
            throw new AiServiceException(StatusCodes.Status502BadGateway, "AI provider error.");
        }
    }

    /// <summary>Same mapping as the gRPC transport (FastAPI's 422 validation error counts as 400).</summary>
    internal static AiServiceException Map(int status, string? detail) => status switch
    {
        400 or 422 => new(StatusCodes.Status400BadRequest, detail ?? "The question is not valid."),
        401 => new(StatusCodes.Status401Unauthorized, detail ?? "Session is no longer active"),
        403 => new(StatusCodes.Status403Forbidden, detail ?? "You don't have access to the AI assistant."),
        429 => new(StatusCodes.Status429TooManyRequests, detail ?? "Rate limit exceeded. Try again in a minute."),
        503 => new(StatusCodes.Status503ServiceUnavailable, "The AI assistant is busy or unavailable, please retry shortly."),
        504 => new(StatusCodes.Status504GatewayTimeout, "The AI assistant took too long to answer."),
        _ => new(StatusCodes.Status502BadGateway, "AI provider error."),
    };

    /// <summary>FastAPI errors are {"detail": "..."}; validation errors have a list there, which isn't shown.</summary>
    private static string? Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("detail", out var d)
                   && d.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(d.GetString())
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null; // e.g. an HTML error page from a proxy
        }
    }
}
