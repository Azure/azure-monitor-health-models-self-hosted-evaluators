namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Reports an authentication or transport failure using a safe code instead of upstream payloads.</summary>
public sealed class AuthenticationFailureException : Exception
{
    // Codes, not upstream exception messages or response bodies, cross the diagnostic boundary.
    internal AuthenticationFailureException(string code) : base($"Authentication/transport failure ({code}).") => Code = code;
    /// <summary>Gets the stable, non-secret failure category.</summary>
    public string Code { get; }
}
