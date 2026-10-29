using System.Net;

namespace SelfHostedEvaluators.Core.Auth;

internal sealed class ResponseLifetimeContent : HttpContent
{
    private readonly HttpContent inner;
    private readonly CancellationToken lifetime;
    private Action? release;

    public ResponseLifetimeContent(HttpContent inner, Action release, CancellationToken lifetime = default)
    {
        this.inner = inner;
        this.release = release;
        this.lifetime = lifetime;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, ct);
        linked.Token.ThrowIfCancellationRequested();
        await inner.CopyToAsync(stream, context, linked.Token).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength ?? -1;
        return length >= 0;
    }

    protected override void Dispose(bool disposing)
    {
        var complete = disposing ? Interlocked.Exchange(ref release, null) : null;
        if (complete is null) { base.Dispose(disposing); return; }
        try { inner.Dispose(); }
        finally
        {
            try { base.Dispose(disposing); }
            finally { complete(); }
        }
    }
}
