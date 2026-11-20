using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;

namespace Erp.Api.Services;

/// <summary>
/// A notice that something changed for a user. It never carries message content (which is encrypted anyway):
/// clients fetch the new data with their normal, authorized API calls.
/// Types: "message" (new message in ConversationId), "conversation" (added to a new conversation).
/// </summary>
public sealed record ChatEvent(string Type, Guid? ConversationId = null, long? MessageId = null);

public interface IChatEventBus
{
    Task PublishAsync(IReadOnlyCollection<Guid> userIds, ChatEvent evt);
    /// <summary>Events for one user on this API instance, until the subscription is disposed.</summary>
    ChatSubscription Subscribe(Guid userId);
}

public sealed class ChatSubscription(ChannelReader<ChatEvent> reader, Action onDispose) : IDisposable
{
    private int _disposed;
    public ChannelReader<ChatEvent> Reader { get; } = reader;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
    }
}

/// <summary>
/// Delivers chat events to the users' open event streams (GET /api/chat/stream).
/// The API runs as several replicas behind a round-robin balancer, so with Redis configured every event is
/// published on one Redis channel and each instance delivers it to the streams it holds. Without Redis
/// (tests, single-instance dev) delivery is in-process. Delivery is best effort: clients re-sync on reconnect.
/// </summary>
public sealed class ChatEventBus(IConfiguration cfg, ILogger<ChatEventBus> log) : IChatEventBus, IHostedService, IAsyncDisposable
{
    private static readonly RedisChannel Channel = RedisChannel.Literal("erp:chat:events");
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ChatEvent>>> _local = new();
    private ConnectionMultiplexer? _redis;
    private ISubscriber? _sub;

    private sealed record Envelope(Guid[] U, ChatEvent E);

    public async Task StartAsync(CancellationToken ct)
    {
        var conn = cfg.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(conn)) return;
        try
        {
            var options = ConfigurationOptions.Parse(conn);
            options.AbortOnConnectFail = false;
            _redis = await ConnectionMultiplexer.ConnectAsync(options);
            _sub = _redis.GetSubscriber();
            await _sub.SubscribeAsync(Channel, (_, value) =>
            {
                try
                {
                    var env = JsonSerializer.Deserialize<Envelope>(value.ToString());
                    if (env is not null) DeliverLocal(env.U, env.E);
                }
                catch (JsonException e) { log.LogWarning(e, "Ignoring malformed chat event"); }
            });
        }
        catch (Exception e)
        {
            // Chat still works on this instance; other replicas' users get updates on their next re-sync.
            log.LogWarning(e, "Chat events: Redis pub/sub unavailable, delivering in-process only");
            _sub = null;
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null) await _redis.DisposeAsync();
    }

    public async Task PublishAsync(IReadOnlyCollection<Guid> userIds, ChatEvent evt)
    {
        if (userIds.Count == 0) return;
        if (_sub is not null)
        {
            try
            {
                await _sub.PublishAsync(Channel, JsonSerializer.Serialize(new Envelope(userIds.ToArray(), evt)));
                return;
            }
            catch (Exception e) { log.LogWarning(e, "Chat events: Redis publish failed, delivering in-process"); }
        }
        DeliverLocal(userIds, evt);
    }

    public ChatSubscription Subscribe(Guid userId)
    {
        // Bounded: a stalled client loses old notices rather than growing memory (it re-syncs on reconnect).
        var channel = System.Threading.Channels.Channel.CreateBounded<ChatEvent>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _local.GetOrAdd(userId, _ => new()).TryAdd(id, channel);
        return new ChatSubscription(channel.Reader, () =>
        {
            if (_local.TryGetValue(userId, out var subs))
            {
                subs.TryRemove(id, out _);
                if (subs.IsEmpty) _local.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<Guid, Channel<ChatEvent>>>(userId, subs));
            }
            channel.Writer.TryComplete();
        });
    }

    internal int LocalSubscriberCount(Guid userId) => _local.TryGetValue(userId, out var s) ? s.Count : 0;

    private void DeliverLocal(IEnumerable<Guid> userIds, ChatEvent evt)
    {
        foreach (var u in userIds)
            if (_local.TryGetValue(u, out var subs))
                foreach (var ch in subs.Values) ch.Writer.TryWrite(evt);
    }
}
