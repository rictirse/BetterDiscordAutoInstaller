using System.Diagnostics;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Platform;

namespace BetterDiscordAutoInstaller.Tests.TestSupport;

/// <summary>
/// 測試用的平台：BetterDiscord 資料夾指向測試的暫存目錄，host 結構和 Windows 相同（app-x\resources、app-x\modules）。
/// 測試不該用到的成員一律丟 NotSupportedException，避免不小心碰到真實的 %AppData%、登錄檔或 process。
/// </summary>
internal sealed class FakePlatformService(string betterDiscordFolder) : IPlatformService
{
    public string BetterDiscordFolder { get; } = betterDiscordFolder;

    public string GetResourcesPath(string hostPath) => Path.Combine(hostPath, "resources");

    public string? FindDesktopCoreDir(string hostPath) => DiscordHostLayout.FindDesktopCoreDir(hostPath);

    public string Name => throw new NotSupportedException();
    public bool CanManageDiscordUpdates => throw new NotSupportedException();
    public bool CanInjectBetterDiscord => throw new NotSupportedException();
    public string GetDefaultDiscordPath(DiscordEdition edition) => throw new NotSupportedException();
    public string GetHostPath(string installPath) => throw new NotSupportedException();
    public string GetHostVersionLabel(string installPath) => throw new NotSupportedException();
    public Version? GetInstalledVersion(string installPath) => throw new NotSupportedException();
    public string GetProcessName(DiscordEdition edition) => throw new NotSupportedException();
    public bool StartDiscord(DiscordEdition edition, string installPath) => throw new NotSupportedException();
    public bool RequestGracefulExit(Process process) => throw new NotSupportedException();
    public bool IsDiscordAutoStartEnabled(DiscordEdition edition) => throw new NotSupportedException();
    public bool StartDiscordLikeAutoStart(DiscordEdition edition, string installPath) => throw new NotSupportedException();
    public string? GetUpdaterBusyReason(DiscordEdition edition, string installPath) => throw new NotSupportedException();
    public Task<bool> DownloadAndInstallDiscordAsync(HttpClient http) => throw new NotSupportedException();
    public string EnableAutostartMenuText => throw new NotSupportedException();
    public string DisableAutostartMenuText => throw new NotSupportedException();
    public void EnableAutostart() => throw new NotSupportedException();
    public void DisableAutostart() => throw new NotSupportedException();
    public void PrepareWorkingDirectory() => throw new NotSupportedException();
    public string? RunLogPath => throw new NotSupportedException();
}
