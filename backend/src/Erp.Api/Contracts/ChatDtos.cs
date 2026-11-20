using System.ComponentModel.DataAnnotations;

namespace Erp.Api.Contracts;

// ---------- Keys ----------

/// <summary>The caller's own key: public key plus the passphrase-encrypted private key (opaque to the server).</summary>
public record ChatKeyBundleDto(
    Guid KeyId, string PublicKey, string Fingerprint, string EncryptedPrivateKey, string KdfSalt, int KdfIterations,
    DateTime CreatedAt);

public record RegisterChatKeyRequest(
    [Required, MaxLength(256)] string PublicKey,
    [Required, MaxLength(1024)] string EncryptedPrivateKey,
    [Required, MaxLength(64)] string KdfSalt,
    int KdfIterations);

/// <summary>New passphrase: the same private key, re-encrypted on the client.</summary>
public record UpdateChatKeyBackupRequest(
    [Required, MaxLength(1024)] string EncryptedPrivateKey,
    [Required, MaxLength(64)] string KdfSalt,
    int KdfIterations);

/// <summary>Another user's current public key. KeyId null = they have not set up secure messaging yet.</summary>
public record ChatContactDto(Guid UserId, string FullName, string Email, string Role, Guid? KeyId, string? PublicKey, string? Fingerprint);

// ---------- Conversations ----------

public record ChatMemberDto(Guid UserId, string FullName, string Role, string? Fingerprint);

public record ChatConversationDto(
    Guid Id, string Kind, string? Title, IReadOnlyList<ChatMemberDto> Members, DateTime LastMessageAt,
    int Unread, long LastReadMessageId, bool CanPost);

public record CreateConversationRequest([Required] List<Guid> MemberIds, [MaxLength(100)] string? Title);

/// <summary>Who a new message must be encrypted for (every member's active key, the sender's included).</summary>
public record ChatRecipientsDto(IReadOnlyList<ChatContactDto> Recipients, IReadOnlyList<string> WithoutKey);

// ---------- Messages ----------

public record ChatWrappedKeyDto([Required] Guid RecipientKeyId, [Required, MaxLength(256)] string WrappedKey);

public record SendChatMessageRequest(
    [Required] Guid SenderKeyId,
    [Required, MaxLength(65_536)] string Ciphertext,
    [Required, MaxLength(32)] string Iv,
    [Required] List<ChatWrappedKeyDto> Keys);

/// <summary>
/// A message as stored. WrappedKey is the message key wrapped for the caller's current key, or null if the
/// message was sent before the caller's key existed (after a key reset it can no longer be decrypted).
/// </summary>
public record ChatMessageDto(
    long Id, Guid ConversationId, Guid? SenderId, string SenderName, Guid SenderKeyId, string SenderPublicKey,
    string Ciphertext, string Iv, string? WrappedKey, DateTime CreatedAt);

public record MarkReadRequest(long MessageId);
