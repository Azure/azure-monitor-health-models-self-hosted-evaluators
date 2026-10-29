using System.Net.Http.Json;

namespace Checkout.Demo;

public static class DemoCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] == "api")
        {
            await using var app = CheckoutApplication.Create(args.Length == 0 ? [] : args[1..]);
            await app.RunAsync(ct);
            return 0;
        }
        var traffic = args is ["traffic"];
        var fault = args is ["fault", "healthy" or "slow" or "very-slow" or "errors"];
        if (!traffic && !fault)
        {
            Console.Error.WriteLine("Usage: Checkout.Demo.dll [api|traffic|fault healthy|slow|very-slow|errors]");
            return 2;
        }

        TrafficOptions? options = null;
        Uri endpoint;
        try
        {
            var url = Environment.GetEnvironmentVariable("DEMO_API_URL");
            if (traffic)
                options = TrafficOptions.Parse(Environment.GetEnvironmentVariable("DEMO_INTERVAL_SECONDS"), url);
            endpoint = options?.ApiUrl ?? DemoEndpoint.Parse(url);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(3)
        };
        if (traffic)
            return await RunTrafficAsync(client, options!, ct);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/fault")
            {
                Content = JsonContent.Create(new { mode = args[1] })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"DEMO_FAULT:{args[1]}");
            return 0;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"Internal HTTP command failed: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Internal HTTP command timed out or was cancelled.");
            return 1;
        }
    }

    private static async Task<int> RunTrafficAsync(HttpClient client, TrafficOptions options, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        try
        {
            await host.StartAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return 0;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct,
            lifetime.ApplicationStopping);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Checkout.Demo.Traffic");
        logger.LogInformation("Sending serialized demo traffic every {IntervalSeconds} seconds after completion.",
            options.Interval.TotalSeconds);
        try
        {
            await TrafficRunner.RunAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/checkout")
                {
                    Content = JsonContent.Create(new { productId = "demo-item", quantity = 1 })
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
            }, options.Interval, TimeProvider.System, stop.Token, logger);
            return 0;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
