using System.Text.RegularExpressions;

namespace SelfHostedEvaluators.Core.Configuration;

internal static class AuthProfileParser
{
    internal static readonly string[] BuiltInSchemes =
    [
        "none", "apiKey", "bearer", "basic", "oauth2ClientCredentials", "mtls", "entraId",
        "managedIdentity", "workloadIdentity", "servicePrincipalSecret", "servicePrincipalCertificate"
    ];

    public static AuthProfile Parse(StrictJson json, HostOptions host)
    {
        if (json.Element.ValueKind != System.Text.Json.JsonValueKind.Object) throw json.Error();
        var type = json.Text("type");
        switch (type)
        {
            case "none":
                json.Shape("type");
                return new NoneAuthProfile();
            case "apiKey":
                json.Shape("type", "headers", "queryParameter", "secret");
                var headers = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
                if (json.Has("headers"))
                {
                    var field = json.Child("headers").ShapeMap();
                    foreach (var property in field.Element.EnumerateObject())
                    {
                        if (!Regex.IsMatch(property.Name, @"^[!#$%&'*+.^_`|~0-9a-zA-Z-]+\z") ||
                            !headers.TryAdd(property.Name, field.Child(property.Name).Secret())) throw field.Error();
                    }
                }
                var parameter = json.OptionalText("queryParameter");
                if ((headers.Count > 0) == (parameter is not null) || json.Has("secret") != (parameter is not null))
                    throw json.Error();
                if (parameter is not null && !Regex.IsMatch(parameter, @"^[a-zA-Z0-9_-]+\z")) throw json.Error("queryParameter");
                return new ApiKeyAuthProfile(headers, parameter, parameter is null ? null : json.Child("secret").Secret());
            case "bearer":
                json.Shape("type", "token");
                return new BearerAuthProfile(json.Child("token").Secret());
            case "basic":
                json.Shape("type", "username", "password");
                return new BasicAuthProfile(json.Child("username").Secret(), json.Child("password").Secret());
            case "mtls":
                json.Shape("type", "certificate");
                return new MtlsAuthProfile(json.Child("certificate").Secret());
            case "oauth2ClientCredentials":
                json.Shape("type", "tokenEndpoint", "clientId", "clientSecret", "scopes");
                return new OAuth2ClientCredentialsAuthProfile(json.Child("tokenEndpoint").Https(), json.Text("clientId"),
                    json.Child("clientSecret").Secret(), json.Child("scopes").Items().Select(s => s.Text()));
            case "entraId":
                json.Shape("type", "azure", "scope");
                var azure = Parse(json.Child("azure"), host) as AzureAuthProfile ?? throw json.Error("azure");
                return new EntraIdAuthProfile(azure, json.Text("scope"));
            case "managedIdentity":
                json.Shape("type", "clientId");
                return new ManagedIdentityAuthProfile(json.Has("clientId") ? GuidText(json, "clientId") : host.AzureClientId);
            case "workloadIdentity":
                json.Shape("type", "tenantId", "clientId", "tokenFile");
                var tokenFile = json.OptionalText("tokenFile") ?? host.AzureFederatedTokenFile;
                if (string.IsNullOrWhiteSpace(tokenFile)) throw json.Error("tokenFile");
                return new WorkloadIdentityAuthProfile(GuidText(json, "tenantId"), GuidText(json, "clientId"), tokenFile);
            case "servicePrincipalSecret":
                json.Shape("type", "tenantId", "clientId", "clientSecret");
                return new ServicePrincipalSecretAuthProfile(GuidText(json, "tenantId"), GuidText(json, "clientId"),
                    json.Child("clientSecret").Secret());
            case "servicePrincipalCertificate":
                json.Shape("type", "tenantId", "clientId", "certificate");
                return new ServicePrincipalCertificateAuthProfile(GuidText(json, "tenantId"), GuidText(json, "clientId"),
                    json.Child("certificate").Secret());
            default:
                if (BuiltInSchemes.Contains(type, StringComparer.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(type, @"^[a-zA-Z][a-zA-Z0-9-]*\z")) throw json.Error("type");
                return new CustomAuthProfile(type, json.Element);
        }
    }

    private static string GuidText(StrictJson json, string property)
    {
        var text = json.Text(property);
        if (!Guid.TryParseExact(text, "D", out _)) throw json.Error(property);
        return text;
    }

    internal static StrictJson ShapeMap(this StrictJson json)
    {
        if (json.Element.ValueKind != System.Text.Json.JsonValueKind.Object) throw json.Error();
        return json;
    }
}
