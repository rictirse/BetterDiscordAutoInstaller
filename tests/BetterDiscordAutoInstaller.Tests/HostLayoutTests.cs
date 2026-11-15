using BetterDiscordAutoInstaller.Platform;
using BetterDiscordAutoInstaller.Tests.TestSupport;

namespace BetterDiscordAutoInstaller.Tests;

public sealed class HostLayoutTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── ParseAppFolderVersion ──

    [Theory]
    [InlineData("app-1.0.9268", "1.0.9268")]
    [InlineData("APP-1.0.9268", "1.0.9268")]
    [InlineData("app-1.0.10000", "1.0.10000")]
    [InlineData("1.0.9268", "1.0.9268")]
    public void ParseAppFolderVersion_ValidName_ReturnsVersion(string folderName, string expected)
    {
        Assert.Equal(Version.Parse(expected), DiscordHostLayout.ParseAppFolderVersion(folderName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("app-")]
    [InlineData("app-abc")]
    [InlineData("Discord")]
    [InlineData("packages")]
    [InlineData(".app-1.0.10001.installing")]
    public void ParseAppFolderVersion_InvalidName_ReturnsNull(string folderName)
    {
        Assert.Null(DiscordHostLayout.ParseAppFolderVersion(folderName));
    }

    // ── GetLatestInstalledDiscordFolder ──

    [Fact]
    public void GetLatestInstalledDiscordFolder_ComparesVersionsNumerically()
    {
        var install = _temp.CreateDirectory("Discord");
        _temp.CreateDirectory("Discord", "app-1.0.9999");
        _temp.CreateDirectory("Discord", "app-1.0.10000");
        _temp.CreateDirectory("Discord", "app-1.0.9268");

        Assert.Equal("app-1.0.10000", DiscordHostLayout.GetLatestInstalledDiscordFolder(install));
    }

    [Fact]
    public void GetLatestInstalledDiscordFolder_IgnoresInstallingFolder()
    {
        var install = _temp.CreateDirectory("Discord");
        _temp.CreateDirectory("Discord", "app-1.0.9999");
        _temp.CreateDirectory("Discord", "app-1.0.10000");
        _temp.CreateDirectory("Discord", ".app-1.0.10001.installing");

        Assert.Equal("app-1.0.10000", DiscordHostLayout.GetLatestInstalledDiscordFolder(install));
    }

    [Fact]
    public void GetLatestInstalledDiscordFolder_WithoutAppFolders_ThrowsDirectoryNotFound()
    {
        var install = _temp.CreateDirectory("Discord");
        _temp.CreateDirectory("Discord", "packages");
        _temp.CreateDirectory("Discord", ".app-1.0.10001.installing");
        File.WriteAllText(_temp.Combine("Discord", "app-1.0.9999"), "not a directory");

        Assert.Throws<DirectoryNotFoundException>(() => DiscordHostLayout.GetLatestInstalledDiscordFolder(install));
    }

    [Fact]
    public void GetLatestInstalledDiscordFolder_MissingDirectory_ThrowsDirectoryNotFound()
    {
        var missing = _temp.Combine("DoesNotExist");

        Assert.Throws<DirectoryNotFoundException>(() => DiscordHostLayout.GetLatestInstalledDiscordFolder(missing));
    }

    // ── FindDesktopCoreDir ──

    [Fact]
    public void FindDesktopCoreDir_PicksHighestModuleNumber()
    {
        var host = _temp.CreateDirectory("Discord", "app-1.0.9268");
        _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_desktop_core-1", "discord_desktop_core");
        _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_desktop_core-2", "discord_desktop_core");
        _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_desktop_core-10", "discord_desktop_core");
        _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_voice-11", "discord_voice");

        Assert.Equal(
            Path.Combine(host, "modules", "discord_desktop_core-10", "discord_desktop_core"),
            DiscordHostLayout.FindDesktopCoreDir(host));
    }

    [Fact]
    public void FindDesktopCoreDir_WithoutModulesFolder_ReturnsNull()
    {
        var host = _temp.CreateDirectory("Discord", "app-1.0.9268");

        Assert.Null(DiscordHostLayout.FindDesktopCoreDir(host));
    }

    [Fact]
    public void FindDesktopCoreDir_WithoutDesktopCoreModule_ReturnsNull()
    {
        var host = _temp.CreateDirectory("Discord", "app-1.0.9268");
        _temp.CreateDirectory("Discord", "app-1.0.9268", "modules", "discord_voice-1", "discord_voice");

        Assert.Null(DiscordHostLayout.FindDesktopCoreDir(host));
    }

    // ── IsInsideDirectory（純路徑比對，不碰檔案系統） ──

    [Fact]
    public void IsInsideDirectory_FileInSubdirectory_ReturnsTrue()
    {
        var discord = _temp.Combine("Discord");

        Assert.True(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord, "Update.exe"), discord));
        Assert.True(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord, "app-1.0.9268", "Discord.exe"), discord));
        Assert.True(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord, "Update.exe"), discord + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void IsInsideDirectory_SiblingWithSamePrefix_ReturnsFalse()
    {
        var discord = _temp.Combine("Discord");
        var discord2 = _temp.Combine("Discord2");

        Assert.False(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord2, "Update.exe"), discord));
    }

    [Fact]
    public void IsInsideDirectory_EscapingWithDotDot_ReturnsFalse()
    {
        var discord = _temp.Combine("Discord");

        Assert.False(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord, "..", "Other", "Update.exe"), discord));
        Assert.False(DiscordHostLayout.IsInsideDirectory(Path.Combine(discord, "app-1.0.9268", "..", "..", "Update.exe"), discord));
    }
}
