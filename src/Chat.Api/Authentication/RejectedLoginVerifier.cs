using System.Security.Cryptography;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Chat.Api.Authentication;

// Perform a password hash verification even when the account cannot be authenticated.
// This reduces the obvious timing difference of returning before password hashing.
public sealed class RejectedLoginVerifier
{
    private readonly ApplicationUser _dummy = new();
    private readonly PasswordHasher<ApplicationUser> _hasher;
    private readonly string _hash;

    public RejectedLoginVerifier(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<ApplicationUser>(options);
        _hash = _hasher.HashPassword(_dummy, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_dummy, _hash, password);
}
