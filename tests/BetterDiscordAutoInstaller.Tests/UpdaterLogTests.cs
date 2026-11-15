using System.Globalization;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// UpdaterLog.Analyze：回傳 null = 閒置，非 null = 忙碌（字串是原因）。
/// log 的時間戳帶時區，Analyze 會轉成本地時間比較；測試的 processStart / now 一律從帶時區的 DateTimeOffset 轉 .LocalDateTime，
/// 所以不論跑測試的機器在哪個時區（CI 的 UTC、開發機的 +08:00）結果都一樣。
/// </summary>
public sealed class UpdaterLogTests
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff zzz";

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpdaterLog");

    // processStart = null、now = 最後一行的時間 + 10 秒
    private static readonly Dictionary<string, string?> ExpectedWithoutProcessStart = new()
    {
        ["idle-up-to-date.log"] = null,
        ["busy-host-download.log"] = "checking / installing updates",
        ["busy-module-install.log"] = "installing modules",
        ["idle-after-failure.log"] = null,
        ["multi-session.log"] = null, // 第一個 session 停在 "Starting update to latest."，只看最後一個 session 才會是閒置
    };

    public static TheoryData<string> FixtureFiles => [.. ExpectedWithoutProcessStart.Keys];

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixtureDir, fileName));

    /// <summary>檔案最後一行的時間戳（本地時間）</summary>
    private static DateTime LastLineTimestamp(string log)
    {
        var lastLine = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        var stamp = lastLine[1..lastLine.IndexOf(']')];
        return DateTimeOffset.ParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture).LocalDateTime;
    }

    // ── fixture ──

    [Fact]
    public void EveryFixtureFileHasAnExpectation()
    {
        var files = Directory.GetFiles(FixtureDir, "*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal);

        Assert.Equal(ExpectedWithoutProcessStart.Keys.Order(StringComparer.Ordinal), files);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Analyze_Fixture_WithoutProcessStart(string fileName)
    {
        var log = ReadFixture(fileName);
        var now = LastLineTimestamp(log).AddSeconds(10);

        Assert.Equal(ExpectedWithoutProcessStart[fileName], UpdaterLog.Analyze(log, processStart: null, now));
    }

    [Fact]
    public void Analyze_Fixture_NewProcessHasNotWrittenLogYet_ReturnsStartingUp()
    {
        var log = ReadFixture("idle-up-to-date.log");
        var processStart = new DateTimeOffset(2026, 1, 1, 19, 45, 38, TimeSpan.FromHours(8)).LocalDateTime.AddSeconds(60);
        var now = processStart.AddSeconds(5);

        Assert.Equal("starting up", UpdaterLog.Analyze(log, processStart, now));
    }

    // ── inline 邊界 ──

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 17, 23, 9, 832, TimeSpan.FromHours(8));

    /// <summary>格式同 Discord updater：[2026-01-01 17:23:09.832000 +08:00] INFO [updater]: ...</summary>
    private static string Line(int secondsAfterBase, string text) =>
        $"[{BaseTime.AddSeconds(secondsAfterBase).ToString(TimestampFormat, CultureInfo.InvariantCulture)}] {text}\n";

    private static DateTime Local(int secondsAfterBase) => BaseTime.AddSeconds(secondsAfterBase).LocalDateTime;

    private const string LoggingInitialized = "INFO [updater]: Logging initialized";
    private const string UpdateStart = "INFO [updater_client::install]: Starting update to latest.";
    private const string UpdateComplete = "INFO [updater_client::install]: Update to latest complete.";
    private const string UpdateFailed = "ERROR [updater_client]: Failed 26: TaskFailed(HostInstall(NotFound))";

    private static string ModuleRequested(string module) => $"INFO [updater_client::install]: Requesting installation of module {module}.";
    private static string ModuleDone(string module) => $"INFO [updater_client::install]: Install of module {module} finished successfully.";
    private static string ModuleFailed(string module) => $"ERROR [updater_client]: Failed 9: TaskFailed(ModuleInstall({module}))";

    [Fact]
    public void Analyze_EmptyLog_ReturnsNull()
    {
        Assert.Null(UpdaterLog.Analyze("", processStart: null, Local(0)));
    }

    [Fact]
    public void Analyze_WithoutLoggingInitialized_AnalyzesWholeText()
    {
        var busy = Line(0, UpdateStart);
        var idle = Line(0, UpdateStart) + Line(2, UpdateComplete);

        Assert.Equal("checking / installing updates", UpdaterLog.Analyze(busy, processStart: null, Local(10)));
        Assert.Null(UpdaterLog.Analyze(idle, processStart: null, Local(10)));
    }

    [Fact]
    public void Analyze_WithoutLoggingInitialized_DoesNotReportStartingUp()
    {
        // 沒有 session 起點可以和 process 啟動時間比較 → 只看內容
        var log = Line(0, UpdateStart) + Line(2, UpdateComplete);
        var processStart = Local(120);

        Assert.Null(UpdaterLog.Analyze(log, processStart, processStart.AddSeconds(5)));
    }

    [Theory]
    [InlineData(UpdateComplete)]
    [InlineData(UpdateFailed)]
    public void Analyze_UpdateStartFollowedByCompleteOrFailure_ReturnsNull(string endLine)
    {
        var log = Line(0, LoggingInitialized) + Line(1, UpdateStart) + Line(5, endLine);

        Assert.Null(UpdaterLog.Analyze(log, processStart: null, Local(15)));
    }

    [Theory]
    [InlineData(UpdateComplete)]
    [InlineData(UpdateFailed)]
    public void Analyze_NewUpdateStartAfterPreviousEnd_ReturnsBusy(string endLine)
    {
        var log = Line(0, LoggingInitialized) + Line(1, UpdateStart) + Line(5, endLine) + Line(30, UpdateStart);

        Assert.Equal("checking / installing updates", UpdaterLog.Analyze(log, processStart: null, Local(40)));
    }

    [Fact]
    public void Analyze_MoreModuleRequestsThanFinished_ReturnsInstallingModules()
    {
        var log = Line(0, LoggingInitialized)
            + Line(1, ModuleRequested("discord_media"))
            + Line(1, ModuleRequested("discord_krisp"))
            + Line(1, ModuleRequested("discord_voice"))
            + Line(2, ModuleDone("discord_media"))
            + Line(3, ModuleFailed("discord_krisp"));

        Assert.Equal("installing modules", UpdaterLog.Analyze(log, processStart: null, Local(15)));
    }

    [Fact]
    public void Analyze_AllModuleRequestsFinishedOrFailed_ReturnsNull()
    {
        var log = Line(0, LoggingInitialized)
            + Line(1, ModuleRequested("discord_media"))
            + Line(1, ModuleRequested("discord_krisp"))
            + Line(2, ModuleDone("discord_media"))
            + Line(3, ModuleFailed("discord_krisp"));

        Assert.Null(UpdaterLog.Analyze(log, processStart: null, Local(15)));
    }

    [Fact]
    public void Analyze_OnlyLastSessionCounts()
    {
        var log = Line(0, LoggingInitialized)
            + Line(1, UpdateStart)
            + Line(1, ModuleRequested("discord_media"))
            + Line(600, LoggingInitialized)
            + Line(601, UpdateStart)
            + Line(605, UpdateComplete);

        Assert.Null(UpdaterLog.Analyze(log, processStart: null, Local(615)));
    }

    [Fact]
    public void Analyze_SessionStartedNearProcessStart_IsNotStartingUp()
    {
        // 這次啟動的 updater 已經寫了 log（session 起點在 process 啟動前 15 秒內）
        var log = Line(0, LoggingInitialized) + Line(1, UpdateStart) + Line(5, UpdateComplete);
        var processStart = Local(10);

        Assert.Null(UpdaterLog.Analyze(log, processStart, processStart.AddSeconds(5)));
    }

    [Fact]
    public void Analyze_ProcessRunningForMoreThanTwoMinutes_IsNotStartingUp()
    {
        var log = Line(0, LoggingInitialized) + Line(1, UpdateStart) + Line(5, UpdateComplete);
        var processStart = Local(600);

        Assert.Null(UpdaterLog.Analyze(log, processStart, processStart.AddMinutes(3)));
    }
}
