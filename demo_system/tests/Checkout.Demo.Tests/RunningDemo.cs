using System.Collections.Concurrent;
using System.Diagnostics;

namespace Checkout.Demo.Tests;

internal sealed class RunningDemo : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly TaskCompletionSource<Uri> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Output => string.Join(Environment.NewLine, _lines);

    public RunningDemo(string[] args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CheckoutApplication).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("DEMO_", StringComparison.Ordinal) ||
                     key.StartsWith("ASPNETCORE_", StringComparison.Ordinal) ||
                     key.StartsWith("DOTNET_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["DOTNET_NOLOGO"] = "1";
        if (environment is not null)
            foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        _process = Process.Start(start)!;
        _stdout = ReadAsync(_process.StandardOutput);
        _stderr = ReadAsync(_process.StandardError);
    }

    public async Task<(int Code, string Output)> FinishAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(_stdout, _stderr);
        return (_process.ExitCode, Output);
    }

    public Task<Uri> AddressAsync() => _address.Task.WaitAsync(TimeSpan.FromSeconds(10));

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _lines.Enqueue(line);
            const string marker = "Now listening on: ";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(line[(index + marker.Length)..], UriKind.Absolute, out var address))
                _address.TrySetResult(address);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(_stdout, _stderr);
        _process.Dispose();
    }
}
