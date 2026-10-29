using System.Globalization;

namespace SelfHostedEvaluators.Core.Evaluation;

/// <summary>Calculates whole-minute report lifetimes that cover at least two polling intervals.</summary>
public static class TtlCalculator
{
    /// <summary>Calculates expiry using an integer multiplier or an explicit minute override.</summary>
    /// <param name="interval">The positive effective polling interval.</param>
    /// <param name="multiplier">The positive interval multiplier.</param>
    /// <param name="overrideMinutes">An optional explicit expiry between one minute and one week.</param>
    /// <returns>The validated expiry in minutes, with calculated values rounded up and clamped.</returns>
    public static int Calculate(TimeSpan interval, int multiplier, int? overrideMinutes) =>
        Calculate(interval, (double)multiplier, overrideMinutes);

    /// <summary>Calculates expiry using a finite fractional multiplier or an explicit minute override.</summary>
    /// <param name="interval">The positive effective polling interval.</param>
    /// <param name="multiplier">The positive finite multiplier, used only when no override is supplied.</param>
    /// <param name="overrideMinutes">An optional explicit expiry between one minute and one week.</param>
    /// <returns>The validated expiry in minutes, with calculated values rounded up and clamped.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An input is invalid or expiry covers less than twice the interval.</exception>
    public static int Calculate(TimeSpan interval, double multiplier, int? overrideMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        if (!double.IsFinite(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        if (overrideMinutes is < 1 or > 10080)
            throw new ArgumentOutOfRangeException(nameof(overrideMinutes));
        // Preserve round-trip multiplier precision; multiply ticks first to keep 200s * 2.1 exactly seven minutes.
        var ttl = overrideMinutes ?? (multiplier >= 10080d * TimeSpan.TicksPerMinute / interval.Ticks
            ? 10080
            : (int)Math.Clamp(decimal.Ceiling(interval.Ticks *
                decimal.Parse(multiplier.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
                / TimeSpan.TicksPerMinute), 1, 10080));
        if (ttl * (decimal)TimeSpan.TicksPerMinute < 2m * interval.Ticks)
            throw new ArgumentOutOfRangeException(nameof(overrideMinutes), "TTL must cover at least twice the interval.");
        return ttl;
    }
}
