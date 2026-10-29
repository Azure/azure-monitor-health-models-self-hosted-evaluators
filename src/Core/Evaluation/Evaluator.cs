using System.Globalization;
using System.Text.RegularExpressions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Evaluation;

/// <summary>Converts keyed query outcomes into health states using thresholds or a native-state mapping.</summary>
/// <param name="redactor">The shared redactor for rendered context and failure messages.</param>
/// <param name="time">An optional clock for measuring evaluation duration.</param>
/// <param name="diagnostics">Optional runtime metrics, defaulting to the shared instance.</param>
public sealed partial class Evaluator(Redactor redactor, TimeProvider? time = null, RuntimeDiagnostics? diagnostics = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly RuntimeDiagnostics diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;

    /// <summary>Evaluates one result, mapping failures or invalid outcomes to Unknown and rendering safe context.</summary>
    /// <param name="key">The complete identity expected in the result.</param>
    /// <param name="configuration">The effective signal rules, expiry and context template.</param>
    /// <param name="result">The plugin outcome; null or mismatched results become explicit failures.</param>
    /// <returns>The evaluated state with redacted, length-bounded report context.</returns>
    public EvaluatedSignal Evaluate(SignalKey key, SignalConfiguration configuration, SignalResult result)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(configuration);
        var started = time.GetTimestamp();
        var rules = configuration.Evaluation.Unhealthy is { } unhealthy
            ? new EvaluationRules(unhealthy, configuration.Evaluation.Degraded) : null;
        var state = HealthState.Unknown;
        double? value = null;
        string? context;
        if (result is null || result.Key != key)
            result = new SignalResult.Failure(key, "invalid_result", "The plugin returned an invalid result.");
        if (result is SignalResult.Observation observation)
        {
            if (observation.Value is { } number && !double.IsFinite(number))
                result = new SignalResult.Failure(key, "invalid_value", "The observed value is not finite.");
            else if (configuration.Evaluation.StateMap is not null && string.IsNullOrWhiteSpace(observation.NativeState))
                result = new SignalResult.Failure(key, "missing_state", "The observation has no native state.");
            else if (configuration.Evaluation.StateMap is null && observation.Value is null)
                result = new SignalResult.Failure(key, "missing_value", "The observation has no numeric value.");
            else
            {
                value = observation.Value;
                state = configuration.Evaluation.StateMap is { } map
                    ? map.GetValueOrDefault(observation.NativeState!, HealthState.Unknown)
                    : Matches(configuration.Evaluation.Unhealthy!, value!.Value) ? HealthState.Unhealthy
                    : configuration.Evaluation.Degraded is { } degraded && Matches(degraded, value.Value) ? HealthState.Degraded
                    : HealthState.Healthy;
            }
        }
        else if (result is SignalResult.NoData) state = configuration.Evaluation.NoData;
        else if (result is not SignalResult.Failure)
            result = new SignalResult.Failure(key, "invalid_result", "The plugin returned an invalid result.");

        if (result is SignalResult.Failure failure)
            context = $"Poll failed: {failure.Code}: {failure.Message}";
        else
        {
            var detail = result is SignalResult.Observation observed ? observed.Detail : ((SignalResult.NoData)result).Reason;
            context = configuration.Context is { } template
                ? TemplateToken().Replace(template, match => match.Groups[1].Value switch
                {
                    "value" => value?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                    "state" => (result as SignalResult.Observation)?.NativeState ?? "",
                    "healthState" => state.ToString(),
                    "detail" => detail ?? "",
                    "observedAt" => (result as SignalResult.Observation)?.ObservedAt.ToString("O", CultureInfo.InvariantCulture) ?? "",
                    _ => ""
                })
                : detail;
        }
        context = context is null ? null : redactor.Redact(context);
        if (context?.Length > 4096)
        {
            var length = char.IsHighSurrogate(context[4095]) ? 4095 : 4096;
            context = context[..length];
        }
        diagnostics.Evaluated(state.ToString(), time.GetElapsedTime(started));
        return new(key, state, value, rules, configuration.ExpiresInMinutes, context);
    }

    private static bool Matches(ThresholdRule rule, double value) => rule.Operator switch
    {
        RuleOperator.LessThan => value < rule.Threshold,
        RuleOperator.LessThanOrEqual => value <= rule.Threshold,
        RuleOperator.GreaterThan => value > rule.Threshold,
        RuleOperator.GreaterThanOrEqual => value >= rule.Threshold,
        RuleOperator.Equal => value == rule.Threshold,
        RuleOperator.NotEqual => value != rule.Threshold,
        _ => false
    };

    [GeneratedRegex(@"\{\{(value|state|healthState|detail|observedAt)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateToken();
}
