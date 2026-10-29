using System.Text.Json;
using static Checkout.Demo.Tests.InfrastructureTools;

namespace Checkout.Demo.Tests;

[TestFixture, Parallelizable(ParallelScope.All)]
public sealed class MainInfrastructureTests
{
    [Test]
    public void FoundationCreatesOnlyDemoOwnedResourcesInTheSelectedGroup()
    {
        var template = Compile("main");
        Assert.That(template.GetProperty("$schema").GetString(), Does.Contain("deploymentTemplate.json"));
        Assert.That(template.GetProperty("parameters").EnumerateObject().Select(p => p.Name),
            Is.EquivalentTo(new[] { "location", "namePrefix" }));
        Assert.That(template.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("type").GetString()),
            Is.EquivalentTo(new[]
            {
                "Microsoft.ContainerRegistry/registries", "Microsoft.ManagedIdentity/userAssignedIdentities",
                "Microsoft.Monitor/accounts", "Microsoft.Insights/dataCollectionEndpoints",
                "Microsoft.Insights/dataCollectionRules", "Microsoft.Authorization/roleAssignments",
                "Microsoft.Authorization/roleAssignments", "Microsoft.CloudHealth/healthmodels",
                "Microsoft.CloudHealth/healthmodels/entities", "Microsoft.CloudHealth/healthmodels/entities",
                "Microsoft.CloudHealth/healthmodels/entities", "Microsoft.CloudHealth/healthmodels/relationships", 
                "Microsoft.CloudHealth/healthmodels/relationships", "Microsoft.CloudHealth/healthmodels/relationships"
            }));
        foreach (var resource in template.GetProperty("resources").EnumerateArray())
        {
            if (resource.GetProperty("type").GetString() == "Microsoft.Authorization/roleAssignments") continue;
            var tags = resource.GetProperty("type").GetString()!.Count(c => c == '/') == 1
                ? resource.GetProperty("tags")
                : resource.GetProperty("properties").GetProperty("tags");
            Assert.That(ResolveVariable(template, tags).GetProperty("she-demo-system").GetString(), Is.EqualTo("[variables('demoOwner')]"));
            Assert.That(resource.TryGetProperty("scope", out _), Is.False);
        }
        Assert.That(template.GetProperty("variables").GetProperty("demoOwner").GetString(),
            Is.EqualTo("[uniqueString(resourceGroup().id, parameters('namePrefix'))]"));
    }

    [Test]
    public void RegistryAndWorkspaceUsePublicAuthenticatedEndpointsWithoutSharedKeys()
    {
        var template = Compile("main");
        var registry = Resource(template, "Microsoft.ContainerRegistry/registries");
        Assert.That(registry.GetProperty("sku").GetProperty("name").GetString(), Is.EqualTo("Basic"));
        var properties = registry.GetProperty("properties");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(properties.GetProperty("adminUserEnabled").GetBoolean(), Is.False);
            Assert.That(properties.GetProperty("publicNetworkAccess").GetString(), Is.EqualTo("Enabled"));
            Assert.That(properties.GetProperty("anonymousPullEnabled").GetBoolean(), Is.False);
            Assert.That(properties.GetProperty("roleAssignmentMode").GetString(), Is.EqualTo("LegacyRegistryPermissions"));
        }
        Assert.That(Resource(template, "Microsoft.Monitor/accounts").GetProperty("properties")
            .GetProperty("publicNetworkAccess").GetString(), Is.EqualTo("Enabled"));
    }

    [Test]
    public void CustomCollectionRouteTargetsOnlyTheDemoMonitorWorkspace()
    {
        var template = Compile("main");
        var dce = Resource(template, "Microsoft.Insights/dataCollectionEndpoints");
        Assert.That(dce.GetProperty("properties").GetProperty("networkAcls").GetProperty("publicNetworkAccess").GetString(),
            Is.EqualTo("Enabled"));
        var dcr = Resource(template, "Microsoft.Insights/dataCollectionRules").GetProperty("properties");
        Assert.That(dcr.GetProperty("dataCollectionEndpointId").GetString(), Does.Contain("Microsoft.Insights/dataCollectionEndpoints"));
        Assert.That(dcr.TryGetProperty("dataSources", out var sources), Is.True,
            "Azure requires a nonempty data source for the Linux Prometheus collection rule.");
        var forwarder = sources.GetProperty("prometheusForwarder").EnumerateArray().Single();
        Assert.That(forwarder.GetProperty("streams").EnumerateArray().Select(s => s.GetString()),
            Is.EqualTo(new[] { "Microsoft-PrometheusMetrics" }));
        Assert.That(forwarder.GetProperty("name").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(forwarder.GetProperty("labelIncludeFilter").EnumerateObject(), Is.Empty,
            "The remote-write route must accept all of the demo's retained metrics.");
        var destination = dcr.GetProperty("destinations").GetProperty("monitoringAccounts").EnumerateArray().Single();
        Assert.That(destination.GetProperty("accountResourceId").GetString(), Does.Contain("Microsoft.Monitor/accounts"));
        var flow = dcr.GetProperty("dataFlows").EnumerateArray().Single();
        Assert.That(flow.GetProperty("streams").EnumerateArray().Select(s => s.GetString()),
            Is.EqualTo(new[] { "Microsoft-PrometheusMetrics" }));
        Assert.That(flow.GetProperty("destinations").EnumerateArray().Single().GetString(),
            Is.EqualTo(destination.GetProperty("name").GetString()));
    }

    [Test]
    public void RuntimeIdentityReceivesOnlyRegistryPullAndCustomRulePublisherRoles()
    {
        var template = Compile("main");
        var roles = Resources(template, "Microsoft.Authorization/roleAssignments");
        Assert.That(roles, Has.Length.EqualTo(2));
        foreach (var role in roles)
        {
            var properties = role.GetProperty("properties");
            Assert.That(properties.GetProperty("principalType").GetString(), Is.EqualTo("ServicePrincipal"));
            Assert.That(properties.GetProperty("principalId").GetString(), Does.Contain("Microsoft.ManagedIdentity/userAssignedIdentities"));
        }
        var pull = roles.Single(r => ResolveVariable(template, r.GetProperty("properties").GetProperty("roleDefinitionId")).GetString()!
            .Contains("7f951dda-4ed3-4680-a7ca-43fe172d538d", StringComparison.Ordinal));
        var publisher = roles.Single(r => ResolveVariable(template, r.GetProperty("properties").GetProperty("roleDefinitionId")).GetString()!
            .Contains("3913510d-42f4-4e42-8a64-420c390055eb", StringComparison.Ordinal));
        Assert.That(pull.GetProperty("scope").GetString(), Does.Contain("Microsoft.ContainerRegistry/registries"));
        Assert.That(publisher.GetProperty("scope").GetString(), Does.Contain("Microsoft.Insights/dataCollectionRules"));
    }

    [Test]
    public void HealthModelPredeclaresExternalSignalMembersAndWorstOfDependencies()
    {
        var template = Compile("main");
        Assert.That(Resource(template, "Microsoft.CloudHealth/healthmodels").GetProperty("apiVersion").GetString(),
            Is.EqualTo("2026-10-01-preview"));
        var entities = Resources(template, "Microsoft.CloudHealth/healthmodels/entities");
        JsonElement Entity(string name) => entities.Single(e => e.GetProperty("name").GetString()!.EndsWith($", '{name}')]", StringComparison.Ordinal))
            .GetProperty("properties");
        var parent = Entity("checkout");
        Assert.That(parent.GetProperty("signalGroups").GetProperty("dependencies").GetProperty("aggregationType").GetString(), Is.EqualTo("WorstOf"));
        foreach (var entity in entities)
        {
            var properties = entity.GetProperty("properties");
            if (properties.TryGetProperty("signalGroups", out var groups))
                Assert.That(groups.TryGetProperty("external", out _), Is.False, "External signals are report-created/read-only in ARM.");
        }
        foreach (var (entity, members) in new[]
        {
            ("checkout-api", new[] { "p95-latency", "checkout-error-rate", "availability" }),
            ("checkout-traffic", new[] { "traffic-freshness" })
        })
        {
            var group = Entity(entity).GetProperty("signalAggregationGroups").EnumerateArray().Single();
            Assert.That(group.GetProperty("aggregationType").GetString(), Is.EqualTo("WorstOf"));
            Assert.That(group.GetProperty("members").EnumerateArray().Select(m => m.GetString()), Is.EquivalentTo(members));
        }
        var relationships = Resources(template, "Microsoft.CloudHealth/healthmodels/relationships");
        var edges = relationships.Select(r => (
            Parent: r.GetProperty("properties").GetProperty("parentEntityName").GetString()!,
            Child: r.GetProperty("properties").GetProperty("childEntityName").GetString()!)).ToArray();
        Assert.That(edges, Has.Length.EqualTo(3));
        Assert.That(edges.Where(e => e.Parent == "checkout").Select(e => e.Child),
            Is.EquivalentTo(new[] { "checkout-api", "checkout-traffic" }));
        Assert.That(edges.Single(e => e.Child == "checkout").Parent, Does.Contain("hm-"),
            "The model root entity must aggregate the checkout entity.");
    }

    [Test]
    public void FoundationExportsResourceIdsAndServiceProvidedQueryAndIngestionEndpoints()
    {
        var outputs = Compile("main").GetProperty("outputs");
        Assert.That(outputs.EnumerateObject().Select(o => o.Name), Is.EquivalentTo(new[]
        {
            "owner", "registryName", "registryLoginServer", "registryResourceId", "identityName",
            "identityResourceId", "identityClientId", "identityPrincipalId", "workspaceResourceId",
            "workspaceQueryEndpoint", "healthModelResourceId", "dataCollectionRuleResourceId",
            "dataCollectionEndpointResourceId", "metricsIngestionEndpoint", "containerGroupName", "containerGroupResourceId"
        }));
        Assert.That(outputs.GetProperty("workspaceQueryEndpoint").GetProperty("value").GetString(),
            Does.Contain(".metrics.prometheusQueryEndpoint"));
        var ingestion = outputs.GetProperty("metricsIngestionEndpoint").GetProperty("value").GetString();
        Assert.That(ingestion, Does.Contain(".metricsIngestion.endpoint"));
        Assert.That(ingestion, Does.Contain(".immutableId"));
        Assert.That(ingestion, Does.Contain("/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24"));
    }

    [TestCase("main")]
    [TestCase("runtime")]
    public void TemplatesRequireLocationAndBoundTheLowercaseAlphanumericPrefix(string name)
    {
        var parameters = Compile(name).GetProperty("parameters");
        Assert.That(parameters.GetProperty("location").TryGetProperty("defaultValue", out _), Is.False);
        var prefix = parameters.GetProperty("namePrefix");
        Assert.That(prefix.TryGetProperty("defaultValue", out _), Is.False);
        Assert.That(prefix.GetProperty("minLength").GetInt32(), Is.EqualTo(3));
        Assert.That(prefix.GetProperty("maxLength").GetInt32(), Is.EqualTo(10));
    }
}
