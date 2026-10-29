using System.Globalization;

namespace Checkout.Demo;

public sealed record TrafficOptions(TimeSpan Interval, Uri ApiUrl)
{
    public static TrafficOptions Parse(string? intervalSeconds, string? apiUrl)
    {
        var seconds = 5;
        if (intervalSeconds is not null &&
            !int.TryParse(intervalSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            throw new ArgumentException("DEMO_INTERVAL_SECONDS must be an integer number of seconds.");
        var interval = TimeSpan.FromSeconds(seconds);
        TrafficRunner.ValidateInterval(interval);
        return new TrafficOptions(interval, DemoEndpoint.Parse(apiUrl));
    }
}

internal static class DemoEndpoint
{
    public static Uri Parse(string? value)
    {
        if (!Uri.TryCreate(value ?? "http://127.0.0.1:8080", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("DEMO_API_URL must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        return uri;
    }
}
