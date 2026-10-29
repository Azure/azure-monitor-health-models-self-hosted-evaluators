using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Evaluation;

/// <summary>Carries numeric evaluation rules into a health report when threshold evaluation is used.</summary>
/// <param name="UnhealthyRule">The unhealthy boundary, evaluated first.</param>
/// <param name="DegradedRule">The optional degraded boundary.</param>
public sealed record EvaluationRules(ThresholdRule UnhealthyRule, ThresholdRule? DegradedRule);

/// <summary>Contains the health state and sanitized context ready for report construction.</summary>
/// <param name="Key">The complete model/entity/signal identity.</param>
/// <param name="State">The evaluated health state.</param>
/// <param name="Value">The optional numeric observation.</param>
/// <param name="EvaluationRules">Numeric rules, or null for state-map evaluation.</param>
/// <param name="ExpiresInMinutes">The effective report lifetime in minutes.</param>
/// <param name="AdditionalContext">Optional redacted context bounded to the report context limit.</param>
public sealed record EvaluatedSignal(
    SignalKey Key,
    HealthState State,
    double? Value,
    EvaluationRules? EvaluationRules,
    int ExpiresInMinutes,
    string? AdditionalContext);
