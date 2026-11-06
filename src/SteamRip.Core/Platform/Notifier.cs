using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Platform;

public interface INotifier
{
    Task NotifyAsync(string title, string body);
}

/// <summary>Linux: org.freedesktop.Notifications via gdbus/notify-send. Windows: PowerShell toast (no WinRT dependency). Fallback: no-op.</summary>
public sealed class Notifier(ILogger<Notifier>? logger = null) : INotifier
{
    private readonly ILogger _log = logger ?? NullLogger<Notifier>.Instance;

    public async Task NotifyAsync(string title, string body)
    {
        _log.LogInformation("Notify: {Title} — {Body}", title, body.Replace('\n', ' '));
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Install.ArchiveTool.Find("gdbus") is { } gdbus)
                    await RunAsync(gdbus, "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
                        "--method", "org.freedesktop.Notifications.Notify", "SteamRIP", "0", "", title, body, "[]", "{}", "8000");
                else if (Install.ArchiveTool.Find("notify-send") is { } ns)
                    await RunAsync(ns, "-a", "SteamRIP", title, body);
            }
            else if (OperatingSystem.IsWindows())
            {
                var script = $$"""
                    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                    $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                    $t = $xml.GetElementsByTagName('text'); $t[0].AppendChild($xml.CreateTextNode('{{Esc(title)}}')) | Out-Null; $t[1].AppendChild($xml.CreateTextNode('{{Esc(body)}}')) | Out-Null
                    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('SteamRIP Desktop').Show([Windows.UI.Notifications.ToastNotification]::new($xml))
                    """;
                await RunAsync("powershell", "-NoProfile", "-NonInteractive", "-Command", script);
            }
        }
        catch (Exception e) { _log.LogWarning(e, "Notification failed"); }
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static async Task RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(); } catch { } }
    }
}
