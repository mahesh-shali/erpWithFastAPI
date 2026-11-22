using Erp.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erp.Api.UnitTests;

public class ChatValidationTests
{
    [Fact]
    public void Direct_key_is_the_same_whoever_starts_the_chat()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        ChatService.DirectKeyOf(a, b).Should().Be(ChatService.DirectKeyOf(b, a));
        ChatService.DirectKeyOf(a, b).Should().NotBe(ChatService.DirectKeyOf(a, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(12, 12, 12, true)]
    [InlineData(11, 12, 12, false)]
    [InlineData(13, 12, 12, false)]
    [InlineData(60, 60, 60, true)]
    public void Base64_fields_must_decode_to_the_expected_length(int actual, int min, int max, bool ok)
    {
        var value = Convert.ToBase64String(new byte[actual]);
        var act = () => ChatService.Base64(value, "field", min, max);
        if (ok) act.Should().NotThrow().Which.Should().HaveCount(actual);
        else act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    public void Invalid_base64_is_rejected(string? value) =>
        FluentActions.Invoking(() => ChatService.Base64(value, "field", 1, 100)).Should().Throw<DomainException>();

    [Fact]
    public void Oversized_values_are_rejected_before_decoding() =>
        FluentActions.Invoking(() => ChatService.Base64(new string('A', 10_000), "field", 1, 100)).Should().Throw<DomainException>();
}

public class ChatEventBusTests
{
    private static ChatEventBus Bus() => new(new ConfigurationBuilder().Build(), NullLogger<ChatEventBus>.Instance);

    [Fact]
    public async Task Events_reach_only_the_targeted_users_streams()
    {
        var bus = Bus();
        await bus.StartAsync(default); // no Redis configured: in-process delivery
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        using var subA1 = bus.Subscribe(a);
        using var subA2 = bus.Subscribe(a); // e.g. web + desktop
        using var subB = bus.Subscribe(b);

        var conv = Guid.NewGuid();
        await bus.PublishAsync([a], new ChatEvent("message", conv, 7));

        subA1.Reader.TryRead(out var e1).Should().BeTrue();
        e1!.ConversationId.Should().Be(conv);
        subA2.Reader.TryRead(out _).Should().BeTrue();
        subB.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Disposed_subscriptions_stop_receiving_and_are_removed()
    {
        var bus = Bus();
        var u = Guid.NewGuid();
        var sub = bus.Subscribe(u);
        bus.LocalSubscriberCount(u).Should().Be(1);
        sub.Dispose();
        sub.Dispose(); // idempotent
        bus.LocalSubscriberCount(u).Should().Be(0);
        await bus.PublishAsync([u], new ChatEvent("message"));
        sub.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task A_slow_client_keeps_only_the_latest_events()
    {
        var bus = Bus();
        var u = Guid.NewGuid();
        using var sub = bus.Subscribe(u);
        for (var i = 0; i < 300; i++) await bus.PublishAsync([u], new ChatEvent("message", MessageId: i));
        var ids = new List<long>();
        while (sub.Reader.TryRead(out var e)) ids.Add(e.MessageId!.Value);
        ids.Should().HaveCount(256);
        ids[^1].Should().Be(299);
    }
}

public class DeviceInfoTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "Safari on macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0 Mobile/15E148 Safari/604.1", "Chrome on iOS")]
    [InlineData("Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0", "Firefox on Android")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox on Linux")]
    [InlineData("ERP-Desktop/1.2.0 (win32) Electron/33.2.0", "ERP Desktop app on Windows")]
    [InlineData("ERP-Desktop/1.2.0 (darwin) Electron/33.2.0", "ERP Desktop app on macOS")]
    [InlineData("curl/8.4.0", "curl")]
    [InlineData(null, "Unknown device")]
    [InlineData("", "Unknown device")]
    public void Describes_the_app_and_operating_system(string? userAgent, string expected) =>
        DeviceInfo.Describe(userAgent).Should().Be(expected);

    [Fact]
    public void Browser_updates_are_the_same_device() =>
        DeviceInfo.Describe("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0.0.0 Safari/537.36")
            .Should().Be(DeviceInfo.Describe("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36"));

    [Fact]
    public void The_alert_email_html_escapes_user_supplied_values()
    {
        var user = new Erp.Api.Domain.User { Email = "a@x.com", FullName = "<script>x</script>" };
        var m = LoginAlertService.NewDeviceEmail(user, "Chrome on Windows", "203.0.113.7", new DateTime(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc));
        m.HtmlBody.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
        m.TextBody.Should().Contain("203.0.113.7").And.Contain("09:30 UTC");
    }
}

public class EmailOptionsTests
{
    private static EmailOptions From(params (string Key, string Value)[] values) =>
        EmailOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void Flat_env_vars_configure_gmail_and_the_sender_defaults_to_the_username()
    {
        var o = From(("SMTP_HOST", "smtp.gmail.com"), ("SMTP_USERNAME", "me@gmail.com"), ("SMTP_PASSWORD", "app-pass"));
        o.IsConfigured.Should().BeTrue();
        o.Port.Should().Be(587);
        o.FromAddress.Should().Be("me@gmail.com");
    }

    [Fact]
    public void A_username_without_a_password_only_logs() =>
        From(("Email:Host", "smtp.gmail.com"), ("Email:Username", "me@gmail.com")).IsConfigured.Should().BeFalse();

    [Fact]
    public void Nothing_set_only_logs() => From().IsConfigured.Should().BeFalse();
}
