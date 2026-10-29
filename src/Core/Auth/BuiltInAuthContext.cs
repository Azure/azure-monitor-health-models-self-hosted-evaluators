using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Auth;

internal sealed class BuiltInAuthContext(AuthProfile profile, SecretResolver secrets, AzureCredentialFactory credentials,
    HttpClient exchange, Redactor redactor, TimeProvider time, string? azureScope, PluginRequestLimiter limiter, TimeSpan timeout) : IAuthContext
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, X509Certificate2> certificates = new(StringComparer.Ordinal);
    private TokenCredential? credential;
    private string? oauthSecret;
    private string? oauthToken;
    private DateTimeOffset oauthExpires;
    private bool disposed;
    public string Scheme => profile.Type;

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        switch (profile)
        {
            case ApiKeyAuthProfile apiKey:
                foreach (var entry in apiKey.Headers)
                { redactor.RegisterHeader(entry.Key); await secrets.ResolveAsync(entry.Value, ct).ConfigureAwait(false); }
                if (apiKey.Secret is not null) await secrets.ResolveAsync(apiKey.Secret, ct).ConfigureAwait(false);
                break;
            case BearerAuthProfile bearer:
                await secrets.ResolveAsync(bearer.Token, ct).ConfigureAwait(false);
                break;
            case BasicAuthProfile basic:
                await secrets.ResolveAsync(basic.Username, ct).ConfigureAwait(false);
                await secrets.ResolveAsync(basic.Password, ct).ConfigureAwait(false);
                break;
            case MtlsAuthProfile mtls:
                await CertificateAsync(mtls.Certificate, ct).ConfigureAwait(false);
                break;
            case OAuth2ClientCredentialsAuthProfile oauth:
                await secrets.ResolveAsync(oauth.ClientSecret, ct).ConfigureAwait(false);
                break;
            case EntraIdAuthProfile entra:
                credential = await credentials.CreateAsync(entra.Azure, ct, limiter, timeout).ConfigureAwait(false);
                break;
            case AzureAuthProfile azure:
                credential = await credentials.CreateAsync(azure, ct, limiter, timeout).ConfigureAwait(false);
                break;
            case NoneAuthProfile: break;
            default: throw new AuthenticationFailureException("unsupported_scheme");
        }
    }

    public async ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        switch (profile)
        {
            case ApiKeyAuthProfile apiKey:
                foreach (var header in apiKey.Headers)
                    SetHeader(request, header.Key, await secrets.ResolveAsync(header.Value, ct).ConfigureAwait(false));
                if (apiKey.QueryParameter is not null)
                {
                    var value = await secrets.ResolveAsync(apiKey.Secret!, ct).ConfigureAwait(false);
                    var builder = new UriBuilder(request.RequestUri!);
                    var pairs = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(pair => Uri.UnescapeDataString(pair.Split('=')[0].Replace("+", " ", StringComparison.Ordinal)) != apiKey.QueryParameter);
                    builder.Query = string.Join("&", pairs.Append(Uri.EscapeDataString(apiKey.QueryParameter) + "=" + Uri.EscapeDataString(value)));
                    request.RequestUri = builder.Uri;
                }
                break;
            case BearerAuthProfile bearer:
                SetHeader(request, "Authorization", "Bearer " + await secrets.ResolveAsync(bearer.Token, ct).ConfigureAwait(false));
                break;
            case BasicAuthProfile basic:
                var user = await secrets.ResolveAsync(basic.Username, ct).ConfigureAwait(false);
                var password = await secrets.ResolveAsync(basic.Password, ct).ConfigureAwait(false);
                if (user.Contains(':')) throw new AuthenticationFailureException("invalid_basic_username");
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
                redactor.RegisterSecret(encoded);
                SetHeader(request, "Authorization", "Basic " + encoded);
                break;
            case OAuth2ClientCredentialsAuthProfile oauth:
                SetHeader(request, "Authorization", "Bearer " + await OAuthTokenAsync(oauth, ct).ConfigureAwait(false));
                break;
            case EntraIdAuthProfile entra:
                SetHeader(request, "Authorization", "Bearer " + (await credential!.GetTokenAsync(new([entra.Scope]), ct).ConfigureAwait(false)).Token);
                break;
            case AzureAuthProfile:
                SetHeader(request, "Authorization", "Bearer " + (await credential!.GetTokenAsync(
                    new([azureScope ?? "https://data.cloudhealth.azure.com/.default"]), ct).ConfigureAwait(false)).Token);
                break;
            case NoneAuthProfile or MtlsAuthProfile: break;
            default: throw new AuthenticationFailureException("unsupported_scheme");
        }
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        if (value.Any(c => char.IsControl(c))) throw new AuthenticationFailureException("invalid_header_value");
        request.Headers.Remove(name);
        request.Headers.Add(name, value);
    }

    private async ValueTask<string> OAuthTokenAsync(OAuth2ClientCredentialsAuthProfile oauth, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secret = await secrets.ResolveAsync(oauth.ClientSecret, ct).ConfigureAwait(false);
            if (secret == oauthSecret && oauthToken is not null && oauthExpires > time.GetUtcNow()) return oauthToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = oauth.ClientId,
                    ["client_secret"] = secret, ["scope"] = string.Join(" ", oauth.Scopes)
                })
            };
            using var response = await exchange.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AuthenticationFailureException("oauth_response");
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(accessToken.GetString()) ||
                !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds <= 0 ||
                !root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new AuthenticationFailureException("invalid_oauth_token");
            oauthToken = accessToken.GetString()!;
            redactor.RegisterSecret(oauthToken);
            oauthSecret = secret;
            oauthExpires = time.GetUtcNow().AddSeconds(seconds);
            return oauthToken;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<X509Certificate2> CertificateAsync(Uri reference, CancellationToken ct)
    {
        // Certificate generations remain alive until the host releases the context after all batches.
        var value = await secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!certificates.TryGetValue(value, out var certificate))
            {
                certificate = SecretResolver.LoadCertificate(value);
                certificates.Add(value, certificate);
            }
            return certificate;
        }
        finally { gate.Release(); }
    }

    public async ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (profile is MtlsAuthProfile mtls)
        {
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            handler.ClientCertificates.Add(await CertificateAsync(mtls.Certificate, ct).ConfigureAwait(false));
        }
    }

    public async ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (profile is MtlsAuthProfile mtls)
        {
            options.ClientCertificates ??= new();
            options.ClientCertificates.Clear();
            options.ClientCertificates.Add(await CertificateAsync(mtls.Certificate, ct).ConfigureAwait(false));
            return;
        }
        if (profile is ApiKeyAuthProfile { QueryParameter: not null })
            throw new AuthenticationFailureException("websocket_query_auth_unsupported");
        var builder = new UriBuilder(destination) { Scheme = "https", Port = destination.Port };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        await AuthenticateAsync(request, ct).ConfigureAwait(false);
        foreach (var header in request.Headers) options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (credential is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
            foreach (var certificate in certificates.Values) certificate.Dispose();
            certificates.Clear();
            oauthToken = null;
            oauthSecret = null;
        }
        finally { gate.Release(); }
    }
}
