using System.Security.Cryptography;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>
/// End-to-end encrypted chat. The server only relays and stores ciphertext; it enforces WHO may talk to whom:
///
///  - Everything is inside the caller's organization (EF tenant filter + explicit org checks on users).
///  - Everyone in the organization (the SuperAdmin included) creates direct and group conversations.
///  - Seniority: starting a conversation with anyone whose role level is higher than the caller's needs Write on
///    the "messages-senior" right (PageKeys.MessagesSenior). Same or lower levels are always allowed. Once a
///    conversation exists its members may post in it, so a junior can reply to a senior who wrote first.
///  - Each organization has two org-wide channels with every user as a member: General (everyone posts) and
///    Announcements (only the SuperAdmin posts, everyone reads).
///  - A message must be encrypted for exactly the current keys of the conversation's members (409 otherwise),
///    so nobody is silently left out and no extra key can be slipped in.
/// </summary>
public interface IChatService
{
    Task<ChatKeyBundleDto?> GetMyKeyAsync(CancellationToken ct);
    /// <summary>First set-up, or (replace = true) a reset after a forgotten passphrase: old messages become unreadable.</summary>
    Task<ChatKeyBundleDto> RegisterKeyAsync(RegisterChatKeyRequest req, bool replace, CancellationToken ct);
    /// <summary>New passphrase for the same key.</summary>
    Task<ChatKeyBundleDto> UpdateBackupAsync(UpdateChatKeyBackupRequest req, CancellationToken ct);
    Task<IReadOnlyList<ChatContactDto>> ContactsAsync(CancellationToken ct);
    Task<IReadOnlyList<ChatConversationDto>> ConversationsAsync(CancellationToken ct);
    Task<ChatConversationDto> CreateConversationAsync(CreateConversationRequest req, CancellationToken ct);
    Task<ChatRecipientsDto> RecipientsAsync(Guid conversationId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessageDto>> MessagesAsync(Guid conversationId, long? before, long? after, int limit, CancellationToken ct);
    Task<ChatMessageDto> SendAsync(Guid conversationId, SendChatMessageRequest req, CancellationToken ct);
    Task MarkReadAsync(Guid conversationId, long messageId, CancellationToken ct);
}

public sealed class ChatService(
    ErpDbContext db, ITenantContext tenant, IChatEventBus events, IPermissionService permissions, TimeProvider clock) : IChatService
{
    public const string AnnouncementTitle = "Announcements";
    public const string GeneralTitle = "General";
    private const int SpkiP256Length = 91;     // WebCrypto exportKey('spki') of an ECDH P-256 key
    private const int IvLength = 12;
    private const int WrappedKeyLength = IvLength + 32 + 16; // IV + AES-GCM(32-byte key) + tag

    private Guid Org => tenant.OrganizationId;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record Actor(Guid Id, string FullName, int RoleId, int Level)
    {
        /// <summary>The organization owner: the only one who posts announcements.</summary>
        public bool IsOwner => RoleId == Roles.SuperAdminId;
    }

    // ---------------- Keys ----------------

    public async Task<ChatKeyBundleDto?> GetMyKeyAsync(CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var key = await db.ChatUserKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == me.Id && k.RevokedAt == null, ct);
        return key is null ? null : ToBundle(key);
    }

    public async Task<ChatKeyBundleDto> RegisterKeyAsync(RegisterChatKeyRequest req, bool replace, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var spki = Base64(req.PublicKey, "publicKey", SpkiP256Length, SpkiP256Length);
        ValidateBackup(req.EncryptedPrivateKey, req.KdfSalt, req.KdfIterations);

        var hasKey = await db.ChatUserKeys.AnyAsync(k => k.UserId == me.Id && k.RevokedAt == null, ct);
        if (hasKey && !replace)
            throw new ConflictException("Secure messaging is already set up for your account. Unlock it with your passphrase.");
        if (hasKey) // revoke first: at most one active key per user (partial unique index)
            await db.ChatUserKeys.Where(k => k.UserId == me.Id && k.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, Now), ct);

        var key = new ChatUserKey
        {
            Id = Guid.NewGuid(), UserId = me.Id, PublicKey = req.PublicKey,
            Fingerprint = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            EncryptedPrivateKey = req.EncryptedPrivateKey, KdfSalt = req.KdfSalt, KdfIterations = req.KdfIterations,
            CreatedAt = Now, UpdatedAt = Now,
        };
        db.ChatUserKeys.Add(key);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("Secure messaging was just set up from another device. Unlock it with your passphrase.");
        }
        return ToBundle(key);
    }

    public async Task<ChatKeyBundleDto> UpdateBackupAsync(UpdateChatKeyBackupRequest req, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        ValidateBackup(req.EncryptedPrivateKey, req.KdfSalt, req.KdfIterations);
        var key = await db.ChatUserKeys.FirstOrDefaultAsync(k => k.UserId == me.Id && k.RevokedAt == null, ct)
                  ?? throw new NotFoundException("Secure messaging is not set up yet.");
        key.EncryptedPrivateKey = req.EncryptedPrivateKey;
        key.KdfSalt = req.KdfSalt;
        key.KdfIterations = req.KdfIterations;
        key.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return ToBundle(key);
    }

    // ---------------- People & conversations ----------------

    public async Task<IReadOnlyList<ChatContactDto>> ContactsAsync(CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        // People the caller may start a conversation with: more senior roles only with the right.
        var maxLevel = await MaxReachableLevelAsync(me, ct);
        return await ContactsQuery(db.Users.Where(u => u.OrganizationId == Org && u.Id != me.Id && u.Role!.Level <= maxLevel))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatConversationDto>> ConversationsAsync(CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        await EnsureOrgChannelsAsync(ct);
        return await BuildAsync(me, null, ct);
    }

    public async Task<ChatConversationDto> CreateConversationAsync(CreateConversationRequest req, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var ids = (req.MemberIds ?? []).Distinct().Where(id => id != me.Id).ToList();
        if (ids.Count == 0) throw new DomainException("Choose at least one person.");
        if (ids.Count + 1 > ChatLimits.MaxGroupMembers)
            throw new DomainException($"A group can have at most {ChatLimits.MaxGroupMembers} members.");

        var members = await db.Users.AsNoTracking().Where(u => u.OrganizationId == Org && ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Role!.Level }).ToListAsync(ct);
        if (members.Count != ids.Count) throw new NotFoundException("Some of the selected people were not found.");

        string? directKey = null, title = null;
        var kind = ids.Count == 1 ? ChatKinds.Direct : ChatKinds.Group;
        if (kind == ChatKinds.Direct)
        {
            // An existing direct chat is always reopened, e.g. a junior answering a senior who wrote first.
            directKey = DirectKeyOf(me.Id, ids[0]);
            var existing = await db.ChatConversations.Where(c => c.DirectKey == directKey).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (existing is { } id) return (await BuildAsync(me, id, ct)).Single();
        }

        var maxLevel = await MaxReachableLevelAsync(me, ct);
        if (members.Any(m => m.Level > maxLevel))
            throw new ForbiddenException("You don't have permission to start conversations with people in more senior roles.");

        if (kind == ChatKinds.Group)
        {
            title = req.Title?.Trim();
            if (string.IsNullOrEmpty(title)) throw new DomainException("Give the group a name.");
            if (title.Length > ChatLimits.MaxTitleLength) throw new DomainException("The group name is too long.");
        }

        var conv = new ChatConversation
        {
            Id = Guid.NewGuid(), Kind = kind, Title = title, DirectKey = directKey, CreatedByUserId = me.Id,
            CreatedAt = Now, LastMessageAt = Now,
            Participants = ids.Append(me.Id).Select(u => new ChatParticipant { UserId = u, JoinedAt = Now }).ToList(),
        };
        db.ChatConversations.Add(conv);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (directKey is not null)
        {
            // Both people opened the chat at the same moment: use the one that won.
            db.ChangeTracker.Clear();
            var id = await db.ChatConversations.Where(c => c.DirectKey == directKey).Select(c => c.Id).FirstAsync(ct);
            return (await BuildAsync(me, id, ct)).Single();
        }

        await events.PublishAsync(ids, new ChatEvent("conversation", conv.Id));
        return (await BuildAsync(me, conv.Id, ct)).Single();
    }

    public async Task<ChatRecipientsDto> RecipientsAsync(Guid conversationId, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var conv = await AccessibleAsync(me, conversationId, ct);
        EnsureCanPost(me, conv);
        var people = await ContactsQuery(db.Users.Where(u => MemberIds(conv).Contains(u.Id))).ToListAsync(ct);
        return new ChatRecipientsDto(
            people.Where(p => p.KeyId is not null).ToList(),
            people.Where(p => p.KeyId is null).Select(p => p.FullName).ToList());
    }

    // ---------------- Messages ----------------

    public async Task<IReadOnlyList<ChatMessageDto>> MessagesAsync(Guid conversationId, long? before, long? after, int limit, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        await AccessibleAsync(me, conversationId, ct);
        var myKeyId = await ActiveKeyIdAsync(me.Id, ct);
        limit = Math.Clamp(limit <= 0 ? ChatLimits.PageSize : limit, 1, 100);

        var q = db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (after is { } a) q = q.Where(m => m.Id > a).OrderBy(m => m.Id);
        else q = (before is { } b ? q.Where(m => m.Id < b) : q).OrderByDescending(m => m.Id);

        var rows = await q.Take(limit).Select(m => new ChatMessageDto(
            m.Id, m.ConversationId, m.SenderId, m.Sender != null ? m.Sender.FullName : "Deleted user",
            m.SenderKeyId, m.SenderKey!.PublicKey, m.Ciphertext, m.Iv,
            m.Keys.Where(k => k.RecipientKeyId == myKeyId).Select(k => k.WrappedKey).FirstOrDefault(),
            m.CreatedAt)).ToListAsync(ct);
        return rows.OrderBy(m => m.Id).ToList();
    }

    public async Task<ChatMessageDto> SendAsync(Guid conversationId, SendChatMessageRequest req, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var conv = await AccessibleAsync(me, conversationId, ct);
        EnsureCanPost(me, conv);

        Base64(req.Iv, "iv", IvLength, IvLength);
        Base64(req.Ciphertext, "ciphertext", 17, ChatLimits.MaxCiphertextChars / 4 * 3);
        if (req.Keys is null || req.Keys.Count == 0) throw new DomainException("The message has no recipients.");
        foreach (var k in req.Keys) Base64(k.WrappedKey, "wrappedKey", WrappedKeyLength, WrappedKeyLength);

        var myKey = await db.ChatUserKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == me.Id && k.RevokedAt == null, ct);
        if (myKey is null || myKey.Id != req.SenderKeyId)
            throw new ConflictException("Your secure messaging key has changed. Unlock it again and resend.");

        // The message must be readable by exactly the members' current keys: no one left out, nothing extra.
        var expected = await db.ChatUserKeys.AsNoTracking()
            .Where(k => k.RevokedAt == null && k.UserId != null && MemberIds(conv).Contains(k.UserId.Value))
            .Select(k => k.Id).ToListAsync(ct);
        var provided = req.Keys.Select(k => k.RecipientKeyId).ToList();
        if (provided.Count != provided.Distinct().Count() || !new HashSet<Guid>(provided).SetEquals(expected))
            throw new ConflictException("The members of this conversation or their keys have changed. Refresh and try again.");

        var msg = new ChatMessage
        {
            ConversationId = conv.Id, SenderId = me.Id, SenderKeyId = myKey.Id,
            Ciphertext = req.Ciphertext, Iv = req.Iv, CreatedAt = Now,
            Keys = req.Keys.Select(k => new ChatMessageKey { RecipientKeyId = k.RecipientKeyId, WrappedKey = k.WrappedKey }).ToList(),
        };
        db.ChatMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        await db.ChatConversations.Where(c => c.Id == conv.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageAt, msg.CreatedAt), ct);
        await MarkReadAsync(me, conv, msg.Id, ct);

        var notify = await MemberIds(conv).ToListAsync(ct);
        await events.PublishAsync(notify, new ChatEvent("message", conv.Id, msg.Id));

        return new ChatMessageDto(msg.Id, conv.Id, me.Id, me.FullName, myKey.Id, myKey.PublicKey, msg.Ciphertext, msg.Iv,
            req.Keys.First(k => k.RecipientKeyId == myKey.Id).WrappedKey, msg.CreatedAt);
    }

    public async Task MarkReadAsync(Guid conversationId, long messageId, CancellationToken ct)
    {
        var me = await ActorAsync(ct);
        var conv = await AccessibleAsync(me, conversationId, ct);
        await MarkReadAsync(me, conv, messageId, ct);
    }

    // ---------------- internals ----------------

    private async Task MarkReadAsync(Actor me, ChatConversation conv, long messageId, CancellationToken ct)
    {
        var p = await db.ChatParticipants.FirstOrDefaultAsync(x => x.ConversationId == conv.Id && x.UserId == me.Id, ct);
        if (p is null)
        {
            // Org-wide channels keep no membership rows; this row only remembers the read position.
            if (!ChatKinds.IsOrgWide(conv.Kind)) throw new NotFoundException("Conversation not found.");
            db.ChatParticipants.Add(new ChatParticipant { ConversationId = conv.Id, UserId = me.Id, JoinedAt = Now, LastReadMessageId = messageId });
        }
        else if (messageId > p.LastReadMessageId) p.LastReadMessageId = messageId;
        else return;

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // concurrent first read from another device
    }

    private async Task<Actor> ActorAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == tenant.UserId && u.OrganizationId == Org)
            .Select(u => new Actor(u.Id, u.FullName, u.RoleId, u.Role!.Level))
            .FirstOrDefaultAsync(ct)
        ?? throw new ForbiddenException("Your account was not found.");

    /// <summary>
    /// The conversation if the caller may see it. Hidden conversations answer 404, so their existence isn't revealed.
    /// </summary>
    private async Task<ChatConversation> AccessibleAsync(Actor me, Guid id, CancellationToken ct)
    {
        var conv = await db.ChatConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
                   ?? throw new NotFoundException("Conversation not found.");
        if (ChatKinds.IsOrgWide(conv.Kind)) return conv;
        if (!await db.ChatParticipants.AnyAsync(p => p.ConversationId == id && p.UserId == me.Id, ct))
            throw new NotFoundException("Conversation not found.");
        return conv;
    }

    private static bool CanPost(Actor me, string kind) => kind != ChatKinds.Announcement || me.IsOwner;

    /// <summary>
    /// Highest role level the caller may start a conversation with: any level with the "messages-senior" right
    /// (the owner has every right), otherwise their own level and below.
    /// </summary>
    private async Task<int> MaxReachableLevelAsync(Actor me, CancellationToken ct) =>
        me.IsOwner || await permissions.HasAsync(me.Id, Org, PageKeys.MessagesSenior, PageAction.Write, ct) ? int.MaxValue : me.Level;

    private static void EnsureCanPost(Actor me, ChatConversation conv)
    {
        if (!CanPost(me, conv.Kind))
            throw new ForbiddenException(conv.Kind == ChatKinds.Announcement
                ? "Only the organization owner can post announcements."
                : "You can't post in this conversation.");
    }

    /// <summary>Everyone who receives the conversation's messages. Org-wide channels: every user of the organization.</summary>
    private IQueryable<Guid> MemberIds(ChatConversation conv) =>
        ChatKinds.IsOrgWide(conv.Kind)
            ? db.Users.Where(u => u.OrganizationId == Org).Select(u => u.Id)
            : db.ChatParticipants.Where(p => p.ConversationId == conv.Id).Select(p => p.UserId);

    private IQueryable<ChatContactDto> ContactsQuery(IQueryable<User> users) =>
        from u in users.AsNoTracking()
        join k in db.ChatUserKeys.Where(k => k.RevokedAt == null) on (Guid?)u.Id equals k.UserId into keys
        from k in keys.DefaultIfEmpty()
        orderby u.FullName
        select new ChatContactDto(u.Id, u.FullName, u.Email, u.Role!.Name,
            k == null ? null : k.Id, k == null ? null : k.PublicKey, k == null ? null : k.Fingerprint);

    private Task<Guid?> ActiveKeyIdAsync(Guid userId, CancellationToken ct) =>
        db.ChatUserKeys.Where(k => k.UserId == userId && k.RevokedAt == null).Select(k => (Guid?)k.Id).FirstOrDefaultAsync(ct);

    /// <summary>Creates the organization's General and Announcements channels on first use.</summary>
    private async Task EnsureOrgChannelsAsync(CancellationToken ct)
    {
        var existing = await db.ChatConversations.Where(c => c.Kind == ChatKinds.General || c.Kind == ChatKinds.Announcement)
            .Select(c => c.Kind).ToListAsync(ct);
        foreach (var (kind, title) in new[] { (ChatKinds.General, GeneralTitle), (ChatKinds.Announcement, AnnouncementTitle) })
        {
            if (existing.Contains(kind)) continue;
            db.ChatConversations.Add(new ChatConversation { Id = Guid.NewGuid(), Kind = kind, Title = title, CreatedAt = Now, LastMessageAt = Now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // created concurrently (unique per organization)
        }
    }

    private static string? TitleOf(string kind, string? title) => kind switch
    {
        ChatKinds.Announcement => AnnouncementTitle,
        ChatKinds.General => GeneralTitle,
        _ => title,
    };

    /// <summary>Conversation list for the caller (or the one conversation `onlyId`), newest activity first.</summary>
    private async Task<List<ChatConversationDto>> BuildAsync(Actor me, Guid? onlyId, CancellationToken ct)
    {
        var q = db.ChatConversations.AsNoTracking()
            .Where(c => c.Kind == ChatKinds.General || c.Kind == ChatKinds.Announcement || c.Participants.Any(p => p.UserId == me.Id));
        if (onlyId is { } only) q = q.Where(c => c.Id == only);

        var rows = await q
            .Select(c => new
            {
                c.Id, c.Kind, c.Title, c.LastMessageAt,
                LastRead = c.Participants.Where(p => p.UserId == me.Id).Select(p => (long?)p.LastReadMessageId).FirstOrDefault() ?? 0,
            })
            .Select(c => new
            {
                c.Id, c.Kind, c.Title, c.LastMessageAt, c.LastRead,
                Unread = db.ChatMessages.Count(m => m.ConversationId == c.Id && m.Id > c.LastRead && m.SenderId != me.Id),
            })
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);

        var memberConvIds = rows.Where(r => !ChatKinds.IsOrgWide(r.Kind)).Select(r => r.Id).ToList();
        var members = await (
                from p in db.ChatParticipants.AsNoTracking().Where(p => memberConvIds.Contains(p.ConversationId))
                join k in db.ChatUserKeys.Where(k => k.RevokedAt == null) on (Guid?)p.UserId equals k.UserId into keys
                from k in keys.DefaultIfEmpty()
                orderby p.User!.FullName
                select new { p.ConversationId, Member = new ChatMemberDto(p.UserId, p.User!.FullName, p.User.Role!.Name, k == null ? null : k.Fingerprint) })
            .ToListAsync(ct);
        var byConv = members.ToLookup(m => m.ConversationId, m => m.Member);

        return rows.Select(r => new ChatConversationDto(
            r.Id, r.Kind, TitleOf(r.Kind, r.Title),
            byConv[r.Id].ToList(), r.LastMessageAt, r.Unread, r.LastRead, CanPost(me, r.Kind))).ToList();
    }

    internal static string DirectKeyOf(Guid a, Guid b)
    {
        var (x, y) = (a.ToString("N"), b.ToString("N"));
        return string.CompareOrdinal(x, y) < 0 ? $"{x}:{y}" : $"{y}:{x}";
    }

    private static void ValidateBackup(string encryptedPrivateKey, string salt, int iterations)
    {
        Base64(encryptedPrivateKey, "encryptedPrivateKey", IvLength + 16 + 64, 700);
        Base64(salt, "kdfSalt", 16, 32);
        if (iterations is < ChatLimits.MinKdfIterations or > ChatLimits.MaxKdfIterations)
            throw new DomainException($"kdfIterations must be between {ChatLimits.MinKdfIterations} and {ChatLimits.MaxKdfIterations}.");
    }

    /// <summary>Decodes base64 and checks the decoded length; the server never interprets the bytes otherwise.</summary>
    internal static byte[] Base64(string? value, string field, int minBytes, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > (maxBytes + 2) / 3 * 4 + 4)
            throw new DomainException($"{field} is missing or too large.");
        var buffer = new byte[value.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(value, buffer, out var n) || n < minBytes || n > maxBytes)
            throw new DomainException($"{field} is not valid.");
        return buffer[..n];
    }

    private static ChatKeyBundleDto ToBundle(ChatUserKey k) =>
        new(k.Id, k.PublicKey, k.Fingerprint, k.EncryptedPrivateKey, k.KdfSalt, k.KdfIterations, k.CreatedAt);
}
