using System.Text.Json;
using System.Text.Json.Serialization;

namespace Checkout.Demo;

internal static class BoundedJson
{
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.ContentLength > MaximumBytes)
            throw new JsonException("Expected a JSON body of at most 4096 bytes.");
        var buffer = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumBytes)
            throw new JsonException("JSON body exceeds 4096 bytes.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Options);
    }
}
