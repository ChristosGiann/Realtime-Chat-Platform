using System.Security.Claims;
using Chat.Api.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Chat.Api.IntegrationTests;

public sealed class CurrentUserTests
{
    [Fact]
    public void OutsideHttpRequest_HasNoUser()
    {
        var accessor = new HttpContextCurrentUser(new HttpContextAccessor(), Options.Create(new IdentityOptions()));
        Assert.Null(accessor.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingOrInvalidIdentifier_HasNoUser(string? value)
    {
        var claims = value is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.NameIdentifier, value)];
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var accessor = new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context }, Options.Create(new IdentityOptions()));
        Assert.Null(accessor.UserId);
    }

    [Fact]
    public void AnonymousClaims_AreNotTrusted()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]))
        };
        var accessor = new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context }, Options.Create(new IdentityOptions()));
        Assert.Null(accessor.UserId);
    }

    [Fact]
    public void UsesConfiguredIdentityClaim_AndDoesNotCachePreviousPrincipal()
    {
        var id = Guid.NewGuid();
        var options = new IdentityOptions();
        options.ClaimsIdentity.UserIdClaimType = "application-user-id";
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("application-user-id", id.ToString())], "test"))
        };
        var accessor = new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context }, Options.Create(options));
        Assert.Equal(id, accessor.UserId);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Null(accessor.UserId);
    }
}
