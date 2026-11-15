using System.Globalization;
using System.Text;

namespace BetterDiscordAutoInstaller.Utils;

/// <summary>
/// 解析 Discord 新版 updater（updater.node）的 log。
/// 目前只在 Windows 驗證過 log 內容；macOS 版 log 的實際格式尚未確認，所以 macOS 不使用這裡的 marker。
/// </summary>
public static class UpdaterLog
{
    // Discord 新版 updater 的 log 是「累積」的（約 1MB 才輪替一次），會保留之前好幾次啟動的紀錄，
    // 所以判斷是否正在更新時只能看「本次啟動」那一段（最後一個 "Logging initialized" 之後）。
    private const string LogSessionStartMarker = "Logging initialized";
    private const string UpdateStartMarker = "Starting update to latest.";
    private const string UpdateCompleteMarker = "Update to latest complete.";
    private const string UpdateFailedMarker = "ERROR [updater_client]: Failed";
    private const string ModuleRequestMarker = "Requesting installation of module";
    private const string ModuleDoneMarker = "Install of module";
    private const string ModuleFailedMarker = "TaskFailed(ModuleInstall";

    /// <summary>
    /// 分析 updater log 的最後一段（本次啟動），判斷 updater 是否還在工作。
    /// </summary>
    internal static string? Analyze(string tail, DateTime? processStart, DateTime now)
    {
        bool recentlyStarted = processStart != null && now - processStart.Value < TimeSpan.FromMinutes(2);
        int sessionIdx = tail.LastIndexOf(LogSessionStartMarker, StringComparison.Ordinal);
        string session = sessionIdx >= 0 ? tail[sessionIdx..] : tail;

        // log 裡最後一次啟動早於目前的 Discord process → 這次啟動的 updater 還沒開始寫 log
        if (recentlyStarted && sessionIdx >= 0 &&
            TryParseLogLineTimestamp(tail, sessionIdx, out var sessionStart) &&
            sessionStart < processStart!.Value - TimeSpan.FromSeconds(15))
        {
            return "starting up";
        }

        int lastStart = session.LastIndexOf(UpdateStartMarker, StringComparison.Ordinal);
        int lastEnd = Math.Max(
            session.LastIndexOf(UpdateCompleteMarker, StringComparison.Ordinal),
            session.LastIndexOf(UpdateFailedMarker, StringComparison.Ordinal));
        if (lastStart > lastEnd) return "checking / installing updates";

        int requested = CountOccurrences(session, ModuleRequestMarker);
        int finished = CountOccurrences(session, ModuleDoneMarker) + CountOccurrences(session, ModuleFailedMarker);
        if (requested > finished) return "installing modules";

        return null;
    }

    public static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 解析 log 行首的時間，例如 "[2026-11-15 13:07:07.832607 +08:00] INFO ..."
    /// </summary>
    private static bool TryParseLogLineTimestamp(string text, int indexInLine, out DateTime localTime)
    {
        localTime = default;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, indexInLine - 1)) + 1;
        if (lineStart >= text.Length || text[lineStart] != '[') return false;
        int close = text.IndexOf(']', lineStart);
        if (close < 0) return false;

        if (DateTimeOffset.TryParseExact(text[(lineStart + 1)..close], "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            localTime = dto.LocalDateTime;
            return true;
        }
        return false;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }
}
