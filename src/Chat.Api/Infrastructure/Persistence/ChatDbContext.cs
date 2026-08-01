using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Infrastructure.Persistence;

public sealed class ChatDbContext(
    DbContextOptions<ChatDbContext> options)
    : DbContext(options)
{
}