namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Contains an immutable set of redacted offline validation errors.</summary>
public sealed class ValidationResult
{
    private ValidationResult(string[] errors) => Errors = Array.AsReadOnly(errors);

    /// <summary>Gets the shared result representing successful validation.</summary>
    public static ValidationResult Success { get; } = new([]);
    /// <summary>Gets whether validation produced no errors.</summary>
    public bool IsValid => Errors.Count == 0;
    /// <summary>Gets nonempty error descriptions that must not contain credentials or input JSON.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates a failed result by copying one or more nonblank, redacted errors.</summary>
    /// <param name="errors">Error descriptions safe for diagnostics.</param>
    /// <returns>An invalid result containing the supplied errors.</returns>
    /// <exception cref="ArgumentException">No errors were supplied, or an error is blank.</exception>
    public static ValidationResult Failed(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Length == 0 || errors.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one nonempty validation error is required.", nameof(errors));
        }
        return new ValidationResult((string[])errors.Clone());
    }
}
