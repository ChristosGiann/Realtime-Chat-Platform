using Chat.Api.Conversations;
using Chat.Api.Identity;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Infrastructure.Persistence;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");
        builder.ApplyConfiguration(new ConversationConfiguration());
        builder.ApplyConfiguration(new ConversationMemberConfiguration());
        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(x => x.Email).IsRequired();
            user.Property(x => x.NormalizedEmail).IsRequired();
            user.Property(x => x.UserName).IsRequired();
            user.Property(x => x.NormalizedUserName).IsRequired();
            user.HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
        });
        builder.Entity<UserProfile>(profile =>
        {
            profile.ToTable("UserProfiles", "profiles");
            profile.HasKey(x => x.UserId);
            profile.Property(x => x.UserId).ValueGeneratedNever();
            profile.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            profile.Property(x => x.Bio).HasMaxLength(1000);
            profile.Property(x => x.AvatarUrl).HasMaxLength(2048);
            profile.HasOne<ApplicationUser>().WithOne()
                .HasForeignKey<UserProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
