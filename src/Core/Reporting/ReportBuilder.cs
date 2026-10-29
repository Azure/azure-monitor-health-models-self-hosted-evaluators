using System.Text.Json;
using System.Text.RegularExpressions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Validates and serializes evaluated signals into size-bounded data-plane health reports.</summary>
/// <param name="time">The clock used to establish report creation and expiry times.</param>
/// <param name="redactor">The shared redactor applied before context serialization.</param>
public sealed class ReportBuilder(TimeProvider time, Redactor redactor)
{
    private static readonly Regex Name = new(@"\A[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Calculates a report lifetime from cadence settings before building the report.</summary>
    /// <param name="signal">The evaluated signal with its state, value and optional rules.</param>
    /// <param name="interval">The positive effective polling interval.</param>
    /// <param name="ttlMultiplier">The positive interval multiplier for expiry.</param>
    /// <param name="expiresInMinutes">An optional explicit lifetime overriding the multiplier.</param>
    /// <returns>A report with a newly calculated expiry and stable retry identity.</returns>
    public HealthReport Build(EvaluatedSignal signal, TimeSpan interval, int ttlMultiplier, int? expiresInMinutes) =>
        Build(signal with { ExpiresInMinutes = TtlCalculator.Calculate(interval, ttlMultiplier, expiresInMinutes) });

    /// <summary>Builds a report using the signal's effective expiry and redacted, truncated context.</summary>
    /// <param name="signal">The evaluated signal to validate and serialize.</param>
    /// <returns>A buffered UTF-8 report no larger than 32 KiB with a newly generated request ID.</returns>
    /// <exception cref="ArgumentException">Signal names, values, rules or serialized body size are invalid.</exception>
    public HealthReport Build(EvaluatedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Key is null || !ValidName(signal.Key.Name) || !ValidName(signal.Key.Entity))
            throw new ArgumentException("Invalid report name.", nameof(signal));
        if (!Enum.IsDefined(signal.State)) throw new ArgumentException("Invalid health state.", nameof(signal));
        if (signal.Value is { } value && !double.IsFinite(value))
            throw new ArgumentException("Report value must be finite.", nameof(signal));
        if (signal.ExpiresInMinutes is < 1 or > 10080)
            throw new ArgumentOutOfRangeException(nameof(signal), "Invalid effective TTL.");
        if (signal.EvaluationRules is { } rules)
        {
            Validate(rules.UnhealthyRule);
            if (rules.DegradedRule is { } degraded) Validate(degraded);
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("signalName", signal.Key.Name);
            writer.WriteString("healthState", signal.State.ToString());
            if (signal.Value is { } number) writer.WriteNumber("value", number);
            if (signal.EvaluationRules is { } evaluation)
            {
                writer.WriteStartObject("evaluationRules");
                WriteRule(writer, "unhealthyRule", evaluation.UnhealthyRule);
                if (evaluation.DegradedRule is { } degraded) WriteRule(writer, "degradedRule", degraded);
                writer.WriteEndObject();
            }
            writer.WriteNumber("expiresInMinutes", signal.ExpiresInMinutes);
            if (signal.AdditionalContext is { } context)
            {
                context = redactor.Redact(context);
                if (context.Length > 4096)
                {
                    var length = char.IsHighSurrogate(context[4095]) ? 4095 : 4096;
                    context = context[..length];
                }
                writer.WriteString("additionalContext", context);
            }
            writer.WriteEndObject();
        }
        if (buffer.Length > 32768) throw new ArgumentException("Report body exceeds 32768 bytes.", nameof(signal));
        return new(signal.Key, buffer.ToArray(), time.GetUtcNow(), signal.ExpiresInMinutes);
    }

    private static bool ValidName(string? name) => name is { Length: >= 3 and <= 260 } && Name.IsMatch(name);
    private static void Validate(ThresholdRule? rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Operator) || !double.IsFinite(rule.Threshold))
            throw new ArgumentException("Invalid evaluation rule.");
    }
    private static void WriteRule(Utf8JsonWriter writer, string name, ThresholdRule rule)
    {
        writer.WriteStartObject(name);
        writer.WriteString("operator", rule.Operator.ToString());
        writer.WriteNumber("threshold", rule.Threshold);
        writer.WriteEndObject();
    }
}
