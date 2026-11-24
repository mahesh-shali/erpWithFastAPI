using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Api.Contracts;
using Erp.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Api.IntegrationTests;

/// <summary>POST /api/ai/chat: auth + page right in the API, then the (faked) internal gRPC call to the AI service.</summary>
[Collection(ApiCollection.Name)]
public class AiChatTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private sealed class FakeGateway : IAiAssistantGateway
    {
        public string? Token;
        public string? Question;
        public IReadOnlyList<AiChatTurn>? History;
        public AiServiceException? Fail;

        public Task<AiChatAnswer> ChatAsync(string accessToken, string question, IReadOnlyList<AiChatTurn> history, CancellationToken ct)
        {
            (Token, Question, History) = (accessToken, question, history);
            if (Fail is not null) throw Fail;
            using var rows = JsonDocument.Parse("""[[42, "2026-01-02", 12.5]]""");
            return Task.FromResult(new AiChatAnswer("You have 42 customers.", ["SELECT 1"], ["n", "day", "x"],
                rows.RootElement.Clone(), false, 31));
        }
    }

    private async Task<(HttpClient Client, string Token, FakeGateway Ai)> Owner()
    {
        var ai = new FakeGateway();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAiAssistantGateway>(ai)));
        var c = app.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("AI Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (c, auth.AccessToken, ai);
    }

    [Fact]
    public async Task Forwards_the_callers_token_and_returns_the_rest_shape()
    {
        var (c, token, ai) = await Owner();

        var res = await c.PostAsJsonAsync("/api/ai/chat", new
        {
            question = "How many customers?",
            history = new[] { new { role = "user", content = "hi" }, new { role = "assistant", content = "hello" } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        ai.Token.Should().Be(token);
        ai.Question.Should().Be("How many customers?");
        ai.History.Should().Equal(new AiChatTurn("user", "hi"), new AiChatTurn("assistant", "hello"));

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("answer").GetString().Should().Be("You have 42 customers.");
        body.GetProperty("sql")[0].GetString().Should().Be("SELECT 1");
        body.GetProperty("columns").GetArrayLength().Should().Be(3);
        body.GetProperty("rows").GetRawText().Should().Be("""[[42,"2026-01-02",12.5]]""");
        body.GetProperty("cached").GetBoolean().Should().BeFalse();
        body.GetProperty("latency_ms").GetInt32().Should().Be(31); // same key as the AI service's /ai/chat
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var (c, _, ai) = await Owner();
        c.DefaultRequestHeaders.Authorization = null;

        (await c.PostAsJsonAsync("/api/ai/chat", new { question = "hi" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ai.Question.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"question":""}""")]
    [InlineData("""{"question":"ok","history":[{"role":"robot","content":"x"}]}""")]
    public async Task Invalid_input_is_rejected_before_calling_the_ai_service(string json)
    {
        var (c, _, ai) = await Owner();

        var res = await c.PostAsync("/api/ai/chat", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ai.Question.Should().BeNull();
    }

    [Fact]
    public async Task Ai_service_refusals_keep_their_status_and_message()
    {
        var (c, _, ai) = await Owner();
        ai.Fail = new AiServiceException(429, "Rate limit exceeded. Try again in a minute.");

        var res = await c.PostAsJsonAsync("/api/ai/chat", new { question = "hi" });

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var problem = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("title").GetString().Should().Be("Rate limit exceeded. Try again in a minute.");
    }
}
