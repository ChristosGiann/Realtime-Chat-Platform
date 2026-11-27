namespace Chat.Api.Infrastructure;

public static class RailwayEdgeHttps
{
    public static bool Matches(HttpRequest request, Uri publicOrigin) =>
        request.Scheme == "http"
        && string.Equals(request.Host.Value, publicOrigin.Authority, StringComparison.OrdinalIgnoreCase)
        && request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto.Count == 1 && proto[0] == "https"
        && request.Headers.TryGetValue("X-Forwarded-Host", out var host) && host.Count == 1
        && string.Equals(host[0], publicOrigin.Authority, StringComparison.OrdinalIgnoreCase);
}
