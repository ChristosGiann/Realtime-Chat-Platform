using Chat.Api.Conversations;
using Chat.Api.Identity;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Api.Infrastructure.Persistence;

public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages", "messages", table =>
        {
            table.HasCheckConstraint("CK_Messages_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("CK_Messages_Body", """
                length(btrim("Body", E' \t\n\r\f' || chr(11) || chr(133) || chr(160) || chr(5760)
                    || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196) || chr(8197)
                    || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202)
                    || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288))) > 0
                """);
            table.HasCheckConstraint("CK_Messages_CreatedAt", "isfinite(\"CreatedAt\")");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Body).HasMaxLength(Message.MaxBodyLength).IsRequired();
        // Insertion time, not transaction-start time before waiting for a conversation lock.
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("clock_timestamp()");
        builder.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ConversationId, x.CreatedAt, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("IX_Messages_ConversationHistory");
    }
}
