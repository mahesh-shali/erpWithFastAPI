using System.Text.Json;
using Erp.Api.Grpc.Ai;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Erp.Api.Services;

/// <summary>AiService:* settings. GrpcUrl: http://host:50051, or dns:///host:50051 to round-robin across replicas.</summary>
public sealed class AiServiceOptions
{
    public string GrpcUrl { get; set; } = "http://localhost:50051";
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

/// <summary>Internal call to the AI service (gRPC erp.ai.v1.AiAssistant, see Protos/ai_assistant.proto).</summary>
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
