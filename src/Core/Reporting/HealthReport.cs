using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Holds one buffered health report with a stable request ID and an absolute in-memory expiry.</summary>
public sealed class HealthReport
{
    internal HealthReport(SignalKey key, byte[] body, DateTimeOffset createdAt, int ttl)
    {
        Key = key;
        Body = body;
        CreatedAt = createdAt;
        ExpiresAt = createdAt.AddMinutes(ttl);
        ClientRequestId = Guid.NewGuid().ToString();
    }

    /// <summary>Gets the complete identity used to route and supersede the report.</summary>
    public SignalKey Key { get; }
    /// <summary>Gets the serialized UTF-8 JSON body reused unchanged across retries.</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>Gets the report construction time.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>Gets the deadline after which the publisher must discard this report.</summary>
    public DateTimeOffset ExpiresAt { get; }
    /// <summary>Gets the request identifier preserved across attempts for this report.</summary>
    public string ClientRequestId { get; }
}
