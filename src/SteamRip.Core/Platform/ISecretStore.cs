using System.Diagnostics;
using System.Text;

namespace SteamRip.Core.Platform;

/// <summary>Keychain shim. Linux: Secret Service via <c>secret-tool</c>; Windows: DPAPI-protected file; fallback: 0600 file in the config dir.</summary>
public interface ISecretStore
{
    string? Read(string key);
    void Write(string key, string? value);
    string Backend { get; }
}

public static class SecretStore
{
    public static ISecretStore Create(string configDir)
    {
        if (OperatingSystem.IsLinux() && Install.ArchiveTool.Find("secret-tool") is { } st)
            return new SecretToolStore(st, new FileSecretStore(configDir));
        if (OperatingSystem.IsWindows())
            return new DpapiFileStore(configDir);
        return new FileSecretStore(configDir);
    }
}

internal sealed class SecretToolStore(string exe, ISecretStore fallback) : ISecretStore
{
    public string Backend => "Secret Service (secret-tool)";

    public string? Read(string key)
    {
        var (code, output) = Run("lookup", "application", "steamrip-desktop", "key", key);
        if (code == 0 && output.Length > 0)
            return output.TrimEnd('\n');
        return fallback.Read(key);
    }

    public void Write(string key, string? value)
    {
        if (value is null)
        {
            Run("clear", "application", "steamrip-desktop", "key", key);
            fallback.Write(key, null);
            return;
        }
        var psi = Psi(
            "store",
            "--label=SteamRIP Desktop: " + key,
            "application",
            "steamrip-desktop",
            "key",
            key
        );
        psi.RedirectStandardInput = true;
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(value);
            p.StandardInput.Close();
            p.WaitForExit(5000);
            if (p.ExitCode == 0)
            {
                fallback.Write(key, null);
                return;
            }
        }
        catch { }
        fallback.Write(key, value);
    }

    private (int, string) Run(params string[] args)
    {
        try
        {
            using var p = Process.Start(Psi(args))!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, o);
        }
        catch
        {
            return (-1, "");
        }
    }

    private ProcessStartInfo Psi(params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }
}

internal sealed class FileSecretStore(string dir) : ISecretStore
{
    public string Backend => "file (mode 0600)";

    private string PathFor(string key) =>
        Path.Combine(dir, "secrets", Install.Installer.SafeName(key) + ".secret");

    public string? Read(string key) =>
        File.Exists(PathFor(key)) ? File.ReadAllText(PathFor(key)) : null;

    public void Write(string key, string? value)
    {
        var p = PathFor(key);
        if (value is null)
        {
            if (File.Exists(p))
                File.Delete(p);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, value);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

internal sealed class DpapiFileStore(string dir) : ISecretStore
{
    public string Backend => "Windows DPAPI";

    private string PathFor(string key) =>
        Path.Combine(dir, "secrets", Install.Installer.SafeName(key) + ".dpapi");

    public string? Read(string key)
    {
        if (!File.Exists(PathFor(key)) || !OperatingSystem.IsWindows())
            return null;
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
            File.ReadAllBytes(PathFor(key)),
            null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser
        );
        return Encoding.UTF8.GetString(bytes);
    }

    public void Write(string key, string? value)
    {
        var p = PathFor(key);
        if (value is null)
        {
            if (File.Exists(p))
                File.Delete(p);
            return;
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(
            p,
            System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser
            )
        );
    }
}
