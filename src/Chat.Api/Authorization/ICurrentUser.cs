namespace Chat.Api.Authorization;

/// <summary>The authenticated user's ID, or null when no valid authenticated identity is available.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}
