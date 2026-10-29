using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SelfHostedEvaluators.Core.Auth;

/// <summary>Removes registered credentials, sensitive headers and URL queries from diagnostics and report context.</summary>
public sealed class Redactor
{
    private readonly ConcurrentDictionary<string, byte> secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> headers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex UrlQuery = new(
        @"\b(?:https?|wss?)://[^\s""'<>?]+(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RelativeQuery = new(
        @"(?<!\w)/[^\s""'<>?]*\?[^\s""'<>]*",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Creates a redactor with built-in HTTP and first-party vendor credential header names.</summary>
    public Redactor()
    {
        foreach (var header in new[] { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "X-API-Key", "API-Key", "X-SF-Token", "DD-API-KEY", "DD-APPLICATION-KEY" }) RegisterHeader(header);
    }

    /// <summary>Registers a secret and its encoded representations for subsequent redaction.</summary>
    /// <param name="value">A resolved credential or token; empty values are ignored.</param>
    public void RegisterSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var representation in SecretRepresentations(value)) secrets.TryAdd(representation, 0);
    }

    private static IEnumerable<string> SecretRepresentations(string value) =>
        [value, Uri.EscapeDataString(value), WebUtility.UrlEncode(value), System.Text.Json.JsonEncodedText.Encode(value).ToString()];

    /// <summary>Marks a header name as sensitive using case-insensitive matching.</summary>
    /// <param name="name">The nonblank header name whose values must be removed.</param>
    public void RegisterHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        headers.TryAdd(name, 0);
    }

    /// <summary>Redacts known secrets and sensitive HTTP fields from text.</summary>
    /// <param name="text">The text to sanitize before it crosses a diagnostic boundary.</param>
    /// <returns>Sanitized text, or a redaction marker if pattern matching exceeds its bound.</returns>
    public string Redact(string text) => Redact(text, []);

    private string Redact(string text, IEnumerable<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            // Remove complete values before header/query rewriting can split a registered secret.
            foreach (var value in secrets.Keys.Concat(sensitiveValues).Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
                text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            text = UrlQuery.Replace(text, match =>
            {
                var value = match.Value;
                var query = value.IndexOf('?');
                if (query >= 0) value = value[..query] + "?[REDACTED]";
                var authority = value.IndexOf("://", StringComparison.Ordinal) + 3;
                var at = value.IndexOf('@', authority);
                return at < 0 ? value : value[..authority] + "[REDACTED]@" + value[(at + 1)..];
            });
            text = RelativeQuery.Replace(text, match => match.Value[..match.Value.IndexOf('?')] + "?[REDACTED]");
            foreach (var header in headers.Keys)
            {
                var name = Regex.Escape(header);
                var structured = """(?im)(["']""" + name + """["']\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\r\n}]+)""";
                text = Regex.Replace(text, structured, "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                var plain = """(?im)(?<![\w"'-])(""" + name + """\s*[:=]\s*)[^\r\n]+""";
                text = Regex.Replace(text, plain, "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[REDACTED]"; }
    }

    /// <summary>Wraps a logger so formatted messages, structured state and scopes are redacted.</summary>
    /// <param name="logger">The destination logger, whose lifetime remains externally owned.</param>
    /// <returns>A logger that omits raw exceptions and sanitizes event state.</returns>
    public ILogger Wrap(ILogger logger) => new RedactingLogger(logger, this);

    /// <summary>Sanitizes an activity's display name, tags, baggage and status description in place.</summary>
    /// <param name="activity">The activity to sanitize before export.</param>
    public void RedactActivity(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        activity.DisplayName = Redact(activity.DisplayName);
        if (activity.TraceStateString is { } traceState) activity.TraceStateString = Redact(traceState);
        foreach (var tag in activity.TagObjects.ToArray())
            activity.SetTag(tag.Key, RedactAttribute(tag.Key, tag.Value));
        foreach (var baggage in activity.Baggage.ToArray())
            activity.SetBaggage(baggage.Key, IsSensitiveAttribute(baggage.Key) ? "[REDACTED]" :
                baggage.Value is null ? null : Redact(baggage.Value));
        if (activity.StatusDescription is { } description)
            activity.SetStatus(activity.Status, activity.Status == ActivityStatusCode.Error ? "[REDACTED]" : Redact(description));
    }

    /// <summary>Sanitizes a structured attribute, replacing sensitive fields and exception values entirely.</summary>
    /// <param name="key">The attribute name used to detect sensitive fields.</param>
    /// <param name="value">The attribute value to sanitize.</param>
    /// <returns>A sanitized scalar, string array, null or redaction marker.</returns>
    public object? RedactAttribute(string key, object? value) => IsSensitiveAttribute(key) ||
        key.StartsWith("exception.", StringComparison.OrdinalIgnoreCase) ? "[REDACTED]" :
        value switch
        {
            null => null,
            Exception => "[REDACTED]",
            string text => Redact(text),
            string[] values => values.Select(Redact).ToArray(),
            bool or byte or short or int or long or float or double or decimal => value,
            _ => Redact(value.ToString() ?? "")
        };

    private bool IsSensitiveAttribute(string key)
    {
        if (headers.ContainsKey(key) || key.Contains("header", StringComparison.OrdinalIgnoreCase)) return true;
        var name = key.Replace('_', '.').Replace('-', '.');
        return name.Equals("exception", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("exception.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("queryString", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("requestTarget", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("urlQuery", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("query", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".query", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".query.string", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("query.string", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("http.target", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("request.target", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".request.target", StringComparison.OrdinalIgnoreCase);
    }

    private string RedactState<TState>(TState state, string text)
    {
        if (state is Exception) return "[REDACTED]";
        var sensitiveValues = state is IEnumerable<KeyValuePair<string, object?>> attributes
            ? attributes.Where(attribute => IsSensitiveAttribute(attribute.Key) || attribute.Value is Exception)
                .SelectMany(attribute => AttributeValues(attribute.Value)).SelectMany(SecretRepresentations)
            : [];
        return Redact(text, sensitiveValues);
    }

    private static IEnumerable<string> AttributeValues(object? value) =>
        value is IEnumerable<string> values ? values : [value?.ToString() ?? ""];

    private sealed class RedactingLogger(ILogger inner, Redactor redactor) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(Safe(state, state.ToString() ?? ""));
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            inner.Log(logLevel, new EventId(eventId.Id), Safe(state, formatter(state, null)), null,
                static (value, _) => value.ToString());
        }

        private SafeState Safe<TState>(TState state, string message) =>
            new(redactor.RedactState(state, message),
                state is IEnumerable<KeyValuePair<string, object?>> attributes
                    ? attributes.Select(a => new KeyValuePair<string, object?>(redactor.Redact(a.Key),
                        redactor.RedactAttribute(a.Key, a.Value))).ToArray() : []);
    }

    private sealed class SafeState(string message, KeyValuePair<string, object?>[] attributes)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => attributes.Length;
        public KeyValuePair<string, object?> this[int index] => attributes[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)attributes).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => message;
    }
}
