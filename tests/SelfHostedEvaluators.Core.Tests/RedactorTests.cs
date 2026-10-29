using Microsoft.Extensions.Logging;
using System.Diagnostics;
using SelfHostedEvaluators.Core.Auth;

namespace SelfHostedEvaluators.Core.Tests;

public class RedactorTests
{
    [Test]
    public void ResolvedSecretIsRemovedFromDiagnosticText()
    {
        var redactor = new Redactor();
        redactor.RegisterSecret("test-secret-value");
        Assert.That(redactor.Redact("request failed: test-secret-value"), Does.Not.Contain("test-secret-value"));
    }

    [Test]
    public void HeadersEncodedSecretsAndAllUrlQueriesAreRemoved()
    {
        var redactor = new Redactor();
        redactor.RegisterSecret("secret +/value");
        redactor.RegisterHeader("DD-API-KEY");
        var text = redactor.Redact("Authorization: Bearer unknown\nCookie: session=value\nDD-API-KEY: unknown\n" +
            """{"Authorization":"Bearer unknown","DD-API-KEY":"unknown"} """ +
            "https://sink.test/path?key=unknown&next=value wss://sink.test/stream?token=unknown " +
            Uri.EscapeDataString("secret +/value"));
        Assert.That(text, Does.Not.Contain("unknown").And.Not.Contain("session=value").And.Not.Contain("next=value")
            .And.Not.Contain(Uri.EscapeDataString("secret +/value")));
        Assert.That(text, Does.Contain("/path").And.Contain("/stream"));
    }

    [TestCase("X-API-Key: first,second", "first,second")]
    [TestCase("X-API-Key: first;second", "first;second")]
    [TestCase("X-API-Key: first%2Csecond", "first%2Csecond")]
    [TestCase("X-API-Key: prefix https://sink.test/path?key=hidden suffix", "prefix https://sink.test/path?key=hidden suffix")]
    [TestCase("Authorization: prefix https://sink.test/path?key=hidden suffix", "prefix https://sink.test/path?key=hidden suffix")]
    public void RegisteredSecretsAreRemovedBeforeDiagnosticRewriting(string text, string secret)
    {
        var redactor = new Redactor();
        redactor.RegisterSecret(secret);
        Assert.That(redactor.Redact(text + "\nvalue=" + secret),
            Is.EqualTo(text[..(text.IndexOf(':') + 1)] + " [REDACTED]\nvalue=[REDACTED]"));
    }

    [TestCase("X-API-Key: first,second")]
    [TestCase("X-API-Key: first;second")]
    [TestCase("X-API-Key: \"first\",second;third")]
    [TestCase("Cookie: first=value;second=value,third=value")]
    [TestCase("Set-Cookie: first=value; HttpOnly; SameSite=Strict")]
    public void UnregisteredHeaderValuesAreMaskedThroughCommasAndSemicolons(string text)
    {
        var redactor = new Redactor();
        Assert.That(redactor.Redact(text + "\nsafe detail"), Is.EqualTo(text[..(text.IndexOf(':') + 1)] + " [REDACTED]\nsafe detail"));
    }

    [Test]
    public void RegisteredSecretEncodingsAreRemovedBeforeUrlQueryRewriting()
    {
        var redactor = new Redactor();
        const string secret = "prefix https://sink.test/path?key=hidden suffix";
        redactor.RegisterSecret(secret);
        var representations = new[] { secret, Uri.EscapeDataString(secret), System.Net.WebUtility.UrlEncode(secret),
            System.Text.Json.JsonEncodedText.Encode(secret).ToString() };
        foreach (var value in representations)
            Assert.That(redactor.Redact(value), Is.EqualTo("[REDACTED]"));
    }

    [TestCase(LogLevel.Trace)]
    [TestCase(LogLevel.Debug)]
    [TestCase(LogLevel.Information)]
    [TestCase(LogLevel.Warning)]
    [TestCase(LogLevel.Error)]
    [TestCase(LogLevel.Critical)]
    public void LoggerRedactsFormattedStateScopesAndExceptionsAtEveryLevel(LogLevel level)
    {
        var redactor = new Redactor();
        redactor.RegisterSecret("secret");
        var sink = new CaptureLogger();
        var logger = redactor.Wrap(sink);
        using (logger.BeginScope("scope {value}", "secret"))
            logger.Log(level, new InvalidOperationException("secret https://sink.test?x=hidden"), "value {value}", "secret");
        Assert.That(string.Join(" ", sink.Text), Does.Not.Contain("secret").And.Not.Contain("hidden"));
        Assert.That(sink.Text, Has.Count.EqualTo(2));
    }

    [Test]
    public void TraceTagsBaggageAndContextUseTheSameRedactionBoundary()
    {
        var redactor = new Redactor();
        redactor.RegisterSecret("resolved-secret");
        using var activity = new Activity("test").Start();
        activity.SetTag("url.full", "https://sink.test/path?key=unknown");
        activity.SetTag("http.request.header.authorization", "Bearer unknown");
        activity.SetTag("detail", "resolved-secret");
        activity.AddBaggage("context", "resolved-secret");
        activity.SetStatus(ActivityStatusCode.Error, "https://sink.test?key=unknown");
        redactor.RedactActivity(activity);
        Assert.That(string.Join(" ", activity.Tags.Select(t => t.Value)), Does.Not.Contain("unknown").And.Not.Contain("resolved-secret"));
        Assert.That(activity.GetBaggageItem("context"), Is.EqualTo("[REDACTED]"));
        Assert.That(activity.StatusDescription, Does.Not.Contain("unknown"));
        Assert.That(redactor.Redact("additionalContext: resolved-secret https://sink.test?key=unknown"),
            Does.Not.Contain("unknown").And.Not.Contain("resolved-secret"));
    }

    [TestCase("url.query", "api_key=unregistered")]
    [TestCase("http.request.query_string", "?api_key=unregistered")]
    [TestCase("http.target", "/path?api_key=unregistered")]
    [TestCase("http.request.target", "/path?api_key=unregistered")]
    [TestCase("request-target", "/path?api_key=unregistered")]
    public void SeparatedQueryAndRequestTargetAttributesAreMasked(string name, string value)
    {
        var redactor = new Redactor();
        using var activity = new Activity("test").Start();
        activity.SetTag(name, value);
        activity.AddBaggage(name, value);
        activity.SetTag("http.request.method", "GET");
        redactor.RedactActivity(activity);
        Assert.That(activity.GetTagItem(name), Is.EqualTo("[REDACTED]"));
        Assert.That(activity.GetBaggageItem(name), Is.EqualTo("[REDACTED]"));
        Assert.That(activity.GetTagItem("http.request.method"), Is.EqualTo("GET"));
        activity.SetTag(name, new[] { value });
        redactor.RedactActivity(activity);
        Assert.That(activity.GetTagItem(name), Is.EqualTo("[REDACTED]"));
    }

    [TestCase(LogLevel.Trace)]
    [TestCase(LogLevel.Debug)]
    [TestCase(LogLevel.Information)]
    [TestCase(LogLevel.Warning)]
    [TestCase(LogLevel.Error)]
    [TestCase(LogLevel.Critical)]
    public void StructuredQueryAndRequestTargetLogValuesAreMaskedBeforeFormattingIsLost(LogLevel level)
    {
        var redactor = new Redactor();
        var sink = new CaptureLogger();
        var logger = redactor.Wrap(sink);
        using (logger.BeginScope("query {url.query}, target {http.target}", "scope_key=scope-hidden", "/path?key=scope-hidden"))
            logger.Log(level, "method {http.request.method}, query {url.query}, target {http.request.target}",
                "GET", "api_key=log-hidden", "/path?key=log-hidden");
        Assert.That(string.Join(" ", sink.Text), Does.Not.Contain("scope-hidden").And.Not.Contain("log-hidden").And.Contain("GET"));
        Assert.That(sink.Text, Has.Count.EqualTo(2));
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Text { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        { Text.Add(state.ToString()!); return null; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Text.Add(formatter(state, exception) + exception);
    }
}
