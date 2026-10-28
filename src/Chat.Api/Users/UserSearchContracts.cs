using System.ComponentModel.DataAnnotations;
using Chat.Api.Profiles;

namespace Chat.Api.Users;

public sealed class UserSearchRequest
{
    [StringLength(100)]
    public string? Query { get; init; }

    [StringLength(4096)]
    public string? Cursor { get; init; }

    [Range(1, 50)]
    public int PageSize { get; init; } = 20;
}

public sealed record UserSearchResponse(IReadOnlyList<PublicProfileResponse> Items, string? NextCursor);
