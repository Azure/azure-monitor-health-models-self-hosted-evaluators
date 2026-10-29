using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;

namespace SelfHostedEvaluators.Core.Configuration;

/// <summary>Identifies an authentication scheme without resolving its credential references.</summary>
/// <param name="Type">The built-in or package-registered scheme name.</param>
public abstract record AuthProfile(string Type);
/// <summary>Selects explicitly unauthenticated transport.</summary>
public sealed record NoneAuthProfile() : AuthProfile("none");
/// <summary>Configures a bearer credential resolved from a secret reference.</summary>
/// <param name="Token">The reference to the bearer token.</param>
public sealed record BearerAuthProfile(Uri Token) : AuthProfile("bearer");
/// <summary>Configures HTTP Basic credentials using separate secret references.</summary>
/// <param name="Username">The reference to the username.</param>
/// <param name="Password">The reference to the password.</param>
public sealed record BasicAuthProfile(Uri Username, Uri Password) : AuthProfile("basic");
/// <summary>Configures mutual TLS with a referenced certificate payload.</summary>
/// <param name="Certificate">The reference to the encoded client certificate.</param>
public sealed record MtlsAuthProfile(Uri Certificate) : AuthProfile("mtls");
/// <summary>Configures Entra bearer authentication using an Azure credential and explicit scope.</summary>
/// <param name="Azure">The credential configuration used to acquire the token.</param>
/// <param name="Scope">The requested token scope.</param>
public sealed record EntraIdAuthProfile(AzureAuthProfile Azure, string Scope) : AuthProfile("entraId");

/// <summary>Configures API keys in named headers or a query parameter using secret references.</summary>
public sealed record ApiKeyAuthProfile : AuthProfile
{
    internal ApiKeyAuthProfile(IReadOnlyDictionary<string, Uri> headers, string? queryParameter, Uri? secret)
        : base("apiKey")
    {
        Headers = headers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        QueryParameter = queryParameter;
        Secret = secret;
    }
    /// <summary>Gets the case-insensitive mapping of header names to secret references.</summary>
    public IReadOnlyDictionary<string, Uri> Headers { get; }
    /// <summary>Gets the optional query-parameter name for the API key.</summary>
    public string? QueryParameter { get; }
    /// <summary>Gets the optional secret reference supplying the query-parameter value.</summary>
    public Uri? Secret { get; }
}

/// <summary>Configures an OAuth client-credentials exchange without embedding its secret value.</summary>
public sealed record OAuth2ClientCredentialsAuthProfile : AuthProfile
{
    internal OAuth2ClientCredentialsAuthProfile(Uri tokenEndpoint, string clientId, Uri clientSecret, IEnumerable<string> scopes)
        : base("oauth2ClientCredentials")
    {
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        ClientSecret = clientSecret;
        Scopes = scopes.ToImmutableArray();
    }
    /// <summary>Gets the approved HTTPS token-exchange endpoint.</summary>
    public Uri TokenEndpoint { get; }
    /// <summary>Gets the OAuth client identifier.</summary>
    public string ClientId { get; }
    /// <summary>Gets the reference to the OAuth client secret.</summary>
    public Uri ClientSecret { get; }
    /// <summary>Gets the immutable scopes requested during the exchange.</summary>
    public ImmutableArray<string> Scopes { get; }
}

/// <summary>Base configuration for Azure SDK token credentials.</summary>
/// <param name="Scheme">The Azure credential scheme name, also exposed as the profile type.</param>
public abstract record AzureAuthProfile(string Scheme) : AuthProfile(Scheme);
/// <summary>Selects the system-assigned or a specific user-assigned managed identity.</summary>
/// <param name="ClientId">The user-assigned identity's client ID, or null for system-assigned identity.</param>
public sealed record ManagedIdentityAuthProfile(string? ClientId) : AzureAuthProfile("managedIdentity");
/// <summary>Configures federated workload identity using an externally supplied token file.</summary>
/// <param name="TenantId">The Entra tenant identifier.</param>
/// <param name="ClientId">The application client identifier.</param>
/// <param name="TokenFile">The path to the federated token file.</param>
public sealed record WorkloadIdentityAuthProfile(string TenantId, string ClientId, string TokenFile)
    : AzureAuthProfile("workloadIdentity");
/// <summary>Configures a service principal using a referenced client secret.</summary>
/// <param name="TenantId">The Entra tenant identifier.</param>
/// <param name="ClientId">The application client identifier.</param>
/// <param name="ClientSecret">The secret reference, not the credential value.</param>
public sealed record ServicePrincipalSecretAuthProfile(string TenantId, string ClientId, Uri ClientSecret)
    : AzureAuthProfile("servicePrincipalSecret");
/// <summary>Configures a service principal using a referenced client certificate.</summary>
/// <param name="TenantId">The Entra tenant identifier.</param>
/// <param name="ClientId">The application client identifier.</param>
/// <param name="Certificate">The reference to the encoded client certificate.</param>
public sealed record ServicePrincipalCertificateAuthProfile(string TenantId, string ClientId, Uri Certificate)
    : AzureAuthProfile("servicePrincipalCertificate");

/// <summary>Retains a cloned custom profile for a package-provided authentication scheme.</summary>
public sealed record CustomAuthProfile : AuthProfile
{
    internal CustomAuthProfile(string type, JsonElement profile) : base(type) => Profile = profile.Clone();
    /// <summary>Gets the custom profile JSON, independent of the source document.</summary>
    public JsonElement Profile { get; }
}
