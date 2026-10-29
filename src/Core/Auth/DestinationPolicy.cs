namespace SelfHostedEvaluators.Core.Auth;

internal sealed class DestinationPolicy
{
    private readonly HashSet<(string Host, int Port)> origins;

    public DestinationPolicy(IEnumerable<Uri> endpoints, IEnumerable<string>? allowedHosts = null)
    {
        origins = [];
        var allowList = allowedHosts?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                (allowList is { Count: > 0 } && !allowList.Contains(endpoint.IdnHost)))
                throw new AuthenticationFailureException("unapproved_endpoint");
            origins.Add((endpoint.IdnHost.ToLowerInvariant(), endpoint.Port));
        }
        if (origins.Count == 0) throw new AuthenticationFailureException("missing_endpoint");
    }

    public void Check(Uri? destination, bool webSocket = false)
    {
        if (destination is null || !destination.IsAbsoluteUri ||
            destination.Scheme != (webSocket ? "wss" : "https") || destination.UserInfo.Length != 0 ||
            destination.Fragment.Length != 0 ||
            !origins.Contains((destination.IdnHost.ToLowerInvariant(), destination.Port)))
            throw new AuthenticationFailureException("unapproved_origin");
    }

    public void Check(HttpRequestMessage request)
    {
        Check(request.RequestUri);
        if (request.Headers.Host is { } host && !host.Equals(request.RequestUri!.Authority, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationFailureException("host_override");
    }
}
