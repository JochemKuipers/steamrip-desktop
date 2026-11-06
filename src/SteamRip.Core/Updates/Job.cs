using SteamRip.Core.Catalog;

namespace SteamRip.Core.Updates;

public enum JobStage
{
    Queued,
    Resolving,
    Downloading,
    Installing,
    Done,
    Failed,
    NeedsAttention,
    Cancelled,
}

/// <summary>One install/update unit of work. Mutable so the UI can bind to it; all mutations happen on the queue worker and are published via <see cref="Changed"/>.</summary>
public sealed class Job(CatalogPost post, bool isUpdate)
{
    public CatalogPost Post { get; } = post;
    public bool IsUpdate { get; } = isUpdate;
    public JobStage Stage { get; private set; } = JobStage.Queued;
    public string Status { get; private set; } = "Queued";
    public double? Fraction { get; private set; }
    public double BytesPerSecond { get; private set; }
    public long Bytes { get; private set; }
    public long? Total { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Set when the user solved a challenge by hand; used for the first download attempt instead of the resolver chain.</summary>
    public Hosters.ResolvedDownload? PreResolved { get; init; }

    public event Action<Job>? Changed;

    internal void Set(JobStage stage, string status, double? fraction = null, string? error = null)
    {
        Stage = stage;
        Status = status;
        Fraction = fraction;
        Error = error;
        if (stage is not JobStage.Downloading)
        {
            BytesPerSecond = 0;
        }
        Changed?.Invoke(this);
    }

    internal void Progress(Downloads.DownloadProgress p)
    {
        Bytes = p.Bytes;
        Total = p.Total;
        BytesPerSecond = p.BytesPerSecond;
        Fraction = p.Fraction;
        Status = p.Total is { } t
            ? $"Downloading {Fmt(p.Bytes)} / {Fmt(t)} ({Fmt((long)p.BytesPerSecond)}/s)"
            : $"Downloading {Fmt(p.Bytes)} ({Fmt((long)p.BytesPerSecond)}/s)";
        Changed?.Invoke(this);
    }

    public static string Fmt(long b) =>
        b switch
        {
            >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GiB",
            >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MiB",
            >= 1L << 10 => $"{b / (double)(1L << 10):0} KiB",
            _ => $"{b} B",
        };
}
