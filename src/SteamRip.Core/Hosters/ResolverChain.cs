using SteamRip.Core.Catalog;

namespace SteamRip.Core.Hosters;

/// <summary>Tries a post's mirrors in preference order and returns the first that resolves; collects the failures so the UI can show why none worked.</summary>
public sealed class ResolverChain(IEnumerable<IHostResolver> resolvers, IReadOnlyList<Hoster>? order = null)
{
    public static readonly IReadOnlyList<Hoster> DefaultOrder = [Hoster.FileDitch, Hoster.GoFile, Hoster.MegaDb, Hoster.BuzzHeavier];

    private readonly Dictionary<Hoster, IHostResolver> _byHost = resolvers.ToDictionary(r => r.Host);
    private readonly IReadOnlyList<Hoster> _order = order ?? DefaultOrder;

    public IReadOnlyCollection<Hoster> Supported => _byHost.Keys;

    public async Task<(ResolvedDownload? Result, IReadOnlyList<string> Errors)> ResolveAsync(IReadOnlyList<Mirror> mirrors, Action<string>? log = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var host in _order)
        {
            if (!_byHost.TryGetValue(host, out var resolver)) continue;
            foreach (var m in mirrors.Where(m => m.Host == host))
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"Resolving {host} mirror…");
                try { return (await resolver.ResolveAsync(m.Url, ct), errors); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { errors.Add($"{host}: {e.Message}"); log?.Invoke($"{host} failed: {e.Message}"); }
            }
        }
        if (errors.Count == 0) errors.Add("No supported mirror on this post (" + string.Join(", ", mirrors.Select(m => m.Host).Distinct()) + ")");
        return (null, errors);
    }
}
