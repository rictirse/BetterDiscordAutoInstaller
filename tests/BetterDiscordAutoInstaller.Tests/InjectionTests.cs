using System.Security.Cryptography;
using System.Text;
using BetterDiscordAutoInstaller.Services;
using BetterDiscordAutoInstaller.Tests.TestSupport;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// 在暫存目錄建一個假的 Discord host（app-x\modules\discord_desktop_core-1\discord_desktop_core、app-x\resources），
/// 驗證注入、偵測與還原的檔案內容。
/// </summary>
public sealed class InjectionTests : IDisposable
{
    // 原廠 discord_desktop_core\index.js（CRLF 結尾）；測試自己寫一份，不引用產品程式碼裡的常數
    private const string StockIndexJs = "module.exports = require('./core.asar');\r\n";
    private static readonly byte[] StockIndexJsBytes = Encoding.UTF8.GetBytes(StockIndexJs);

    // 產生 BD 的 resources\app\index.js 時用的假 Windows 路徑（不會被存取，只用來驗證跳脫後的比對）
    private const string WindowsBdFolder = @"C:\Users\user\AppData\Roaming\BetterDiscord";

    private readonly TempDirectory _temp = new();
    private readonly string _bdFolder;
    private readonly string _host;
    private readonly string _coreDir;
    private readonly string _indexJs;
    private readonly string _resources;
    private readonly BetterDiscordService _service;

    public InjectionTests()
    {
        _bdFolder = _temp.CreateDirectory("BetterDiscord");
        _host = _temp.CreateDirectory("Discord", "app-1.0.9268");
        _coreDir = _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_desktop_core-1", "discord_desktop_core");
        _indexJs = Path.Combine(_coreDir, "index.js");
        _resources = _temp.CreateDirectory("Discord", "app-1.0.9268", "resources");
        _service = CreateService(_bdFolder);
    }

    public void Dispose() => _temp.Dispose();

    private static BetterDiscordService CreateService(string betterDiscordFolder)
    {
        var config = new ConfigService();
        return new BetterDiscordService(config, new GitHubService(config), new FakePlatformService(betterDiscordFolder));
    }

    private string StableAsar => Path.Combine(_bdFolder, "data", "betterdiscord.asar");
    private string CiAsar => Path.Combine(_bdFolder, "data", "betterdiscord-ci.asar");
    private string AsarPath(bool isCi) => isCi ? CiAsar : StableAsar;

    private string ResourcesPath(params string[] parts) => Path.Combine([_resources, .. parts]);

    /// <summary>本工具加在 index.js 最前面的那一行（反斜線換成 /）</summary>
    private static string RequireLine(string asarPath) => $"require(\"{asarPath.Replace('\\', '/')}\");";

    /// <summary>和 JSON.stringify 一樣把路徑寫成 JS 字串（反斜線跳脫成兩個）</summary>
    private static string JsString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>原廠的 discord_desktop_core：core.asar + 原廠 index.js</summary>
    private void CreateStockDesktopCore()
    {
        File.WriteAllBytes(Path.Combine(_coreDir, "core.asar"), [0x04, 0x00, 0x00, 0x00]);
        File.WriteAllBytes(_indexJs, StockIndexJsBytes);
    }

    /// <summary>
    /// 模擬 BD 自己的 resources migration：app.asar → betterdiscord.app.asar，並建立 resources\app\index.js。
    /// </summary>
    private void CreateBdResourcesMigration(byte[] betterDiscordAppAsar, string asarPathInLoader)
    {
        File.WriteAllBytes(ResourcesPath("betterdiscord.app.asar"), betterDiscordAppAsar);
        Directory.CreateDirectory(ResourcesPath("app"));
        File.WriteAllText(ResourcesPath("app", "index.js"),
            $"require({JsString(asarPathInLoader)});\nmodule.exports = require(\"../betterdiscord.app.asar\");\n");
    }

    // ── 本工具的 discord_desktop_core 注入 ──

    [Fact]
    public void GetAsarPath_UsesPlatformBetterDiscordFolder()
    {
        Assert.Equal(Path.Combine(_bdFolder, "data", "betterdiscord.asar"), _service.GetAsarPath(isCi: false));
        Assert.Equal(Path.Combine(_bdFolder, "data", "betterdiscord-ci.asar"), _service.GetAsarPath(isCi: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InjectPatch_OnStockIndexJs_PrependsRequireLineAndKeepsOriginalBytes(bool isCi)
    {
        CreateStockDesktopCore();

        Assert.True(_service.InjectPatch(_host, isCi));

        var bytes = File.ReadAllBytes(_indexJs);
        var firstLine = Encoding.UTF8.GetBytes(RequireLine(AsarPath(isCi)) + "\r\n");
        Assert.Equal(firstLine, bytes[..firstLine.Length]);
        Assert.Equal(StockIndexJsBytes, bytes[firstLine.Length..]);
    }

    [Fact]
    public void InjectPatch_WithWindowsStyleFolder_WritesForwardSlashesInRequireLine()
    {
        CreateStockDesktopCore();

        Assert.True(CreateService(WindowsBdFolder).InjectPatch(_host, isCi: false));

        Assert.Equal(
            "require(\"C:/Users/user/AppData/Roaming/BetterDiscord/data/betterdiscord.asar\");\r\n" + StockIndexJs,
            File.ReadAllText(_indexJs));
    }

    [Fact]
    public void InjectPatch_Twice_IsIdempotent()
    {
        CreateStockDesktopCore();
        Assert.True(_service.InjectPatch(_host, isCi: false));
        var afterFirst = File.ReadAllBytes(_indexJs);

        Assert.True(_service.InjectPatch(_host, isCi: false));

        Assert.Equal(afterFirst, File.ReadAllBytes(_indexJs));
    }

    [Fact]
    public void InjectPatch_CiAfterStable_ReplacesRequireLineInsteadOfStacking()
    {
        CreateStockDesktopCore();
        Assert.True(_service.InjectPatch(_host, isCi: false));

        Assert.True(_service.InjectPatch(_host, isCi: true));

        var content = File.ReadAllText(_indexJs);
        Assert.Equal(RequireLine(CiAsar) + "\r\n" + StockIndexJs, content);
        Assert.DoesNotContain(RequireLine(StableAsar), content);
    }

    [Fact]
    public void InjectPatch_WithoutCoreAsar_ReturnsFalseAndDoesNotCreateIndexJs()
    {
        // modules\discord_desktop_core-1\discord_desktop_core 存在，但模組還沒裝完（沒有 core.asar，也沒有 index.js）
        Assert.False(_service.InjectPatch(_host, isCi: false));

        Assert.False(File.Exists(_indexJs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_coreDir));
    }

    [Fact]
    public void InjectPatch_WithoutModulesFolder_ReturnsFalse()
    {
        var host = _temp.CreateDirectory("DiscordPTB", "app-1.0.1000");

        Assert.False(_service.InjectPatch(host, isCi: false));

        Assert.False(Directory.Exists(Path.Combine(host, "modules")));
    }

    // ── 還原（讓 Discord 更新前） ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertInjectionForUpdate_RestoresStockIndexJsByteForByte(bool isCi)
    {
        CreateStockDesktopCore();
        Assert.True(_service.InjectPatch(_host, isCi));
        Assert.NotEqual(Sha256Hex(StockIndexJsBytes), Sha256Hex(File.ReadAllBytes(_indexJs)));

        Assert.True(_service.RevertInjectionForUpdate(_host));

        // Discord 的 delta 更新會驗證舊檔的 SHA256
        Assert.Equal(Sha256Hex(StockIndexJsBytes), Sha256Hex(File.ReadAllBytes(_indexJs)));
        Assert.False(_service.RevertInjectionForUpdate(_host));
        Assert.Equal(Sha256Hex(StockIndexJsBytes), Sha256Hex(File.ReadAllBytes(_indexJs)));
    }

    [Fact]
    public void RevertInjectionForUpdate_OnStockHost_ReturnsFalseAndChangesNothing()
    {
        CreateStockDesktopCore();

        Assert.False(_service.RevertInjectionForUpdate(_host));

        Assert.Equal(StockIndexJsBytes, File.ReadAllBytes(_indexJs));
    }

    [Fact]
    public void RevertInjectionForUpdate_ResourcesMigration_RestoresAppAsarFromBetterDiscordAppAsar()
    {
        CreateStockDesktopCore();
        byte[] stockAppAsar = [0x04, 0x00, 0x00, 0x00, 0x10, 0x20, 0x30];
        CreateBdResourcesMigration(stockAppAsar, StableAsar);

        Assert.True(_service.RevertInjectionForUpdate(_host));

        Assert.Equal(stockAppAsar, File.ReadAllBytes(ResourcesPath("app.asar")));
        Assert.False(Directory.Exists(ResourcesPath("app")));
        Assert.False(File.Exists(ResourcesPath("betterdiscord.app.asar")));
    }

    [Fact]
    public void RevertInjectionForUpdate_ResourcesMigrationWithOpenAsar_RestoresAppAsarFromBackup()
    {
        CreateStockDesktopCore();
        byte[] stockAppAsar = [0x04, 0x00, 0x00, 0x00, 0xAA, 0xBB];
        byte[] openAsar = [0x04, 0x00, 0x00, 0x00, 0x0A, 0x5A, 0x12];
        File.WriteAllBytes(ResourcesPath("app.asar.backup"), stockAppAsar);
        CreateBdResourcesMigration(openAsar, StableAsar);

        Assert.True(_service.RevertInjectionForUpdate(_host));

        Assert.Equal(stockAppAsar, File.ReadAllBytes(ResourcesPath("app.asar")));
        Assert.False(File.Exists(ResourcesPath("app.asar.backup")));
        Assert.False(Directory.Exists(ResourcesPath("app")));
        Assert.False(File.Exists(ResourcesPath("betterdiscord.app.asar")));
    }

    [Fact]
    public void RevertInjectionForUpdate_WhenAppAsarAlreadyExists_KeepsItAndOnlyRemovesAppFolder()
    {
        CreateStockDesktopCore();
        byte[] existingAppAsar = [0x04, 0x00, 0x00, 0x00, 0x01];
        byte[] betterDiscordAppAsar = [0x04, 0x00, 0x00, 0x00, 0x02];
        File.WriteAllBytes(ResourcesPath("app.asar"), existingAppAsar);
        CreateBdResourcesMigration(betterDiscordAppAsar, StableAsar);

        Assert.True(_service.RevertInjectionForUpdate(_host));

        Assert.Equal(existingAppAsar, File.ReadAllBytes(ResourcesPath("app.asar")));
        Assert.False(Directory.Exists(ResourcesPath("app")));
        Assert.Equal(betterDiscordAppAsar, File.ReadAllBytes(ResourcesPath("betterdiscord.app.asar")));
    }

    [Fact]
    public void RevertInjectionForUpdate_AppFolderWithoutBetterDiscordLoader_IsLeftAlone()
    {
        CreateStockDesktopCore();
        byte[] otherAppAsar = [0x04, 0x00, 0x00, 0x00, 0x03];
        File.WriteAllBytes(ResourcesPath("betterdiscord.app.asar"), otherAppAsar);
        Directory.CreateDirectory(ResourcesPath("app"));
        File.WriteAllText(ResourcesPath("app", "index.js"), "module.exports = require(\"../other.asar\");\n");

        Assert.False(_service.RevertInjectionForUpdate(_host));

        Assert.True(File.Exists(ResourcesPath("app", "index.js")));
        Assert.Equal(otherAppAsar, File.ReadAllBytes(ResourcesPath("betterdiscord.app.asar")));
        Assert.False(File.Exists(ResourcesPath("app.asar")));
    }

    // ── 偵測 ──

    [Fact]
    public void StockHost_IsNotInjected()
    {
        CreateStockDesktopCore();

        Assert.False(_service.IsCoreInjected(_host, isCi: false));
        Assert.False(_service.IsResourcesInjected(_host, isCi: false));
        Assert.False(_service.IsBdInjected(_host, isCi: false));
        Assert.False(_service.IsBdInjected(_host, isCi: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreInjection_IsDetectedOnlyForTheInjectedAsar(bool isCi)
    {
        CreateStockDesktopCore();
        Assert.True(_service.InjectPatch(_host, isCi));

        Assert.True(_service.IsCoreInjected(_host, isCi));
        Assert.True(_service.IsBdInjected(_host, isCi));
        Assert.False(_service.IsResourcesInjected(_host, isCi));

        // 用另一個 asar 的路徑查詢 → 未注入
        Assert.False(_service.IsCoreInjected(_host, !isCi));
        Assert.False(_service.IsBdInjected(_host, !isCi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourcesInjection_IsDetectedOnlyForTheInjectedAsar(bool isCi)
    {
        CreateStockDesktopCore();
        CreateBdResourcesMigration([0x04, 0x00, 0x00, 0x00], AsarPath(isCi));

        Assert.True(_service.IsResourcesInjected(_host, isCi));
        Assert.True(_service.IsBdInjected(_host, isCi));
        Assert.False(_service.IsCoreInjected(_host, isCi));

        Assert.False(_service.IsResourcesInjected(_host, !isCi));
        Assert.False(_service.IsBdInjected(_host, !isCi));
    }

    [Fact]
    public void ResourcesInjection_WithoutBetterDiscordAppAsar_IsNotDetected()
    {
        CreateStockDesktopCore();
        CreateBdResourcesMigration([0x04, 0x00, 0x00, 0x00], StableAsar);
        File.Delete(ResourcesPath("betterdiscord.app.asar"));

        Assert.False(_service.IsResourcesInjected(_host, isCi: false));
    }

    [Theory]
    // BD 用 JSON.stringify 寫的路徑：反斜線被跳脫成兩個
    [InlineData(@"require(""C:\\Users\\user\\AppData\\Roaming\\BetterDiscord\\data\\betterdiscord.asar"");")]
    // 大小寫不同
    [InlineData(@"require(""c:\\users\\user\\appdata\\roaming\\betterdiscord\\data\\BetterDiscord.asar"");")]
    // 正斜線
    [InlineData(@"require(""C:/Users/user/AppData/Roaming/BetterDiscord/data/betterdiscord.asar"");")]
    // asar 內的檔案
    [InlineData(@"require(""C:\\Users\\user\\AppData\\Roaming\\BetterDiscord\\data\\betterdiscord.asar\\main.js"");")]
    public void ResourcesInjection_MatchesPathWrittenByBetterDiscord(string loaderLine)
    {
        CreateStockDesktopCore();
        File.WriteAllBytes(ResourcesPath("betterdiscord.app.asar"), [0x04, 0x00, 0x00, 0x00]);
        Directory.CreateDirectory(ResourcesPath("app"));
        File.WriteAllText(ResourcesPath("app", "index.js"), loaderLine + "\nmodule.exports = require(\"../betterdiscord.app.asar\");\n");
        var service = CreateService(WindowsBdFolder);

        Assert.True(service.IsResourcesInjected(_host, isCi: false));
        Assert.True(service.IsBdInjected(_host, isCi: false));
        Assert.False(service.IsResourcesInjected(_host, isCi: true));
    }

    // ── StripBdRequireLines ──

    [Fact]
    public void StripBdRequireLines_RemovesMultipleRequireLines()
    {
        var content =
            "require(\"C:/Users/user/AppData/Roaming/BetterDiscord/data/betterdiscord.asar\");\r\n" +
            "require(\"C:/Users/user/AppData/Roaming/BetterDiscord/data/betterdiscord-ci.asar\");\r\n" +
            StockIndexJs;

        Assert.Equal(StockIndexJs, BetterDiscordService.StripBdRequireLines(content));
    }

    [Fact]
    public void StripBdRequireLines_KeepsUnrelatedRequires()
    {
        var content =
            "require(\"./other.asar\");\r\n" +
            "require(\"C:/Users/user/AppData/Roaming/BetterDiscord/plugins/loader.js\");\r\n" +
            "const x = require(\"C:/Users/user/AppData/Roaming/BetterDiscord/data/betterdiscord.asar\");\r\n" +
            StockIndexJs;

        Assert.Equal(content, BetterDiscordService.StripBdRequireLines(content));
    }

    [Theory]
    [InlineData("require(\"C:/Users/user/AppData/Roaming/BETTERDISCORD/data/BetterDiscord.asar\");")]
    [InlineData("require(\"/home/user/.config/BetterDiscord/data/BETTERDISCORD-CI.ASAR\");")]
    public void StripBdRequireLines_IsCaseInsensitive(string requireLine)
    {
        Assert.Equal(StockIndexJs, BetterDiscordService.StripBdRequireLines(requireLine + "\r\n" + StockIndexJs));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StripBdRequireLines_AcceptsLfOrCrLfLineEnding(string lineEnding)
    {
        var content = RequireLine(StableAsar) + lineEnding + StockIndexJs;

        Assert.Equal(StockIndexJs, BetterDiscordService.StripBdRequireLines(content));
    }

    // ── 暫存檔 ──

    [Fact]
    public void InjectAndRevert_LeaveNoTempFilesBehind()
    {
        CreateStockDesktopCore();
        CreateBdResourcesMigration([0x04, 0x00, 0x00, 0x00], StableAsar);

        Assert.True(_service.InjectPatch(_host, isCi: false));
        Assert.True(_service.InjectPatch(_host, isCi: false));
        Assert.True(_service.InjectPatch(_host, isCi: true));
        Assert.True(_service.RevertInjectionForUpdate(_host));
        Assert.True(_service.InjectPatch(_host, isCi: false));

        // FileUtils 的暫存檔是 ".<檔名>.<guid>.tmp"；包含隱藏檔一起找
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*.tmp", options));
    }
}
