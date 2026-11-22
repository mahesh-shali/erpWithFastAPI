using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using Erp.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Api.IntegrationTests;

/// <summary>New-device sign-in alerts: the email sender is replaced by one that records what would be sent.</summary>
[Collection(ApiCollection.Name)]
public class LoginAlertTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private const string ChromeWindowsNewer = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string FirefoxAndroid = "Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0";
    private const string Desktop = "ERP-Desktop/1.2.0 (win32) Electron/33.2.0";

    private sealed class RecordingSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken ct) { Sent.Enqueue(message); return Task.CompletedTask; }
    }

    private static async Task<HttpStatusCode> Login(HttpClient c, string email, string userAgent)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new LoginRequest(email, Pw)) };
        req.Headers.UserAgent.ParseAdd(userAgent);
        return (await c.SendAsync(req)).StatusCode;
    }

    private static async Task<List<EmailMessage>> WaitForEmails(RecordingSender s, int count)
    {
        for (var i = 0; i < 50 && s.Sent.Count < count; i++) await Task.Delay(100);
        await Task.Delay(300); // anything unexpected would have arrived by now as well
        return [.. s.Sent];
    }

    [Fact]
    public async Task Signing_in_from_a_new_device_emails_the_user_but_known_devices_and_first_sign_in_do_not()
    {
        var sender = new RecordingSender();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(sender)));
        var c = app.CreateClient();

        // Registration (the owner's first session) never alerts
        var reg = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Alert Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        reg.EnsureSuccessStatusCode();
        var owner = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);

        var email = $"lead{Guid.NewGuid():N}@x.com";
        (await c.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Lena Lead", Pw, "Manager")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var anon = app.CreateClient();
        (await Login(anon, email, ChromeWindows)).Should().Be(HttpStatusCode.OK);      // first sign-in: no alert
        (await Login(anon, email, ChromeWindowsNewer)).Should().Be(HttpStatusCode.OK); // browser update: same device
        (await Login(anon, email, FirefoxAndroid)).Should().Be(HttpStatusCode.OK);     // new device: alert
        (await Login(anon, email, FirefoxAndroid)).Should().Be(HttpStatusCode.OK);     // now known
        (await Login(anon, email, Desktop)).Should().Be(HttpStatusCode.OK);            // new device: alert

        var sent = await WaitForEmails(sender, 2);
        sent.Should().HaveCount(2);
        sent.Should().OnlyContain(m => m.To == email && m.Subject == "New sign-in to your ERP account");
        sent[0].TextBody.Should().Contain("Firefox on Android").And.Contain("Lena Lead");
        sent[1].TextBody.Should().Contain("ERP Desktop app on Windows");
        sent[1].HtmlBody.Should().Contain("ERP Desktop app on Windows");
    }

    [Fact]
    public async Task A_failed_sign_in_sends_nothing()
    {
        var sender = new RecordingSender();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(sender)));
        var c = app.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("staff@demo.com", "wrong-password")),
        };
        req.Headers.UserAgent.ParseAdd(FirefoxAndroid);
        (await c.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await WaitForEmails(sender, 1)).Should().BeEmpty();
    }
}
