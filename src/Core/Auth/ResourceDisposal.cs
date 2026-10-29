namespace SelfHostedEvaluators.Core.Auth;

internal static class ResourceDisposal
{
    internal static async ValueTask DisposeAsync(IEnumerable<IDisposable> synchronous, params IAsyncDisposable?[] asynchronous)
    {
        var failed = false;
        foreach (var resource in synchronous)
        {
            try { resource.Dispose(); }
            catch (Exception) { failed = true; }
        }
        foreach (var resource in asynchronous)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { failed = true; }
        }
        if (failed) throw new AuthenticationFailureException("resource_disposal");
    }
}
