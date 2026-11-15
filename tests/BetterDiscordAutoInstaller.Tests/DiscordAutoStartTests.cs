using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// 只測純邏輯（解析登錄裡的指令字串、StartupApproved 的狀態 bytes），不讀登錄檔。
/// </summary>
public sealed class DiscordAutoStartTests
{
    // ── Parse ──

    [Fact]
    public void Parse_QuotedPathWithArguments()
    {
        var entry = DiscordAutoStart.Parse(@"""C:\Users\user\AppData\Local\Discord\Update.exe"" --processStart Discord.exe");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\Users\user\AppData\Local\Discord\Update.exe", entry.FileName);
        Assert.Equal("--processStart Discord.exe", entry.Arguments);
        Assert.False(entry.StartMinimized);
    }

    [Fact]
    public void Parse_WithStartMinimizedArgs_IsStartMinimized()
    {
        var entry = DiscordAutoStart.Parse(
            @"""C:\Users\user\AppData\Local\DiscordPTB\Update.exe"" --processStart DiscordPTB.exe --process-start-args --start-minimized");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\Users\user\AppData\Local\DiscordPTB\Update.exe", entry.FileName);
        Assert.Equal("--processStart DiscordPTB.exe --process-start-args --start-minimized", entry.Arguments);
        Assert.True(entry.StartMinimized);
    }

    [Fact]
    public void Parse_UnquotedPath_SplitsAfterExe()
    {
        var entry = DiscordAutoStart.Parse(@"  C:\Discord\Update.exe --processStart Discord.exe  ");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\Discord\Update.exe", entry.FileName);
        Assert.Equal("--processStart Discord.exe", entry.Arguments);
    }

    [Fact]
    public void Parse_UnquotedPathWithoutArguments()
    {
        var entry = DiscordAutoStart.Parse(@"C:\Discord\Discord.exe");

        Assert.NotNull(entry);
        Assert.Equal(@"C:\Discord\Discord.exe", entry.FileName);
        Assert.Equal("", entry.Arguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_ExpandsEnvironmentVariables(bool quoted)
    {
        // 用測試自己的變數（唯一名稱），不讀也不改真實的 %LOCALAPPDATA%
        var name = "BDAI_TEST_LOCALAPPDATA_" + Guid.NewGuid().ToString("N");
        const string value = @"C:\FakeLocalAppData";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var file = $@"%{name}%\Discord\Update.exe";
            var entry = DiscordAutoStart.Parse((quoted ? $"\"{file}\"" : file) + " --processStart Discord.exe");

            Assert.NotNull(entry);
            Assert.Equal(@"C:\FakeLocalAppData\Discord\Update.exe", entry.FileName);
            Assert.Equal("--processStart Discord.exe", entry.Arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"C:\\Discord\\Update.exe --processStart Discord.exe")]
    [InlineData("\"\" --processStart Discord.exe")]
    [InlineData("Discord --processStart")]
    public void Parse_InvalidCommand_ReturnsNull(string command)
    {
        Assert.Null(DiscordAutoStart.Parse(command));
    }

    // ── IsDisabled（HKCU\...\StartupApproved\Run 的值） ──

    [Fact]
    public void IsDisabled_EnabledState_ReturnsFalse()
    {
        Assert.False(DiscordAutoStart.IsDisabled([0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void IsDisabled_DisabledState_ReturnsTrue()
    {
        Assert.True(DiscordAutoStart.IsDisabled([0x03, 0x00, 0x00, 0x00, 0x5A, 0x1B, 0x3C, 0x4D, 0x00, 0x00, 0x00, 0x00]));
        Assert.True(DiscordAutoStart.IsDisabled([0x03]));
    }

    [Fact]
    public void IsDisabled_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(DiscordAutoStart.IsDisabled(null));
        Assert.False(DiscordAutoStart.IsDisabled([]));
    }
}
