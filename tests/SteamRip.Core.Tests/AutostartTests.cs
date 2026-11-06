using SteamRip.Core.Platform;

namespace SteamRip.Core.Tests;

public class AutostartTests
{
    [Fact]
    public void ExecutablePath_prefers_the_AppImage_file_over_the_mounted_process_path()
    {
        var before = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            Environment.SetEnvironmentVariable(
                "APPIMAGE",
                "/home/me/Apps/steamrip-desktop.AppImage"
            );
            Assert.Equal("/home/me/Apps/steamrip-desktop.AppImage", Autostart.ExecutablePath);

            Environment.SetEnvironmentVariable("APPIMAGE", null);
            Assert.Equal(Environment.ProcessPath, Autostart.ExecutablePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", before);
        }
    }
}
