using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Api.UnitTests;

public class HttpAiAssistantGatewayTests
{
    /// <summary>Records the request and answers with the given function (or throws from it).</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await answer(request, ct);
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static FakeHandler Handler(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new((_, _) => Task.FromResult(Respond(status, body, mediaType)));

    private static HttpAiAssistantGateway Gateway(HttpMessageHandler handler, int timeoutSeconds = 90) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://ai.example.test/") },
            Options.Create(new AiServiceOptions { TimeoutSeconds = timeoutSeconds }),
            NullLogger<HttpAiAssistantGateway>.Instance);

    [Fact]
    public async Task Posts_token_question_and_history_and_parses_the_answer()
    {
        var handler = Handler(HttpStatusCode.OK, """
            {"answer": "You have 42 customers.", "sql": ["SELECT 1"], "columns": ["n", "day"],
             "rows": [[42, "2026-01-02"], [12.5, null]], "cached": true, "latency_ms": 17}
            """);

        var a = await Gateway(handler).ChatAsync("tok-123", "How many?",
            [new AiChatTurn("user", "hi"), new AiChatTurn("assistant", "hello")], CancellationToken.None);

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri.Should().Be(new Uri("https://ai.example.test/ai/chat"));
        handler.Request.Headers.Authorization!.ToString().Should().Be("Bearer tok-123");
        using (var sent = JsonDocument.Parse(handler.Body!))
        {
            sent.RootElement.GetProperty("question").GetString().Should().Be("How many?");
            sent.RootElement.GetProperty("history").EnumerateArray()
                .Select(t => (t.GetProperty("role").GetString(), t.GetProperty("content").GetString()))
                .Should().Equal(("user", "hi"), ("assistant", "hello"));
        }

        a.Answer.Should().Be("You have 42 customers.");
        a.Sql.Should().Equal("SELECT 1");
        a.Columns.Should().Equal("n", "day");
        JsonSerializer.Serialize(a.Rows).Should().Be("""[[42,"2026-01-02"],[12.5,null]]""");
        a.Cached.Should().BeTrue();
        a.LatencyMs.Should().Be(17);
    }

    [Fact]
    public async Task Missing_optional_fields_become_empty()
    {
        var a = await Gateway(Handler(HttpStatusCode.OK, """{"answer": "ok"}"""))
            .ChatAsync("t", "q", [], CancellationToken.None);

        a.Sql.Should().BeEmpty();
        a.Columns.Should().BeEmpty();
        a.Rows.GetArrayLength().Should().Be(0);
        a.Cached.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"detail": "question: too long"}""", StatusCodes.Status400BadRequest, "question: too long")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail": [{"loc": ["body"], "msg": "x"}]}""", StatusCodes.Status400BadRequest, "The question is not valid.")]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail": "Session is no longer active"}""", StatusCodes.Status401Unauthorized, "Session is no longer active")]
    [InlineData(HttpStatusCode.Forbidden, """{"detail": "No AI"}""", StatusCodes.Status403Forbidden, "No AI")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"detail": "Rate limit exceeded."}""", StatusCodes.Status429TooManyRequests, "Rate limit exceeded.")]
    [InlineData(HttpStatusCode.Unauthorized, "", StatusCodes.Status401Unauthorized, "Session is no longer active")]
    // Server-side failures never pass the upstream text through
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"detail": "AI is busy, please retry shortly."}""", StatusCodes.Status503ServiceUnavailable,
        "The AI assistant is busy or unavailable, please retry shortly.")]
    [InlineData(HttpStatusCode.GatewayTimeout, "<html>timeout</html>", StatusCodes.Status504GatewayTimeout, "The AI assistant took too long to answer.")]
    [InlineData(HttpStatusCode.InternalServerError, """{"detail": "Traceback ..."}""", StatusCodes.Status502BadGateway, "AI provider error.")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", StatusCodes.Status502BadGateway, "AI provider error.")]
    [InlineData(HttpStatusCode.NotFound, "Not Found", StatusCodes.Status502BadGateway, "AI provider error.")]
    public async Task Maps_http_status_to_api_status(HttpStatusCode upstream, string body, int status, string message)
    {
        var gateway = Gateway(Handler(upstream, body));

        var act = () => gateway.ChatAsync("t", "q", [], CancellationToken.None);

        var e = (await act.Should().ThrowAsync<AiServiceException>()).Which;
        e.Status.Should().Be(status);
        e.Message.Should().Be(message);
    }

    [Fact]
    public async Task Connection_failure_is_503_without_transport_details()
    {
        var gateway = Gateway(new FakeHandler((_, _) =>
            throw new HttpRequestException("No such host is known. (ai.example.test:443)")));

        var e = (await gateway.Invoking(g => g.ChatAsync("t", "q", [], CancellationToken.None))
            .Should().ThrowAsync<AiServiceException>()).Which;

        e.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        e.Message.Should().NotContain("ai.example.test");
    }

    [Fact]
    public async Task Unreadable_success_body_is_502()
    {
        var gateway = Gateway(Handler(HttpStatusCode.OK, "<html>not json</html>", "text/html"));

        var e = (await gateway.Invoking(g => g.ChatAsync("t", "q", [], CancellationToken.None))
            .Should().ThrowAsync<AiServiceException>()).Which;

        e.Status.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task Slow_answer_is_504_after_the_timeout()
    {
        var gateway = Gateway(new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Respond(HttpStatusCode.OK, "{}");
        }), timeoutSeconds: 1);

        var e = (await gateway.Invoking(g => g.ChatAsync("t", "q", [], CancellationToken.None))
            .Should().ThrowAsync<AiServiceException>()).Which;

        e.Status.Should().Be(StatusCodes.Status504GatewayTimeout);
    }

    [Fact]
    public async Task Client_cancellation_is_not_reported_as_an_ai_error()
    {
        using var cts = new CancellationTokenSource();
        var gateway = Gateway(new FakeHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Respond(HttpStatusCode.OK, "{}");
        }));

        var act = () => gateway.ChatAsync("t", "q", [], cts.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().NotBeOfType<AiServiceException>();
    }
}
