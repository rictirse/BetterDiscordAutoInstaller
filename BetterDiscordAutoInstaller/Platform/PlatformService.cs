namespace BetterDiscordAutoInstaller.Platform;

/// <summary>
/// 依執行中的作業系統選擇 IPlatformService 實作
/// </summary>
public static class PlatformService
{
    private static IPlatformService? _current;

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static IPlatformService Current => _current ??= Create();

    private static IPlatformService Create()
    {
        if (OperatingSystem.IsMacOS()) return new MacPlatformService();
        if (OperatingSystem.IsWindows()) return new WindowsPlatformService();
        throw new PlatformNotSupportedException("BetterDiscordAutoInstaller only supports Windows and macOS.");
    }
}
