using System.Text.Json;
using static Checkout.Demo.Tests.InfrastructureTools;

namespace Checkout.Demo.Tests;

[TestFixture, Parallelizable(ParallelScope.All)]
public sealed class RuntimeInfrastructureTests
{
    private static JsonElement Group() => Resource(Compile("runtime"), "Microsoft.ContainerInstance/containerGroups");

    [Test]
    public void RuntimeHasOnlyAnIdentityAuthenticatedPrivateLinuxContainerGroup()
    {
        var template = Compile("runtime");
        Assert.That(template.GetProperty("$schema").GetString(), Does.Contain("deploymentTemplate.json"));
        Assert.That(template.GetProperty("resources").GetArrayLength(), Is.EqualTo(1));
        var group = Resource(template, "Microsoft.ContainerInstance/containerGroups");
        Assert.That(group.GetProperty("identity").GetProperty("type").GetString(), Is.EqualTo("UserAssigned"));
        var identity = group.GetProperty("identity").GetProperty("userAssignedIdentities").EnumerateObject().Single();
        Assert.That(identity.Name, Does.Contain("Microsoft.ManagedIdentity/userAssignedIdentities"));
        Assert.That(ResolveVariable(template, group.GetProperty("tags")).GetProperty("she-demo-system").GetString(), Is.EqualTo("[variables('demoOwner')]"));
        var properties = group.GetProperty("properties");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(properties.GetProperty("osType").GetString(), Is.EqualTo("Linux"));
            Assert.That(properties.GetProperty("restartPolicy").GetString(), Is.EqualTo("Always"));
            Assert.That(properties.TryGetProperty("ipAddress", out _), Is.False);
            Assert.That(properties.TryGetProperty("subnetIds", out _), Is.False);
            Assert.That(properties.TryGetProperty("diagnostics", out _), Is.False);
            Assert.That(properties.GetProperty("containers").EnumerateArray().All(c => !c.GetProperty("properties").TryGetProperty("ports", out _)), Is.True);
        }
        var credentials = properties.GetProperty("imageRegistryCredentials").EnumerateArray().Single();
        Assert.That(identity.Name, Does.Contain(credentials.GetProperty("identity").GetString()![1..^1]));
        Assert.That(credentials.GetProperty("server").GetString(), Does.Contain(".loginServer"));
        Assert.That(credentials.TryGetProperty("username", out _), Is.False);
        Assert.That(credentials.TryGetProperty("password", out _), Is.False);
    }

    [Test]
    public void AppContainersUseTheSameDigestImageAndTheActualExecutableCommands()
    {
        var containers = Group().GetProperty("properties").GetProperty("containers").EnumerateArray().ToArray();
        Assert.That(containers.Select(c => c.GetProperty("name").GetString()),
            Is.EquivalentTo(new[] { "checkout-api", "traffic-generator", "prometheus" }));
        JsonElement Container(string name) => containers.Single(c => c.GetProperty("name").GetString() == name).GetProperty("properties");
        var api = Container("checkout-api");
        var traffic = Container("traffic-generator");
        Assert.That(api.GetProperty("image").GetString(), Is.EqualTo(traffic.GetProperty("image").GetString()));
        Assert.That(api.GetProperty("image").GetString(), Does.Contain("startsWith(variables('immutableImage')"));
        Assert.That(api.GetProperty("image").GetString(), Does.Contain("Microsoft.ContainerRegistry/registries").And.Contain(".loginServer"));
        Assert.That(api.GetProperty("command").EnumerateArray().Select(c => c.GetString()),
            Is.EqualTo(new[] { "dotnet", "/app/Checkout.Demo.dll", "api" }));
        Assert.That(traffic.GetProperty("command").EnumerateArray().Select(c => c.GetString()),
            Is.EqualTo(new[] { "dotnet", "/app/Checkout.Demo.dll", "traffic" }));
        var env = traffic.GetProperty("environmentVariables").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());
        Assert.That(env["DEMO_API_URL"], Is.EqualTo("http://127.0.0.1:8080"));
        Assert.That(env["DEMO_INTERVAL_SECONDS"], Is.EqualTo("[string(parameters('intervalSeconds'))]"));
        foreach (var probe in new[] { "readinessProbe", "livenessProbe" })
        {
            Assert.That(api.GetProperty(probe).TryGetProperty("httpGet", out var request), Is.True,
                "Health probes must query the running API rather than cold-starting another .NET process.");
            Assert.That(request.GetProperty("path").GetString(), Is.EqualTo("/healthz"));
            Assert.That(request.GetProperty("port").GetInt32(), Is.EqualTo(8080));
            Assert.That(request.GetProperty("scheme").GetString(), Is.EqualTo("http"));
            Assert.That(api.GetProperty(probe).TryGetProperty("exec", out _), Is.False);
        }
    }

    [Test]
    public void RuntimeBoundsTrafficIntervalAndRequiresImageAndIngestionInputs()
    {
        var template = Compile("runtime");
        var parameters = template.GetProperty("parameters");
        Assert.That(parameters.EnumerateObject().Select(p => p.Name),
            Is.EquivalentTo(new[] { "location", "namePrefix", "appImage", "intervalSeconds", "metricsIngestionEndpoint" }));
        var interval = parameters.GetProperty("intervalSeconds");
        Assert.That(interval.GetProperty("type").GetString(), Is.EqualTo("int"));
        Assert.That(interval.GetProperty("minValue").GetInt32(), Is.EqualTo(1));
        Assert.That(interval.GetProperty("maxValue").GetInt32(), Is.EqualTo(3600));
        Assert.That(interval.GetProperty("defaultValue").GetInt32(), Is.EqualTo(5));
        foreach (var parameter in new[] { "appImage", "metricsIngestionEndpoint" })
            Assert.That(parameters.GetProperty(parameter).TryGetProperty("defaultValue", out _), Is.False);
        Assert.That(template.GetProperty("outputs").EnumerateObject().Select(o => o.Name),
            Is.EquivalentTo(new[] { "containerGroupName", "containerGroupResourceId" }));
    }

    [Test]
    public void ContainersRequestSmallPositiveResourcesAboveAciGroupMinimum()
    {
        var requests = RuntimeSnapshot().GetProperty("predictedResources").EnumerateArray().Single()
            .GetProperty("properties").GetProperty("containers").EnumerateArray()
            .Select(c => c.GetProperty("properties").GetProperty("resources").GetProperty("requests")).ToArray();
        Assert.That(requests.Select(r => r.GetProperty("cpu").GetDouble()), Is.All.InRange(0.1, 1));
        Assert.That(requests.Select(r => r.GetProperty("memoryInGB").GetDouble()), Is.All.InRange(0.25, 1.5));
        Assert.That(requests.Sum(r => r.GetProperty("cpu").GetDouble()), Is.GreaterThanOrEqualTo(1));
        Assert.That(requests.Sum(r => r.GetProperty("memoryInGB").GetDouble()), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void PrometheusUsesPinnedImageReadOnlyConfigAndBoundedEphemeralStorage()
    {
        var properties = Group().GetProperty("properties");
        var prometheus = properties.GetProperty("containers").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "prometheus").GetProperty("properties");
        Assert.That(prometheus.GetProperty("image").GetString(), Is.EqualTo("prom/prometheus:v3.15.1"));
        var command = prometheus.GetProperty("command").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.That(command, Does.Contain("--config.file=/etc/checkout-prometheus/prometheus.yml"));
        Assert.That(command, Does.Contain("--storage.tsdb.path=/prometheus"));
        Assert.That(command, Does.Contain("--storage.tsdb.retention.time=1h"));
        Assert.That(command, Does.Contain("--storage.tsdb.retention.size=128MB"));
        Assert.That(command, Does.Contain("--web.listen-address=127.0.0.1:9090"));
        var mount = prometheus.GetProperty("volumeMounts").EnumerateArray().Single();
        Assert.That(mount.GetProperty("readOnly").GetBoolean(), Is.True);
        Assert.That(mount.GetProperty("mountPath").GetString(), Is.EqualTo("/etc/checkout-prometheus"));
        var volume = properties.GetProperty("volumes").EnumerateArray().Single();
        Assert.That(volume.GetProperty("name").GetString(), Is.EqualTo(mount.GetProperty("name").GetString()));
        Assert.That(volume.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "name", "secret" }));
        Assert.That(volume.GetProperty("secret").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "prometheus.yml" }));
    }

    [Test]
    public void OfflineSnapshotsKeepAllResourcesAndRoleScopesInTheCallerSelectedGroup()
    {
        var snapshot = Snapshot("main");
        var resources = snapshot.GetProperty("predictedResources").EnumerateArray().ToArray();
        Assert.That(resources, Has.Length.EqualTo(13));
        foreach (var resource in resources)
            Assert.That(resource.GetProperty("id").GetString(),
                Does.StartWith("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/checkout-test-rg/providers/"));
    }

    private static string RuntimeParameters(string? appImage = null) => $"""
        param appImage = '{appImage ?? "example.azurecr.io/checkout@sha256:" + new string('a', 64)}'
        param metricsIngestionEndpoint = 'https://example.eastus-1.metrics.ingest.monitor.azure.com/dataCollectionRules/dcr-00000000000000000000000000000000/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24'
        """;

    private static JsonElement RuntimeSnapshot() => Snapshot("runtime", RuntimeParameters());

    [Test]
    public void OfflineRuntimeSnapshotResolvesTheSameOwnedIdentityAndContainerGroupNames()
    {
        var main = Snapshot("main").GetProperty("predictedResources").EnumerateArray().ToArray();
        var owner = main.Single(r => r.GetProperty("type").GetString() == "Microsoft.Monitor/accounts")
            .GetProperty("tags").GetProperty("she-demo-system").GetString();
        var identity = main.Single(r => r.GetProperty("type").GetString() == "Microsoft.ManagedIdentity/userAssignedIdentities")
            .GetProperty("id").GetString();
        var runtime = RuntimeSnapshot().GetProperty("predictedResources").EnumerateArray().Single();
        Assert.That(runtime.GetProperty("name").GetString(), Is.EqualTo("aci-demo-" + owner));
        Assert.That(runtime.GetProperty("identity").GetProperty("userAssignedIdentities").EnumerateObject().Single().Name, Is.EqualTo(identity));
        Assert.That(runtime.GetProperty("properties").GetProperty("imageRegistryCredentials").EnumerateArray().Single()
            .GetProperty("identity").GetString(), Is.EqualTo(identity));
    }

    [TestCase("example.azurecr.io/checkout:latest")]
    [TestCase("example.azurecr.io/checkout@sha256:abcd")]
    [TestCase("example.azurecr.io/checkout@sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RuntimeRejectsMutableOrMalformedAppImages(string image)
    {
        var result = SnapshotAttempt("runtime", RuntimeParameters(image));
        Assert.That(result.ExitCode, Is.Not.Zero,
            "A deployment with a mutable tag or malformed SHA256 must not produce an accepted app image.");
        Assert.That(result.Output, Does.Contain("appImage must"));
    }

    [TestCase("main", "Demo")]
    [TestCase("main", "de-mo")]
    [TestCase("runtime", "Demo")]
    [TestCase("runtime", "de_mo")]
    public void OfflineEvaluationRejectsPrefixesOutsideTheOwnedNamingContract(string template, string prefix)
    {
        var result = SnapshotAttempt(template, template == "runtime" ? RuntimeParameters() : "", prefix: prefix);
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("namePrefix must"));
    }
}
