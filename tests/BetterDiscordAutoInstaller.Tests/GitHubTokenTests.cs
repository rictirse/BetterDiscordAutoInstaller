using BetterDiscordAutoInstaller.Services;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// CI 模式沒有 GitHub token 時的行為（GitHubService）：自動執行時不能卡在主控台等輸入，
/// 互動模式沒輸入 token 時也不能存成空檔。這些情況都在連網之前就結束。
/// </summary>
public sealed class GitHubTokenTests
{
    /// <summary>任何讀取都會失敗，用來確認程式沒有去等主控台輸入</summary>
    private sealed class NoInputReader : TextReader
    {
        public override int Peek() => throw new InvalidOperationException("Console input must not be read.");
        public override int Read() => throw new InvalidOperationException("Console input must not be read.");
        public override string? ReadLine() => throw new InvalidOperationException("Console input must not be read.");
    }

    private static async Task<T> WithConsoleInput<T>(TextReader input, Func<Task<T>> action)
    {
        // 測試的工作目錄是輸出資料夾，正常不會有 token 檔；有的話這些測試會真的連網，先擋下來
        Assert.False(File.Exists(PathUtils.GitHubTokenFileName), $"Remove {PathUtils.GitHubTokenFileName} from the test output folder.");

        var original = Console.In;
        Console.SetIn(input);
        try
        {
            return await action();
        }
        finally
        {
            Console.SetIn(original);
            File.Delete(PathUtils.GitHubTokenFileName);
        }
    }

    [Fact]
    public async Task CheckForCiUpdates_NoTokenUnattended_ReturnsFalseWithoutPrompt()
    {
        var github = new GitHubService(new ConfigService(), allowTokenPrompt: false);

        Assert.False(await WithConsoleInput(new NoInputReader(), github.CheckForCiUpdatesAsync));
    }

    [Fact]
    public async Task DownloadLatestCiAsar_NoTokenUnattended_ReturnsFalseWithoutPrompt()
    {
        var github = new GitHubService(new ConfigService(), allowTokenPrompt: false);

        Assert.False(await WithConsoleInput(new NoInputReader(), github.DownloadLatestCiAsarAsync));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("   \n")]
    [InlineData("")] // stdin 已關閉，ReadLine 回傳 null
    public async Task CheckForCiUpdates_InteractiveEmptyInput_ReturnsFalseAndSavesNothing(string input)
    {
        var config = new ConfigService();
        var github = new GitHubService(config, allowTokenPrompt: true);

        bool savedFile = false;
        bool result = await WithConsoleInput(new StringReader(input), async () =>
        {
            bool r = await github.CheckForCiUpdatesAsync();
            savedFile = File.Exists(PathUtils.GitHubTokenFileName);
            return r;
        });

        Assert.False(result);
        Assert.False(savedFile);
        Assert.True(string.IsNullOrEmpty(config.GitHubToken));
    }
}
