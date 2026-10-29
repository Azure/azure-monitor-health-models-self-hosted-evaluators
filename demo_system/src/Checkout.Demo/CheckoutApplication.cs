using System.Text.Json;

namespace Checkout.Demo;

public static class CheckoutApplication
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://0.0.0.0:8080");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CheckoutMetrics>();
        builder.Services.AddSingleton<FaultState>();
        var app = builder.Build();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ready" }));
        app.MapGet("/metrics", async (HttpContext context, CheckoutMetrics metrics) =>
        {
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await metrics.ExportAsync(context.Response.Body, context.RequestAborted);
        });
        app.MapPost("/checkout", CheckoutAsync);
        app.MapPost("/admin/fault", SetFaultAsync);
        return app;
    }

    private static async Task<IResult> CheckoutAsync(
        HttpContext context, CheckoutMetrics metrics, TimeProvider time, FaultState faults)
    {
        var start = time.GetTimestamp();
        metrics.RecordReceived();
        CheckoutRequest? request;
        try
        {
            request = await BoundedJson.ReadAsync<CheckoutRequest>(context.Request, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Complete(Results.BadRequest(new { error = "A bounded JSON checkout request is required." }), 400);
        }
        catch (BadHttpRequestException)
        {
            return Complete(Results.BadRequest(new { error = "The checkout request could not be read." }), 400);
        }
        if (request is null || string.IsNullOrWhiteSpace(request.ProductId) ||
            request.ProductId.Length > 100 || request.Quantity is < 1 or > 100)
            return Complete(Results.BadRequest(new { error = "productId must be 1-100 characters and quantity 1-100." }), 400);

        var mode = faults.Mode;
        if (mode == CheckoutFault.Errors)
            return Complete(Results.Json(new { error = "Synthetic checkout failure." }, statusCode: 500), 500);
        var milliseconds = mode switch
        {
            CheckoutFault.Slow => 750,
            CheckoutFault.VerySlow => 1500,
            _ => 50
        };
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), time, context.RequestAborted);
        return Complete(Results.Ok(new
        {
            orderId = Guid.NewGuid(), productId = request.ProductId, quantity = request.Quantity, status = "confirmed"
        }), 200);

        IResult Complete(IResult result, int status)
        {
            metrics.RecordCompleted(status, time.GetElapsedTime(start));
            return result;
        }
    }

    private sealed record CheckoutRequest(string? ProductId, int Quantity);

    private static async Task<IResult> SetFaultAsync(HttpContext context, FaultState faults)
    {
        FaultRequest? request;
        try
        {
            request = await BoundedJson.ReadAsync<FaultRequest>(context.Request, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "A bounded JSON fault request is required." });
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "The fault request could not be read." });
        }
        return request is not null && faults.TrySet(request.Mode)
            ? Results.Ok(new { mode = request.Mode })
            : Results.BadRequest(new { error = "mode must be healthy, slow, very-slow, or errors." });
    }

    private sealed record FaultRequest(string? Mode);
}
