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
