using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Checkout.Demo.Tests;

internal sealed class ApiFixture : IAsyncDisposable
{
    private readonly WebApplication _application;
    public HttpClient Client { get; }

    private ApiFixture(WebApplication application, Uri address)
    {
        _application = application;
        Client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
    }

    public static async Task<ApiFixture> StartAsync()
    {
        var app = CheckoutApplication.Create(
            ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"]);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        return new ApiFixture(app, new Uri(addresses.Addresses.Single()));
    }

    public Task<HttpResponseMessage> CheckoutAsync() =>
        Client.PostAsJsonAsync("/checkout", new { productId = "demo-item", quantity = 1 });

    public Task<HttpResponseMessage> FaultAsync(string mode) =>
        Client.PostAsJsonAsync("/admin/fault", new { mode });

    public Task<string> MetricsAsync() => Client.GetStringAsync("/metrics");

    public static double Sample(string metrics, string series)
    {
        var line = metrics.Split('\n').Single(value => value.StartsWith(series + " ", StringComparison.Ordinal));
        return double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

internal sealed class ChunkedJsonContent(string value) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
}
