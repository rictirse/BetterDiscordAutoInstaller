using System.Buffers.Binary;
using System.Text;
using BetterDiscordAutoInstaller.Tests.TestSupport;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Tests;

public sealed class AsarArchiveTests : IDisposable
{
    private static readonly (string Path, byte[] Content)[] SampleFiles =
    [
        ("package.json", Encoding.UTF8.GetBytes("{\"name\":\"sample\",\"main\":\"main.js\"}")),
        ("main.js", Encoding.UTF8.GetBytes("console.log('main');\n")),
        ("renderer/index.js", Encoding.UTF8.GetBytes("export default 42;\n")),
        ("renderer/styles/theme.css", Encoding.UTF8.GetBytes("body { color: red; }\n")),
        ("renderer/assets/icon.bin", [0x00, 0x01, 0x02, 0xFF]),
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] BuildSample()
    {
        var builder = new AsarBuilder();
        foreach (var (path, content) in SampleFiles) builder.AddFile(path, content);
        return builder.Build();
    }

    [Fact]
    public void ValidArchiveWithNestedFolders_ReturnsSha256ForEveryFileKeyedByPath()
    {
        var hashes = AsarArchive.TryComputeFileHashes(BuildSample());

        Assert.NotNull(hashes);
        Assert.Equal(SampleFiles.Length, hashes.Count);
        foreach (var (path, content) in SampleFiles)
            Assert.Equal(AsarBuilder.Sha256Hex(content), hashes[path]);
    }

    [Fact]
    public void ValidArchiveOnDisk_ReturnsSameHashesAsBytes_AndMissingFileReturnsNull()
    {
        var asar = BuildSample();
        var path = _temp.Combine("betterdiscord.asar");
        File.WriteAllBytes(path, asar);

        Assert.Equal(AsarArchive.TryComputeFileHashes(asar), AsarArchive.TryComputeFileHashes(path));
        Assert.Null(AsarArchive.TryComputeFileHashes(_temp.Combine("missing.asar")));
    }

    [Fact]
    public void FileWithoutIntegrity_IsStillHashed()
    {
        var content = Encoding.UTF8.GetBytes("no integrity here");
        var asar = new AsarBuilder().AddFile("legacy.js", content, withIntegrity: false).Build();

        var hashes = AsarArchive.TryComputeFileHashes(asar);

        Assert.NotNull(hashes);
        Assert.Equal(AsarBuilder.Sha256Hex(content), Assert.Single(hashes).Value);
    }

    [Fact]
    public void TruncatedArchive_ReturnsNull_AtEveryLength()
    {
        var asar = BuildSample();

        // 從 0 byte 到少 1 byte：header 不完整、JSON 被截斷、最後一個檔案內容不完整，全部都要判定為無效
        Assert.All(Enumerable.Range(0, asar.Length), length => Assert.Null(AsarArchive.TryComputeFileHashes(asar[..length])));
    }

    [Fact]
    public void FlippingOneBitInAnyFileContent_ReturnsNull()
    {
        var asar = BuildSample();
        int dataStart = AsarBuilder.GetDataStart(asar);
        Assert.NotNull(AsarArchive.TryComputeFileHashes(asar));

        Assert.All(Enumerable.Range(dataStart, asar.Length - dataStart), index =>
        {
            var corrupted = (byte[])asar.Clone();
            corrupted[index] ^= 0x01;
            Assert.Null(AsarArchive.TryComputeFileHashes(corrupted));
        });
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 64)]
    [InlineData(3, 1024)]
    [InlineData(4, 64 * 1024)]
    public void RandomGarbage_ReturnsNull(int seed, int length)
    {
        var garbage = new byte[length];
        new Random(seed).NextBytes(garbage);

        Assert.Null(AsarArchive.TryComputeFileHashes(garbage));
    }

    [Fact]
    public void ValidHeaderPrefixFollowedByGarbageJson_ReturnsNull()
    {
        var garbage = new byte[256];
        new Random(5).NextBytes(garbage);
        var span = garbage.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 100);

        Assert.Null(AsarArchive.TryComputeFileHashes(garbage));
    }

    [Fact]
    public void WrongMagic_ReturnsNull()
    {
        var asar = BuildSample();
        BinaryPrimitives.WriteUInt32LittleEndian(asar.AsSpan(), 5);

        Assert.Null(AsarArchive.TryComputeFileHashes(asar));
    }

    [Fact]
    public void EmptyArray_ReturnsNull()
    {
        Assert.Null(AsarArchive.TryComputeFileHashes(Array.Empty<byte>()));
    }

    [Fact]
    public void UnpackedAndLinkEntries_AreSkipped()
    {
        var main = Encoding.UTF8.GetBytes("console.log('main');\n");
        var asar = new AsarBuilder()
            .AddFile("main.js", main)
            .AddUnpacked("native/addon.node", [0xDE, 0xAD, 0xBE, 0xEF])
            .AddLink("native/alias.js", "main.js")
            .Build();

        var hashes = AsarArchive.TryComputeFileHashes(asar);

        Assert.NotNull(hashes);
        var only = Assert.Single(hashes);
        Assert.Equal("main.js", only.Key);
        Assert.Equal(AsarBuilder.Sha256Hex(main), only.Value);
    }

    [Fact]
    public void ArchiveWithOnlyUnpackedAndLinkEntries_ReturnsNull()
    {
        var asar = new AsarBuilder()
            .AddUnpacked("native/addon.node", [0xDE, 0xAD, 0xBE, 0xEF])
            .AddLink("alias.js", "native/addon.node")
            .Build();

        // 沒有任何可以驗證的檔案 → 視為無效
        Assert.Null(AsarArchive.TryComputeFileHashes(asar));
    }
}
