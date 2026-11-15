using BetterDiscordAutoInstaller.Models;
using Microsoft.Win32;

namespace BetterDiscordAutoInstaller.Utils;

/// <summary>
/// Discord 登錄的「開機自動啟動」指令，例如：
/// "C:\Users\xxx\AppData\Local\Discord\Update.exe" --processStart Discord.exe [--process-start-args --start-minimized]
/// </summary>
public sealed record DiscordAutoStartEntry(string FileName, string Arguments)
{
    public bool StartMinimized => Arguments.Contains("--start-minimized", StringComparison.OrdinalIgnoreCase);
}

public static class DiscordAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// 取得 Discord 的開機自動啟動設定；沒有設定或被使用者停用時回傳 null。
    /// Discord 自己寫在 HKCU\...\Run，值名稱就是執行檔名稱（Discord / DiscordPTB / DiscordCanary）。
    /// </summary>
    public static DiscordAutoStartEntry? Find(DiscordEdition edition)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            string name = edition.ToString();

            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) return null;

            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            if (IsDisabled(approved?.GetValue(name) as byte[])) return null;

            return Parse(command);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在「工作管理員 > 啟動應用程式」停用時 Run 的值不會被刪掉，
    /// 而是 StartupApproved 裡同名值的第一個 byte 的 bit0 會被設為 1（02 = 啟用，03 = 停用）
    /// </summary>
    internal static bool IsDisabled(byte[]? startupApprovedState) =>
        startupApprovedState is { Length: > 0 } && (startupApprovedState[0] & 1) == 1;

    /// <summary>
    /// 將登錄中的指令拆成執行檔路徑與參數
    /// </summary>
    public static DiscordAutoStartEntry? Parse(string command)
    {
        command = command.Trim();
        string file, args;

        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end < 0) return null;
            file = command[1..end];
            args = command[(end + 1)..].Trim();
        }
        else
        {
            int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd < 0) return null;
            file = command[..(exeEnd + 4)];
            args = command[(exeEnd + 4)..].Trim();
        }

        return string.IsNullOrWhiteSpace(file) ? null : new DiscordAutoStartEntry(Environment.ExpandEnvironmentVariables(file), args);
    }
}
