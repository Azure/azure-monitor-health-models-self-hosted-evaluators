using System.Globalization;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Parses HTTP Retry-After delta-seconds and supported HTTP-date forms without guessing malformed values.</summary>
public static class RetryAfterParser
{
    /// <summary>Converts a Retry-After value to a nonnegative delay relative to the supplied clock.</summary>
    /// <param name="value">The single raw header value.</param>
    /// <param name="now">The current time used for date calculations and overflow checks.</param>
    /// <returns>The delay, zero for a past date, or null for malformed or unrepresentable values.</returns>
    public static TimeSpan? Parse(string value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                seconds > (DateTimeOffset.MaxValue - now).TotalSeconds) return null;
            return TimeSpan.FromSeconds(seconds);
        }
        string[] formats = ["r", "dddd, dd-MMM-yy HH':'mm':'ss 'GMT'", "ddd MMM d HH':'mm':'ss yyyy",
            "ddd MMM  d HH':'mm':'ss yyyy"];
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return null;
        return date > now ? date - now : TimeSpan.Zero;
    }
}
