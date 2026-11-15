using System.Diagnostics;
using BetterDiscordAutoInstaller.Models;

namespace BetterDiscordAutoInstaller.Platform;

/// <summary>
/// 所有和作業系統有關的部分（路徑、啟動 / 關閉 Discord、updater 狀態、開機自動執行）。
/// 共用流程（Program / DiscordService / BetterDiscordService）只透過這個介面接觸平台差異。
/// </summary>
public interface IPlatformService
{
    /// <summary>顯示用名稱，例如 "Windows"、"macOS"</summary>
    string Name { get; }

    /// <summary>
    /// 本工具是否負責 Discord 本體的更新（版本比對、還原注入後交給 Discord updater）。
    /// macOS 由 Discord 自己的 ShipIt 更新整個 .app，本工具不介入。
    /// </summary>
    bool CanManageDiscordUpdates { get; }

    /// <summary>本工具是否自己注入 BetterDiscord（macOS 交給 BetterDiscord 自己處理，見 MacPlatformService 說明）</summary>
    bool CanInjectBetterDiscord { get; }

    // ── 路徑 ──

    /// <summary>BetterDiscord 資料夾（底下有 data\betterdiscord.asar、plugins 等）</summary>
    string BetterDiscordFolder { get; }

    /// <summary>沒有在設定檔指定路徑時，Discord 的預設安裝位置</summary>
    string GetDefaultDiscordPath(DiscordEdition edition);

    /// <summary>目前使用中的 Discord host（Windows：app-x.x.x 資料夾；macOS：.app bundle）。找不到時丟出例外。</summary>
    string GetHostPath(string installPath);

    /// <summary>目前 host 的版本標記，記錄到設定檔用（Windows："app-1.0.9xxx"；macOS："0.0.xxx"）</summary>
    string GetHostVersionLabel(string installPath);

    /// <summary>目前 host 的版本號，用來和線上版本比對</summary>
    Version? GetInstalledVersion(string installPath);

    /// <summary>host 的 resources 資料夾（app.asar 所在位置）</summary>
    string GetResourcesPath(string hostPath);

    /// <summary>discord_desktop_core 模組資料夾（index.js / core.asar 所在位置）；不支援時回傳 null</summary>
    string? FindDesktopCoreDir(string hostPath);

    // ── process ──

    /// <summary>Discord 主程式的 process 名稱</summary>
    string GetProcessName(DiscordEdition edition);

    /// <summary>啟動 Discord。失敗時只回傳 false，不丟例外</summary>
    bool StartDiscord(DiscordEdition edition, string installPath);

    /// <summary>請 process 正常結束（例如 SIGTERM）。回傳 false 表示平台不支援，呼叫端會直接強制結束</summary>
    bool RequestGracefulExit(Process process);

    // ── Discord 自己的開機 / 登入自動啟動 ──

    /// <summary>Discord 是否設定為開機 / 登入時自動啟動（被使用者停用也算沒有）；無法判斷的平台回傳 false</summary>
    bool IsDiscordAutoStartEnabled(DiscordEdition edition);

    /// <summary>用與開機自動啟動相同的方式啟動 Discord（保留「啟動時最小化」等設定）。失敗時只回傳 false，不丟例外</summary>
    bool StartDiscordLikeAutoStart(DiscordEdition edition, string installPath);

    // ── updater ──

    /// <summary>Discord 內建 updater 忙碌的原因；閒置時回傳 null</summary>
    string? GetUpdaterBusyReason(DiscordEdition edition, string installPath);

    // ── 安裝 Discord ──

    /// <summary>下載並安裝 Discord。回傳 false 表示無法自動安裝（已提示使用者）</summary>
    Task<bool> DownloadAndInstallDiscordAsync(HttpClient http);

    // ── 開機 / 登入時自動執行 ──

    /// <summary>選單第 1、2 項的文字</summary>
    string EnableAutostartMenuText { get; }
    string DisableAutostartMenuText { get; }

    void EnableAutostart();
    void DisableAutostart();

    /// <summary>程式啟動時調整工作目錄（settings.json 以相對路徑存放）</summary>
    void PrepareWorkingDirectory();
}
