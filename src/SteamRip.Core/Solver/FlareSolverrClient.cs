using System.Net.Http.Json;
using System.Text.Json;

namespace SteamRip.Core.Solver;

public sealed record SolveResult(string Html, string UserAgent, IReadOnlyDictionary<string, string> Cookies, string FinalUrl, int Status, string Message);

/// <summary>Thin client for a FlareSolverr instance (tested against the smeinecke fork 3.9.x, which adds sessions.action/eval).</summary>
public sealed class FlareSolverrClient(HttpClient http, Func<string> baseUrl)
{
    public string BaseUrl => baseUrl().TrimEnd('/');

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var r = await http.GetAsync(BaseUrl + "/", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<JsonElement> CommandAsync(object body, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync(BaseUrl + "/v1", body, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public async Task<SolveResult> GetAsync(string url, string? session = null, int maxTimeoutMs = 90_000, bool disableMedia = false, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["cmd"] = "request.get", ["url"] = url, ["maxTimeout"] = maxTimeoutMs, ["disableMedia"] = disableMedia };
        if (session is not null) body["session"] = session;
        var root = await CommandAsync(body, ct);
        var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        if (!root.TryGetProperty("solution", out var sol) || sol.ValueKind != JsonValueKind.Object)
            throw new SolverException($"FlareSolverr: {msg}");
        var cookies = new Dictionary<string, string>();
        if (sol.TryGetProperty("cookies", out var cs))
            foreach (var c in cs.EnumerateArray())
                cookies[c.GetProperty("name").GetString()!] = c.GetProperty("value").GetString() ?? "";
        return new SolveResult(
            sol.TryGetProperty("response", out var resp) ? resp.GetString() ?? "" : "",
            sol.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? "" : "",
            cookies,
            sol.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            sol.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0,
            msg);
    }

    public async Task<Session> CreateSessionAsync(CancellationToken ct = default)
    {
        var id = "sr-" + Guid.NewGuid().ToString("N")[..8];
        await CommandAsync(new { cmd = "sessions.create", session = id }, ct);
        return new Session(this, id);
    }

    /// <summary>Runs browser actions in a session; returns <c>solution.evalResult</c> of the last eval as raw JSON text, or null.</summary>
    public async Task<string?> ActionAsync(string session, object[] actions, CancellationToken ct = default)
    {
        var root = await CommandAsync(new { cmd = "sessions.action", session, actions }, ct);
        if (root.TryGetProperty("status", out var s) && s.GetString() != "ok")
            throw new SolverException("FlareSolverr action failed: " + (root.TryGetProperty("message", out var m) ? m.GetString() : "?"));
        if (root.TryGetProperty("solution", out var sol) && sol.TryGetProperty("evalResult", out var er))
            return er.ValueKind == JsonValueKind.String ? er.GetString() : er.GetRawText();
        return null;
    }

    public sealed class Session(FlareSolverrClient client, string id) : IAsyncDisposable
    {
        public string Id => id;
        public Task<SolveResult> GetAsync(string url, bool disableMedia = false, CancellationToken ct = default) => client.GetAsync(url, id, disableMedia: disableMedia, ct: ct);
        public Task<string?> ActionAsync(object[] actions, CancellationToken ct = default) => client.ActionAsync(id, actions, ct);
        public async ValueTask DisposeAsync()
        {
            try { await client.CommandAsync(new { cmd = "sessions.destroy", session = id }); } catch { /* best effort */ }
        }
    }
}

public sealed class SolverException(string message) : Exception(message);
