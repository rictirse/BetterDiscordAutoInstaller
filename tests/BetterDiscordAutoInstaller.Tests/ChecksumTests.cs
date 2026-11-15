using System.Security.Cryptography;
using System.Text;
using BetterDiscordAutoInstaller.Services;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// 本機 asar 算出的 hash 與 release checksums.txt 的比對（BetterDiscordService.FindChangedFiles）。
/// </summary>
public sealed class ChecksumTests
{
    private static readonly string MainHash = Hash("main.js v1");
    private static readonly string RendererHash = Hash("renderer.js v1");
    private static readonly string PackageHash = Hash("package.json v1");

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // 和產品程式碼一樣：key（asar 內的路徑）不分大小寫
    private static Dictionary<string, string> Checksums(params (string Path, string Hash)[] entries) =>
        entries.ToDictionary(e => e.Path, e => e.Hash, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FindChangedFiles_Identical_ReturnsEmpty()
    {
        var local = Checksums(("main.js", MainHash), ("renderer.js", RendererHash), ("package.json", PackageHash));
        var remote = Checksums(("main.js", MainHash), ("renderer.js", RendererHash), ("package.json", PackageHash));

        Assert.Empty(BetterDiscordService.FindChangedFiles(local, remote));
    }

    [Fact]
    public void FindChangedFiles_HashCaseDiffers_TreatedAsEqual()
    {
        var local = Checksums(("main.js", MainHash), ("renderer.js", RendererHash));
        var remote = Checksums(("main.js", MainHash.ToUpperInvariant()), ("renderer.js", RendererHash.ToUpperInvariant()));

        Assert.Empty(BetterDiscordService.FindChangedFiles(local, remote));
    }

    [Fact]
    public void FindChangedFiles_RemoteHasExtraFile_ListsIt()
    {
        var local = Checksums(("main.js", MainHash));
        var remote = Checksums(("main.js", MainHash), ("renderer.js", RendererHash));

        Assert.Equal(["renderer.js"], BetterDiscordService.FindChangedFiles(local, remote));
    }

    [Fact]
    public void FindChangedFiles_HashDiffers_ListsIt()
    {
        var local = Checksums(("main.js", MainHash), ("renderer.js", RendererHash));
        var remote = Checksums(("main.js", Hash("main.js v2")), ("renderer.js", RendererHash));

        Assert.Equal(["main.js"], BetterDiscordService.FindChangedFiles(local, remote));
    }

    [Fact]
    public void FindChangedFiles_LocalOnlyFile_IsNotListed()
    {
        var local = Checksums(("main.js", MainHash), ("old.js", Hash("old.js")));
        var remote = Checksums(("main.js", MainHash));

        Assert.Empty(BetterDiscordService.FindChangedFiles(local, remote));
    }
}
