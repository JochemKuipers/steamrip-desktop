using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Hosters;

/// <summary>Tries a post's mirrors in preference order and returns the first that resolves; collects the failures so the UI can show why none worked.</summary>
public sealed class ResolverChain(
    IEnumerable<IHostResolver> resolvers,
    IReadOnlyList<Hoster>? order = null,
    ILogger<ResolverChain>? logger = null
)
{
    public static readonly IReadOnlyList<Hoster> DefaultOrder =
    [
        Hoster.FileDitch,
        Hoster.GoFile,
        Hoster.MegaDb,
        Hoster.BuzzHeavier,
    ];

    private readonly Dictionary<Hoster, IHostResolver> _byHost = resolvers.ToDictionary(r =>
        r.Host
    );
    private readonly IReadOnlyList<Hoster> _order = order ?? DefaultOrder;
    private readonly ILogger _log = logger ?? NullLogger<ResolverChain>.Instance;

    public IReadOnlyCollection<Hoster> Supported => _byHost.Keys;

    public async Task<(ResolvedDownload? Result, IReadOnlyList<string> Errors)> ResolveAsync(
        IReadOnlyList<Mirror> mirrors,
        CancellationToken ct = default
    )
    {
        var errors = new List<string>();
        _log.LogInformation(
            "Resolving: {Count} mirror(s) [{Hosts}], order [{Order}]",
            mirrors.Count,
            string.Join(", ", mirrors.Select(m => m.Host)),
            string.Join(" → ", _order)
        );
        foreach (var host in _order)
        {
            if (!_byHost.TryGetValue(host, out var resolver))
                continue;
            foreach (var m in mirrors.Where(m => m.Host == host))
            {
                ct.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _log.LogInformation("Trying {Host}: {Url}", host, m.Url);
                try
                {
                    var r = await resolver.ResolveAsync(m.Url, ct);
                    _log.LogInformation(
                        "{Host} resolved in {Seconds:0.0}s",
                        host,
                        sw.Elapsed.TotalSeconds
                    );
                    return (r, errors);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    errors.Add($"{host}: {e.Message}");
                    _log.LogWarning(
                        e,
                        "{Host} failed after {Seconds:0.0}s: {Error}",
                        host,
                        sw.Elapsed.TotalSeconds,
                        e.Message
                    );
                }
            }
        }
        if (errors.Count == 0)
            errors.Add(
                "No supported mirror on this post ("
                    + string.Join(", ", mirrors.Select(m => m.Host).Distinct())
                    + ")"
            );
        _log.LogError("No mirror resolved: {Errors}", string.Join(" | ", errors));
        return (null, errors);
    }
}
