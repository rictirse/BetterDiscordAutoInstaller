using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Platform;

/// <summary>
/// macOS：
///   - Discord 是 /Applications/Discord.app（或 ~/Applications/Discord.app），PTB / Canary 為 "Discord PTB.app" / "Discord Canary.app"
///   - Discord 的使用者資料在 ~/Library/Application Support/discord（discordptb / discordcanary）
///   - BetterDiscord 資料在 ~/Library/Application Support/BetterDiscord（與 BD 自己的 userData/.. 規則相同）
///   - 登入時自動執行用 ~/Library/LaunchAgents 底下的 LaunchAgent plist
///
/// 【為什麼 macOS 上本工具不自己注入 BetterDiscord】
/// BetterDiscord 1.14+ 在 macOS 的注入方式是修改 Discord.app/Contents/Resources：
/// 把 app.asar 改名為 betterdiscord.app.asar，再新增 app/index.js 先載入 BD
/// （BD 官方 Installer 與 BD 原始碼的 scripts/inject.ts 都是這樣做）。本工具不做這件事，原因：
///   1. Discord 在 macOS 用 ShipIt 整包替換 .app 來更新，每次更新都會把注入洗掉。
///      BD 1.14.6 的 betterdiscord.asar 已經自己處理：它 hook Discord updater 的 _updateMacOSHostVersion，
///      在 ShipIt 安裝新版後由一個 /bin/sh 腳本等 Gatekeeper 先接受「未修改」的新 bundle，
///      再關掉 Discord、patch、重開；Gatekeeper 若拒絕還會自動 revert。這段時機很難從外部可靠地重做。
///   2. Gatekeeper：新安裝的 bundle 若在第一次啟動被接受之前就被改過，macOS 會判定「已損毀」而拒絕開啟。
///   3. 「App 管理」權限（macOS 13+）：修改其他 App 的 bundle 需要「App 管理」或「完整磁碟取用權限」。
///      BD 的腳本是 Discord 的子 process，由 Discord 本身負責所以不受影響；
///      本工具從終端機或 LaunchAgent 執行時通常沒有這個權限，寫入會失敗（Operation not permitted）。
///   4. Windows 需要 RevertInjectionForUpdate 是因為 Discord 的 delta 更新會驗證舊檔 hash；
///      macOS 的 host 更新是整包替換，沒有這個問題，所以本工具也不需要主導 Discord 的更新。
/// 因此 macOS 上本工具只負責：保持 betterdiscord.asar 與 plugin 為最新版、偵測 BD 是否已注入
/// （沒有時提示使用者先執行一次官方 Installer），以及更新檔案時關閉並重新開啟 Discord。
///
/// 【尚未在實機 Mac 上驗證】
///   - Discord macOS 版 updater log 的實際檔名與內容：只用「log 最近有沒有寫入」判斷，不套用 Windows 的 marker
///   - Discord 新版 updater 在 macOS 的 modules 位置：因此 FindDesktopCoreDir 回傳 null，不做 discord_desktop_core 注入
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformService : IPlatformService
{
    private const string LaunchAgentLabel = "local.BetterDiscordAutoInstaller";

    // updater 在兩個動作之間最長會停約 3 秒，留一點餘裕（與 Windows 相同）
    private static readonly TimeSpan LogQuietPeriod = TimeSpan.FromSeconds(8);

    // macOS 的 updater log 格式尚未驗證，無法判斷 module 更新是否完成，只能讓 Discord 啟動後先跑一段時間
    private static readonly TimeSpan StartupGracePeriod = TimeSpan.FromSeconds(30);

    // 新版 host 暫存到 app-x.x.x 或寫入 ShipIt_request.json 之後，Discord 很快就會結束並交給 ShipIt 安裝
    private static readonly TimeSpan HostStagingWindow = TimeSpan.FromMinutes(2);

    // BD 的 ShipIt 腳本認為超過 10 分鐘的 lock 是殘留的（find -mmin +10）
    private static readonly TimeSpan BdMigrateLockMaxAge = TimeSpan.FromMinutes(10);

    private static readonly Lazy<string[]> TempDirs = new(FindTempDirs);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // .NET 在 macOS 的 SpecialFolder.ApplicationData 是 ~/.config，不是 Electron 用的 ~/Library/Application Support
    private static string ApplicationSupport => Path.Combine(Home, "Library", "Application Support");

    private static string LaunchAgentPath => Path.Combine(Home, "Library", "LaunchAgents", LaunchAgentLabel + ".plist");
    private static string AutostartLogPath => Path.Combine(Home, "Library", "Logs", "BetterDiscordAutoInstaller.log");

    public string Name => "macOS";
    public bool CanManageDiscordUpdates => false;
    public bool CanInjectBetterDiscord => false;

    public string BetterDiscordFolder => Path.Combine(ApplicationSupport, "BetterDiscord");

    private static string GetAppName(DiscordEdition edition) => edition switch
    {
        DiscordEdition.DiscordPTB => "Discord PTB",
        DiscordEdition.DiscordCanary => "Discord Canary",
        _ => "Discord"
    };

    private static string GetDefaultReleaseChannel(DiscordEdition edition) => edition switch
    {
        DiscordEdition.DiscordPTB => "ptb",
        DiscordEdition.DiscordCanary => "canary",
        _ => "stable"
    };

    // Electron 的 userData：~/Library/Application Support/discord、discordptb、discordcanary
    private static string GetDataFolder(DiscordEdition edition) => Path.Combine(ApplicationSupport, edition.ToString().ToLowerInvariant());

    /// <summary>
    /// 優先使用 /Applications，沒有時才找 ~/Applications（與 BD 官方 Installer 的搜尋順序相同）
    /// </summary>
    public string GetDefaultDiscordPath(DiscordEdition edition)
    {
        string bundle = GetAppName(edition) + ".app";
        string system = Path.Combine("/Applications", bundle);
        string user = Path.Combine(Home, "Applications", bundle);
        return !Directory.Exists(system) && Directory.Exists(user) ? user : system;
    }

    /// <summary>
    /// macOS 的 host 就是 .app bundle 本身。確認裡面有 Discord 的 app（原廠 app.asar，或被 BD 改名後的 betterdiscord.app.asar）
    /// </summary>
    public string GetHostPath(string installPath)
    {
        string resources = GetResourcesPath(installPath);
        if (!File.Exists(Path.Combine(resources, "app.asar")) && !File.Exists(Path.Combine(resources, "betterdiscord.app.asar")))
            throw new DirectoryNotFoundException($"No Discord app.asar found in: {resources}");

        return installPath;
    }

    public string GetHostVersionLabel(string installPath) => ReadBuildInfo(installPath).Version ?? "unknown";

    public Version? GetInstalledVersion(string installPath) =>
        Version.TryParse(ReadBuildInfo(installPath).Version, out var version) ? version : null;

    public string GetResourcesPath(string hostPath) => Path.Combine(hostPath, "Contents", "Resources");

    // macOS 不做 discord_desktop_core 注入（見類別說明）
    public string? FindDesktopCoreDir(string hostPath) => null;

    /// <summary>
    /// 讀取 Contents/Resources/build_info.json，例如 {"releaseChannel":"stable","version":"0.0.396"}
    /// </summary>
    private static (string? Version, string? ReleaseChannel) ReadBuildInfo(string bundlePath)
    {
        try
        {
            var path = Path.Combine(bundlePath, "Contents", "Resources", "build_info.json");
            if (!File.Exists(path)) return (null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? Get(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return (Get("version"), Get("releaseChannel"));
        }
        catch
        {
            return (null, null);
        }
    }

    // 主程式的 process 名稱與 .app 名稱相同（含空白），helper 是 "Discord Helper (...)"，不會被誤判
    public string GetProcessName(DiscordEdition edition) => GetAppName(edition);

    /// <summary>
    /// 用 open -a 啟動，與從 Finder / Dock 開啟相同（LaunchServices 會處理已在執行、Gatekeeper 等情況）
    /// </summary>
    public bool StartDiscord(DiscordEdition edition, string installPath)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(installPath);

            using var process = Process.Start(psi);
            if (process == null || !process.WaitForExit(15000) || process.ExitCode != 0)
            {
                Console.WriteLine($"[ERROR] Could not start {edition}: open -a \"{installPath}\" failed.");
                return false;
            }

            Console.WriteLine($"Started {edition}: open -a \"{installPath}\"");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Could not start {edition}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 先送 SIGTERM 讓 Discord（Electron）正常結束；沒在時間內結束時，呼叫端才會強制結束。
    /// BD 的 ShipIt 腳本也是同樣的作法（kill -TERM，等待後才 kill -KILL）
    /// </summary>
    public bool RequestGracefulExit(Process process)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
            psi.ArgumentList.Add("-TERM");
            psi.ArgumentList.Add(process.Id.ToString());

            using var kill = Process.Start(psi);
            return kill != null && kill.WaitForExit(5000) && kill.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 回傳 updater（或 BD 的 ShipIt 後處理）忙碌的原因；閒置時回傳 null
    /// </summary>
    public string? GetUpdaterBusyReason(DiscordEdition edition, string installPath)
    {
        var now = DateTime.Now;

        // 1. BD 的 ShipIt 腳本正在重新 patch 更新後的 Discord.app（它會自己關閉、patch、再開啟 Discord，期間不要插手）。
        //    即使 Discord 這時沒在執行也要等，因為腳本馬上會把它開起來
        string channel = ReadBuildInfo(installPath).ReleaseChannel ?? GetDefaultReleaseChannel(edition);
        foreach (var tmp in TempDirs.Value)
        {
            var lockDir = Path.Combine(tmp, $"betterdiscord-migrate-{channel}.lock");
            if (Directory.Exists(lockDir) && now - Directory.GetLastWriteTime(lockDir) < BdMigrateLockMaxAge)
                return "BetterDiscord is re-applying its injection after a Discord update";
        }

        // 2. updater log 還在持續寫入 → updater 正在工作
        string? logPath = FindUpdaterLog(edition);
        if (logPath != null && now - File.GetLastWriteTime(logPath) < LogQuietPeriod)
            return "updater is active";

        string processName = GetProcessName(edition);
        if (!ProcessUtils.IsRunning(processName)) return null;

        // 3. 新版 host 剛下載 / 暫存好（app-x.x.x/Discord.app、ShipIt_request.json），Discord 即將結束並交給 ShipIt 安裝
        string data = GetDataFolder(edition);
        if (Directory.Exists(data))
        {
            if (Directory.EnumerateDirectories(data, "app-*").Any(d => now - Directory.GetLastWriteTime(d) < HostStagingWindow))
                return "staging a new Discord version";

            var shipItRequest = Path.Combine(data, "ShipIt_request.json");
            if (File.Exists(shipItRequest) && now - File.GetLastWriteTime(shipItRequest) < HostStagingWindow)
                return "handing a Discord update to ShipIt";
        }

        // 4. Discord 啟動時會先檢查 / 安裝 module 更新；log 內容未驗證，至少讓它啟動一段時間再動它
        DateTime? processStart = ProcessUtils.GetEarliestStartTime(processName);
        if (processStart != null && now - processStart.Value < StartupGracePeriod) return "starting up";

        return null;
    }

    /// <summary>
    /// 找 userData/logs 底下最近寫入的 updater log（Windows 是 Discord_updater_rCURRENT.log，macOS 的檔名未驗證，用萬用字元比對）
    /// </summary>
    private static string? FindUpdaterLog(DiscordEdition edition)
    {
        try
        {
            var logs = new DirectoryInfo(Path.Combine(GetDataFolder(edition), "logs"));
            if (!logs.Exists) return null;

            return logs.EnumerateFiles("*updater*.log", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// BD 腳本的 lock 放在 Node 的 os.tmpdir()（也就是 $TMPDIR，通常是 /var/folders/.../T）。
    /// LaunchAgent 執行時不一定有 TMPDIR，所以也用 getconf 取得同一個使用者暫存資料夾
    /// </summary>
    private static string[] FindTempDirs()
    {
        var dirs = new List<string?>
        {
            Environment.GetEnvironmentVariable("TMPDIR"),
            RunAndReadLine("/usr/bin/getconf", "DARWIN_USER_TEMP_DIR"),
            Path.GetTempPath(),
            "/tmp"
        };

        return dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim().TrimEnd('/'))
            .Where(d => d.Length > 0)
            .Distinct()
            .ToArray();
    }

    private static string? RunAndReadLine(string file, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null) return null;
            string output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// macOS 的 Discord 是 dmg，需要使用者自己拖到「應用程式」資料夾，這裡只提示下載位置
    /// </summary>
    public Task<bool> DownloadAndInstallDiscordAsync(HttpClient http)
    {
        Console.WriteLine(Lang.T(
            "\n[提示] 找不到 Discord。請先到 https://discord.com/download 下載並把 Discord 拖到「應用程式」資料夾，開啟一次後再執行本工具。",
            "\n[Info] Discord was not found. Download it from https://discord.com/download, drag it to the Applications folder, open it once, then run this tool again."));
        return Task.FromResult(false);
    }

    public string EnableAutostartMenuText => Lang.T("1. 每次登入 macOS 的時候自動執行自動更新", "1. Enable auto-update at macOS login");
    public string DisableAutostartMenuText => Lang.T("2. 解除登入 macOS 時的自動更新", "2. Disable auto-update at macOS login");

    /// <summary>
    /// 在 ~/Library/LaunchAgents 建立帶有參數 3 的 LaunchAgent（RunAtLoad：每次登入時執行一次）
    /// </summary>
    public void EnableAutostart()
    {
        try
        {
            var command = GetLaunchCommand();
            if (command == null)
            {
                Console.WriteLine(Lang.T("無法取得當前程式路徑，設定失敗。", "Failed to retrieve current executable path."));
                return;
            }

            string workingDirectory = AppContext.BaseDirectory.TrimEnd('/');
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartLogPath)!);

            FileUtils.WriteAllTextAtomic(LaunchAgentPath,
                BuildLaunchAgentPlist(LaunchAgentLabel, [.. command, "3"], workingDirectory, AutostartLogPath));

            Console.WriteLine(Lang.T(
                $"\n[成功] 已建立 LaunchAgent，下次登入 macOS 時會自動執行自動更新 (帶入參數 3)！\n  {LaunchAgentPath}\n  執行紀錄：{AutostartLogPath}\n  macOS 可能會通知「已加入背景項目」，可在「系統設定 > 一般 > 登入項目」管理。",
                $"\n[Success] LaunchAgent created. Auto-update (with argument 3) will run each time you log in to macOS!\n  {LaunchAgentPath}\n  Log: {AutostartLogPath}\n  macOS may show a \"Background Items Added\" notification; manage it in System Settings > General > Login Items."));
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.T(
                $"\n[失敗] 設定登入自動執行時發生錯誤: {ex.Message}",
                $"\n[Failed] Error enabling login auto-update: {ex.Message}"));
        }
    }

    /// <summary>
    /// 移除 LaunchAgent。已載入的 job 只會在登入時執行一次，所以刪除檔案即可，下次登入就不會再執行
    /// </summary>
    public void DisableAutostart()
    {
        try
        {
            if (File.Exists(LaunchAgentPath))
            {
                File.Delete(LaunchAgentPath);
                Console.WriteLine(Lang.T(
                    "\n[成功] 已移除登入自動執行的 LaunchAgent！",
                    "\n[Success] Login LaunchAgent removed successfully!"));
            }
            else
            {
                Console.WriteLine(Lang.T(
                    "\n[提示] 尚未設定登入自動執行，無需移除。",
                    "\n[Info] No login LaunchAgent was found."));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.T(
                $"\n[失敗] 移除 LaunchAgent 時發生錯誤: {ex.Message}",
                $"\n[Failed] Error removing login LaunchAgent: {ex.Message}"));
        }
    }

    /// <summary>
    /// 從 Finder 雙擊或在其他資料夾用終端機執行時，工作目錄不是程式所在資料夾，
    /// settings.json 會和 LaunchAgent（WorkingDirectory 為程式所在資料夾）分成兩份，所以統一切到程式所在資料夾
    /// </summary>
    public void PrepareWorkingDirectory()
    {
        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not switch to the program folder: {ex.Message}");
        }
    }

    /// <summary>
    /// 自己這支程式的啟動指令。用 "dotnet BetterDiscordAutoInstaller.dll" 執行時 ProcessPath 是 dotnet，要把 dll 一起帶上
    /// </summary>
    private static string[]? GetLaunchCommand()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return null;

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (assemblyName == null) return null;
            return [processPath, Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll")];
        }

        return [processPath];
    }

    internal static string BuildLaunchAgentPlist(string label, IEnumerable<string> programArguments, string workingDirectory, string logPath)
    {
        static string E(string value) => SecurityElement.Escape(value);

        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            "<plist version=\"1.0\">",
            "<dict>",
            "\t<key>Label</key>",
            $"\t<string>{E(label)}</string>",
            "\t<key>ProgramArguments</key>",
            "\t<array>"
        };
        lines.AddRange(programArguments.Select(arg => $"\t\t<string>{E(arg)}</string>"));
        lines.AddRange(
        [
            "\t</array>",
            "\t<key>WorkingDirectory</key>",
            $"\t<string>{E(workingDirectory)}</string>",
            "\t<key>RunAtLoad</key>",
            "\t<true/>",
            // 只在圖形介面登入時執行（SSH 登入不需要開 Discord）
            "\t<key>LimitLoadToSessionType</key>",
            "\t<string>Aqua</string>",
            "\t<key>StandardOutPath</key>",
            $"\t<string>{E(logPath)}</string>",
            "\t<key>StandardErrorPath</key>",
            $"\t<string>{E(logPath)}</string>",
            "</dict>",
            "</plist>",
            ""
        ]);

        return string.Join("\n", lines);
    }
}
