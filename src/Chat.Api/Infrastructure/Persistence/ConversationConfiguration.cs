using Chat.Api.Conversations;
using Chat.Api.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chat.Api.Infrastructure.Persistence;

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations", "conversations", table =>
        {
            table.HasCheckConstraint("CK_Conversations_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("CK_Conversations_Type", "\"Type\" IN (1, 2)");
            table.HasCheckConstraint("CK_Conversations_Shape", """
                ("Type" = 1 AND "Name" IS NULL AND "DirectUserLowId" IS NOT NULL
                    AND "DirectUserHighId" IS NOT NULL AND "DirectUserLowId" < "DirectUserHighId")
                OR
                ("Type" = 2 AND "Name" IS NOT NULL AND "Name" ~ '[^[:space:]]'
                    AND "DirectUserLowId" IS NULL AND "DirectUserHighId" IS NULL)
                """);
            table.HasCheckConstraint("CK_Conversations_CreatedAt", "isfinite(\"CreatedAt\")");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Type).HasConversion<int>();
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DirectUserLowId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DirectUserHighId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.DirectUserLowId, x.DirectUserHighId })
            .IsUnique().HasFilter("\"Type\" = 1").HasDatabaseName("UX_Conversations_DirectPair");
    }
}

public sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("ConversationMembers", "conversations", table =>
        {
            table.HasCheckConstraint("CK_ConversationMembers_Role", "\"Role\" IN (1, 2)");
            table.HasCheckConstraint("CK_ConversationMembers_JoinedAt", "isfinite(\"JoinedAt\")");
        });
        builder.HasKey(x => new { x.ConversationId, x.UserId });
        builder.Property(x => x.Role).HasConversion<int>();
        builder.Property(x => x.JoinedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasOne(x => x.Conversation).WithMany(x => x.Members)
            .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.ConversationId });
        builder.HasIndex(x => x.ConversationId).IsUnique().HasFilter("\"Role\" = 2")
            .HasDatabaseName("UX_ConversationMembers_Owner");
    }
}
