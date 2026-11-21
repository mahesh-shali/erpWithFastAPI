namespace Erp.Api.Domain;

// ------------------------------------------------------------------------------------------------
// End-to-end encrypted chat.
//
// The server never sees plaintext or a usable private key. It stores:
//  - each user's ECDH P-256 public key, and their private key encrypted on the client with a key
//    derived from a passphrase only the user knows (so the same identity works on web and desktop);
//  - per message: AES-256-GCM ciphertext, plus the message key wrapped separately for every
//    recipient's public key (ECDH -> HKDF -> AES-GCM). See frontend/src/chat/crypto.ts.
//
// Access rules (ChatService): everyone in the organization, the SuperAdmin (owner) included, talks in
// direct and group conversations. Starting a conversation with someone in a more senior role (higher
// Role.Level) needs the "messages-senior" right; seniors can always reach juniors, and anyone can reply in a
// conversation they are already a member of. Every organization also has two org-wide channels: General
// (everyone posts) and Announcements (only the owner posts, everyone reads).
// ------------------------------------------------------------------------------------------------

public static class ChatKinds
{
    public const string Direct = "direct";
    public const string Group = "group";
    /// <summary>One per organization: every member posts and reads.</summary>
    public const string General = "general";
    /// <summary>One per organization: the SuperAdmin posts, everyone else reads.</summary>
    public const string Announcement = "announcement";

    /// <summary>Org-wide channels have no stored membership: every user of the organization is in them.</summary>
    public static bool IsOrgWide(string kind) => kind is General or Announcement;
}

public static class ChatLimits
{
    /// <summary>~48 KB of ciphertext as base64; the UI caps a message at 4000 characters.</summary>
    public const int MaxCiphertextChars = 65_536;
    public const int MaxGroupMembers = 200;
    public const int MaxTitleLength = 100;
    public const int PageSize = 50;
    /// <summary>PBKDF2 iterations accepted for the passphrase-protected key backup.</summary>
    public const int MinKdfIterations = 100_000;
    public const int MaxKdfIterations = 5_000_000;
}

/// <summary>A user's chat identity key. One active (RevokedAt null) per user; older ones stay for history.</summary>
public class ChatUserKey : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>Null after the user is deleted: the key row stays so their old messages keep a sender key.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Base64 SPKI of the ECDH P-256 public key.</summary>
    public string PublicKey { get; set; } = default!;
    /// <summary>SHA-256 of the SPKI, hex. Shown to users so they can verify keys out of band.</summary>
    public string Fingerprint { get; set; } = default!;
    /// <summary>Base64 (12-byte IV || AES-GCM ciphertext) of the PKCS#8 private key. Opaque to the server.</summary>
    public string EncryptedPrivateKey { get; set; } = default!;
    public string KdfSalt { get; set; } = default!;
    public int KdfIterations { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

public class ChatConversation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = ChatKinds.Direct;
    public string? Title { get; set; }
    /// <summary>"{smallerUserId}:{largerUserId}" for direct chats, so a pair has only one conversation.</summary>
    public string? DirectKey { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    public List<ChatParticipant> Participants { get; set; } = [];
}

/// <summary>
/// Membership + read position. Org-wide channels (General, Announcements) have no stored membership (every org user is in it); a row
/// is added there only to remember the user's read position.
/// </summary>
public class ChatParticipant : ITenantOwned
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public long LastReadMessageId { get; set; }
    public User? User { get; set; }
}

public class ChatMessage : ITenantOwned
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? SenderId { get; set; }
    public Guid SenderKeyId { get; set; }
    /// <summary>Base64 AES-256-GCM ciphertext of the message payload.</summary>
    public string Ciphertext { get; set; } = default!;
    /// <summary>Base64 12-byte IV.</summary>
    public string Iv { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ChatUserKey? SenderKey { get; set; }
    public User? Sender { get; set; }
    public List<ChatMessageKey> Keys { get; set; } = [];
}

/// <summary>The message key wrapped for one recipient key.</summary>
public class ChatMessageKey : ITenantOwned
{
    public long MessageId { get; set; }
    public Guid RecipientKeyId { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>Base64 (12-byte IV || AES-GCM ciphertext of the 32-byte message key).</summary>
    public string WrappedKey { get; set; } = default!;
}
