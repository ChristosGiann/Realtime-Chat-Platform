using Chat.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Realtime;

[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
public sealed class ChatHub(ChatSessionValidator sessions) : Hub
{
    public const string Endpoint = "/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        if (!await sessions.IsValidAsync(Context.User)) { Context.Abort(); return; }
        await base.OnConnectedAsync();
    }
}
