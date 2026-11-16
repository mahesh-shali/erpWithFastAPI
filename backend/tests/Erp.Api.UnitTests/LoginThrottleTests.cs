using Erp.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Erp.Api.UnitTests;

public class LoginThrottleTests
{
    private static LoginThrottle Create(int threshold = 3, int minutes = 15) => new(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:LockoutThreshold"] = threshold.ToString(), ["Auth:LockoutMinutes"] = minutes.ToString(),
        }).Build());

    [Fact]
    public async Task Locks_out_after_threshold_failures()
    {
        var t = Create(threshold: 3);
        for (var i = 0; i < 2; i++) await t.RecordFailureAsync("a@x.com", default);
        (await t.IsLockedOutAsync("a@x.com", default)).Should().BeFalse();

        await t.RecordFailureAsync("a@x.com", default);
        (await t.IsLockedOutAsync("a@x.com", default)).Should().BeTrue();
    }

    [Fact]
    public async Task Email_key_ignores_case_and_spaces()
    {
        var t = Create(threshold: 2);
        await t.RecordFailureAsync("Admin@Demo.com", default);
        await t.RecordFailureAsync("  admin@demo.com ", default);
        (await t.IsLockedOutAsync("ADMIN@DEMO.COM", default)).Should().BeTrue();
    }

    [Fact]
    public async Task Other_accounts_are_not_affected()
    {
        var t = Create(threshold: 1);
        await t.RecordFailureAsync("a@x.com", default);
        (await t.IsLockedOutAsync("b@x.com", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Successful_login_reset_clears_the_counter()
    {
        var t = Create(threshold: 2);
        await t.RecordFailureAsync("a@x.com", default);
        await t.RecordFailureAsync("a@x.com", default);
        await t.ResetAsync("a@x.com", default);
        (await t.IsLockedOutAsync("a@x.com", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Threshold_zero_disables_lockout()
    {
        var t = Create(threshold: 0);
        for (var i = 0; i < 20; i++) await t.RecordFailureAsync("a@x.com", default);
        (await t.IsLockedOutAsync("a@x.com", default)).Should().BeFalse();
    }

    [Fact]
    public void Window_comes_from_configuration()
    {
        Create(minutes: 7).Window.Should().Be(TimeSpan.FromMinutes(7));
    }
}
