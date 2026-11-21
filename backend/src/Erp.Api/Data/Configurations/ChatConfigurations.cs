using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Api.Data.Configurations;

// End-to-end encrypted chat (see Domain/Chat.cs). Ciphertext and keys are base64 text; sizes are capped
// here and validated again in ChatService.

public sealed class ChatUserKeyConfiguration : IEntityTypeConfiguration<ChatUserKey>
{
    public void Configure(EntityTypeBuilder<ChatUserKey> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.PublicKey).HasMaxLength(256);
        e.Property(x => x.Fingerprint).HasMaxLength(64);
        e.Property(x => x.EncryptedPrivateKey).HasMaxLength(1024);
        e.Property(x => x.KdfSalt).HasMaxLength(64);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.Property(x => x.UpdatedAt).HasDefaultValueSql(Sql.Now);

        // At most one active key per user
        e.HasIndex(x => x.UserId).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_chat_user_keys_active");
        e.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_chat_user_keys_org");

        e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ChatConversationConfiguration : IEntityTypeConfiguration<ChatConversation>
{
    public void Configure(EntityTypeBuilder<ChatConversation> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Kind).HasMaxLength(20);
        e.Property(x => x.Title).HasMaxLength(100);
        e.Property(x => x.DirectKey).HasMaxLength(80);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.Property(x => x.LastMessageAt).HasDefaultValueSql(Sql.Now);
        e.ToTable(t => t.HasCheckConstraint("ck_chat_conversations_kind", "kind IN ('direct', 'group', 'general', 'announcement')"));

        e.HasIndex(x => new { x.OrganizationId, x.DirectKey }).IsUnique().HasFilter("direct_key IS NOT NULL")
            .HasDatabaseName("ux_chat_conversations_direct");
        e.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("kind = 'announcement'")
            .HasDatabaseName("ux_chat_conversations_announcement");
        // Named: a second unnamed index on organization_id would merge into the one above.
        e.HasIndex(x => x.OrganizationId, "ux_chat_conversations_general").IsUnique().HasFilter("kind = 'general'")
            .HasDatabaseName("ux_chat_conversations_general");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ChatParticipantConfiguration : IEntityTypeConfiguration<ChatParticipant>
{
    public void Configure(EntityTypeBuilder<ChatParticipant> e)
    {
        e.HasKey(x => new { x.ConversationId, x.UserId });
        e.Property(x => x.JoinedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => x.UserId).HasDatabaseName("ix_chat_participants_user");

        e.HasOne<ChatConversation>().WithMany(c => c.Participants).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> e)
    {
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.Ciphertext).HasMaxLength(ChatLimits.MaxCiphertextChars);
        e.Property(x => x.Iv).HasMaxLength(32);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => new { x.ConversationId, x.Id }).HasDatabaseName("ix_chat_messages_conversation");

        e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.SetNull);
        e.HasOne(x => x.SenderKey).WithMany().HasForeignKey(x => x.SenderKeyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ChatMessageKeyConfiguration : IEntityTypeConfiguration<ChatMessageKey>
{
    public void Configure(EntityTypeBuilder<ChatMessageKey> e)
    {
        e.HasKey(x => new { x.MessageId, x.RecipientKeyId });
        e.Property(x => x.WrappedKey).HasMaxLength(256);
        e.HasIndex(x => x.RecipientKeyId).HasDatabaseName("ix_chat_message_keys_recipient");

        e.HasOne<ChatMessage>().WithMany(m => m.Keys).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<ChatUserKey>().WithMany().HasForeignKey(x => x.RecipientKeyId).OnDelete(DeleteBehavior.Cascade);
    }
}
