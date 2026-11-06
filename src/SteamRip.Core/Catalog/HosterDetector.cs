namespace SteamRip.Core.Catalog;

public static class HosterDetector
{
    public static Hoster Detect(string url)
    {
        if (
            !Uri.TryCreate(
                url.StartsWith("//") ? "https:" + url : url,
                UriKind.Absolute,
                out var uri
            )
        )
            return Hoster.Unknown;
        var h = uri.Host.ToLowerInvariant();
        if (h.Contains("bzzhr") || h.Contains("buzzheavier") || h.Contains("fuckingfast"))
            return Hoster.BuzzHeavier;
        if (h.Contains("fileditch"))
            return Hoster.FileDitch;
        if (h.Contains("gofile.io"))
            return Hoster.GoFile;
        if (h.Contains("megadb"))
            return Hoster.MegaDb;
        if (h.Contains("1fichier"))
            return Hoster.Fichier;
        if (h.Contains("filecrypt"))
            return Hoster.FileCrypt;
        return Hoster.Unknown;
    }

    public static string Normalize(string url) => url.StartsWith("//") ? "https:" + url : url;
}
