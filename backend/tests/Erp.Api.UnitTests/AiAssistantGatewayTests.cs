using Erp.Api.Grpc.Ai;
using Erp.Api.Services;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Api.UnitTests;

public class AiAssistantGatewayTests
{
    /// <summary>Generated client with ChatAsync overridden: records the call, answers or throws.</summary>
    private sealed class FakeClient(Func<ChatRequest, ChatReply> answer) : AiAssistant.AiAssistantClient
    {
        public ChatRequest? Request;
        public Metadata? Headers;
        public DateTime? Deadline;

        public override AsyncUnaryCall<ChatReply> ChatAsync(
            ChatRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            Request = request; Headers = headers; Deadline = deadline;
            var reply = Task.Run(() => answer(request), cancellationToken);
            return new AsyncUnaryCall<ChatReply>(reply, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
    }

    private static GrpcAiAssistantGateway Gateway(FakeClient client, int timeoutSeconds = 90) =>
        new(client, Options.Create(new AiServiceOptions { TimeoutSeconds = timeoutSeconds }),
            NullLogger<GrpcAiAssistantGateway>.Instance);

    [Fact]
    public async Task Forwards_token_question_and_history_and_parses_rows()
    {
        var client = new FakeClient(_ => new ChatReply
        {
            Answer = "You have 42 customers.", Sql = { "SELECT 1" }, Columns = { "n", "day" },
            RowsJson = """[[42, "2026-01-02"], [12.5, null]]""", Cached = true, LatencyMs = 17,
        });

        var a = await Gateway(client).ChatAsync("tok-123", "How many?",
            [new AiChatTurn("user", "hi"), new AiChatTurn("assistant", "hello")], CancellationToken.None);

        client.Headers!.GetValue("authorization").Should().Be("Bearer tok-123");
        client.Request!.Question.Should().Be("How many?");
        client.Request.History.Select(t => (t.Role, t.Content)).Should().Equal(("user", "hi"), ("assistant", "hello"));
        client.Deadline.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(90), TimeSpan.FromSeconds(5));

        a.Answer.Should().Be("You have 42 customers.");
        a.Sql.Should().Equal("SELECT 1");
        a.Columns.Should().Equal("n", "day");
        a.Rows.GetRawText().Should().Be("""[[42, "2026-01-02"], [12.5, null]]""");
        a.Cached.Should().BeTrue();
        a.LatencyMs.Should().Be(17);
    }

    [Fact]
    public async Task Empty_rows_json_becomes_an_empty_array()
    {
        var a = await Gateway(new FakeClient(_ => new ChatReply { Answer = "ok" }))
            .ChatAsync("t", "q", [], CancellationToken.None);
        a.Rows.GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument, "question: too long", StatusCodes.Status400BadRequest, "question: too long")]
    [InlineData(StatusCode.Unauthenticated, "Session is no longer active", StatusCodes.Status401Unauthorized, "Session is no longer active")]
    [InlineData(StatusCode.PermissionDenied, "No AI", StatusCodes.Status403Forbidden, "No AI")]
    [InlineData(StatusCode.ResourceExhausted, "Rate limit exceeded.", StatusCodes.Status429TooManyRequests, "Rate limit exceeded.")]
    [InlineData(StatusCode.Unauthenticated, "", StatusCodes.Status401Unauthorized, "Session is no longer active")]
    // Server-side failures never leak transport details
    [InlineData(StatusCode.Unavailable, "Error connecting to subchannel 10.0.0.7:50051", StatusCodes.Status503ServiceUnavailable,
        "The AI assistant is busy or unavailable, please retry shortly.")]
    [InlineData(StatusCode.DeadlineExceeded, "deadline", StatusCodes.Status504GatewayTimeout, "The AI assistant took too long to answer.")]
    [InlineData(StatusCode.Internal, "Traceback ...", StatusCodes.Status502BadGateway, "AI provider error.")]
    [InlineData(StatusCode.Unknown, "boom", StatusCodes.Status502BadGateway, "AI provider error.")]
    public async Task Maps_grpc_status_to_http_status(StatusCode code, string detail, int status, string message)
    {
        var gateway = Gateway(new FakeClient(_ => throw new RpcException(new Status(code, detail))));

        var act = () => gateway.ChatAsync("t", "q", [], CancellationToken.None);

        var e = (await act.Should().ThrowAsync<AiServiceException>()).Which;
        e.Status.Should().Be(status);
        e.Message.Should().Be(message);
    }

    [Fact]
    public async Task Client_cancellation_is_not_reported_as_an_ai_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var gateway = Gateway(new FakeClient(_ => throw new RpcException(new Status(StatusCode.Cancelled, "cancelled"))));

        var act = () => gateway.ChatAsync("t", "q", [], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
