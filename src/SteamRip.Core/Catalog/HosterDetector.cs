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
        return h.Contains("bzzhr") || h.Contains("buzzheavier") || h.Contains("fuckingfast")
                ? Hoster.BuzzHeavier
            : h.Contains("fileditch") ? Hoster.FileDitch
            : h.Contains("gofile.io") ? Hoster.GoFile
            : h.Contains("megadb") ? Hoster.MegaDb
            : h.Contains("1fichier") ? Hoster.Fichier
            : h.Contains("filecrypt") ? Hoster.FileCrypt
            : Hoster.Unknown;
    }

    public static string Normalize(string url) => url.StartsWith("//") ? "https:" + url : url;
}
