using Microsoft.Extensions.Logging;
using SteamRip.Core.Logging;

namespace SteamRip.Core.Tests;

public class LoggingTests
{
    [Fact]
    public async Task Writes_daily_file_filters_by_level_and_fans_out_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-log-" + Guid.NewGuid().ToString("N"));
        var seen = new List<string>();
        using (var provider = new FileLoggerProvider(dir) { MinimumLevel = LogLevel.Information })
        {
            provider.LineWritten += seen.Add;
            var log = provider.CreateLogger("SteamRip.Core.Tests.Thing");
            log.LogDebug("hidden");
            log.LogInformation("hello {Who}", "world");
            log.LogError(new InvalidOperationException("boom"), "failed {N}", 3);
            provider.MinimumLevel = LogLevel.Debug;
            log.LogDebug("now visible");
        }
        var file = Directory.GetFiles(dir, "steamrip-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.Equal(Path.Combine(dir, $"steamrip-{DateTime.Now:yyyyMMdd}.log"), file);
        Assert.Contains("INF [Thing] hello world", text);
        Assert.Contains("ERR [Thing] failed 3", text);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("DBG [Thing] now visible", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Equal(3, seen.Count);
        Directory.Delete(dir, true);
    }
}
