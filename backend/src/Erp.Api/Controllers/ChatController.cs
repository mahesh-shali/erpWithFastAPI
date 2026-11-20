using System.Text.Json;
using Erp.Api.Contracts;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Api.Controllers;

/// <summary>
/// End-to-end encrypted messaging (page "messages"). The server stores and relays ciphertext only;
/// ChatService enforces who can talk to whom (members among themselves, SuperAdmin announcements only).
/// </summary>
[ApiController]
[Route("api/chat")]
[RequirePage(PageKeys.Messages, PageAction.Read)]
public class ChatController(IChatService chat, IChatEventBus events, ITenantContext tenant) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
    private const int MaxStreamsPerUser = 10;

    // ---------- keys ----------

    [HttpGet("keys/me")]
    public async Task<ActionResult<ChatKeyBundleDto>> MyKey(CancellationToken ct) =>
        await chat.GetMyKeyAsync(ct) is { } key ? key : NoContent();

    [HttpPost("keys")]
    public Task<ChatKeyBundleDto> Register(RegisterChatKeyRequest req, CancellationToken ct) =>
        chat.RegisterKeyAsync(req, replace: false, ct);

    /// <summary>Forgotten passphrase: a new key. Messages sent to the old key can no longer be read.</summary>
    [HttpPost("keys/reset")]
    public Task<ChatKeyBundleDto> Reset(RegisterChatKeyRequest req, CancellationToken ct) =>
        chat.RegisterKeyAsync(req, replace: true, ct);

    [HttpPut("keys/me/backup")]
    public Task<ChatKeyBundleDto> UpdateBackup(UpdateChatKeyBackupRequest req, CancellationToken ct) =>
        chat.UpdateBackupAsync(req, ct);

    // ---------- people & conversations ----------

    [HttpGet("contacts")]
    public Task<IReadOnlyList<ChatContactDto>> Contacts(CancellationToken ct) => chat.ContactsAsync(ct);

    [HttpGet("conversations")]
    public Task<IReadOnlyList<ChatConversationDto>> Conversations(CancellationToken ct) => chat.ConversationsAsync(ct);

    [HttpPost("conversations"), RequirePage(PageKeys.Messages, PageAction.Write)]
    public Task<ChatConversationDto> Create(CreateConversationRequest req, CancellationToken ct) =>
        chat.CreateConversationAsync(req, ct);

    [HttpGet("conversations/{id:guid}/recipients"), RequirePage(PageKeys.Messages, PageAction.Write)]
    public Task<ChatRecipientsDto> Recipients(Guid id, CancellationToken ct) => chat.RecipientsAsync(id, ct);

    // ---------- messages ----------

    [HttpGet("conversations/{id:guid}/messages")]
    public Task<IReadOnlyList<ChatMessageDto>> Messages(Guid id, long? before, long? after, int limit = 50, CancellationToken ct = default) =>
        chat.MessagesAsync(id, before, after, limit, ct);

    [HttpPost("conversations/{id:guid}/messages"), RequirePage(PageKeys.Messages, PageAction.Write)]
    public Task<ChatMessageDto> Send(Guid id, SendChatMessageRequest req, CancellationToken ct) => chat.SendAsync(id, req, ct);

    [HttpPost("conversations/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, MarkReadRequest req, CancellationToken ct)
    {
        await chat.MarkReadAsync(id, req.MessageId, ct);
        return NoContent();
    }

    // ---------- live updates ----------

    /// <summary>
    /// Server-Sent Events: "chat" events say which conversation changed (never content), ": ping" comments keep
    /// proxies from timing out. The stream ends when the access token expires, so a revoked session stops
    /// receiving events; clients reconnect with a fresh token and re-sync.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        if (events is ChatEventBus bus && bus.LocalSubscriberCount(tenant.UserId) >= MaxStreamsPerUser)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no"; // nginx: don't buffer this response
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(UntilTokenExpires());
        using var sub = events.Subscribe(tenant.UserId);

        try
        {
            await WriteAsync("retry: 3000\nevent: ready\ndata: {}\n\n", lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                tick.CancelAfter(Heartbeat);
                try
                {
                    if (!await sub.Reader.WaitToReadAsync(tick.Token)) break;
                    while (sub.Reader.TryRead(out var evt))
                        await WriteAsync($"event: chat\ndata: {JsonSerializer.Serialize(evt, Json)}\n\n", lifetime.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    await WriteAsync(": ping\n\n", lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { /* client left or token expired */ }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await Response.WriteAsync(text, ct);
        await Response.Body.FlushAsync(ct);
    }

    private TimeSpan UntilTokenExpires()
    {
        var left = long.TryParse(User.FindFirst("exp")?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp) - DateTimeOffset.UtcNow
            : TimeSpan.FromMinutes(5);
        return left < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : left > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : left;
    }
}
