using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BetterDiscordAutoInstaller.Tests.TestSupport;

/// <summary>
/// 在測試裡用程式產生 Electron asar（不放任何真實的 BetterDiscord asar）。格式與 Utils/AsarArchive 的解析邏輯一致：
///   [uint32 LE = 4][uint32 LE headerPickleSize][uint32 LE headerPickleSize - 4][uint32 LE jsonLength][json，padding 到 4 bytes][檔案內容...]
///   檔案內容從 8 + headerPickleSize 開始；header 的 "files" 是樹狀結構，
///   每個檔案有 offset（字串，相對於檔案內容起點）、size、integrity { algorithm, hash, blockSize, blocks[] }。
/// </summary>
internal sealed class AsarBuilder
{
    private const int BlockSize = 4 * 1024 * 1024;

    private readonly JsonObject _header;
    private readonly JsonObject _rootFiles = [];
    private readonly MemoryStream _content = new();

    public AsarBuilder()
    {
        _header = new JsonObject { ["files"] = _rootFiles };
    }

    /// <summary>加入一般檔案；路徑用 "/" 分隔，例如 "renderer/index.js"</summary>
    public AsarBuilder AddFile(string path, byte[] content, bool withIntegrity = true)
    {
        var entry = new JsonObject
        {
            ["size"] = content.Length,
            ["offset"] = _content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (withIntegrity) entry["integrity"] = Integrity(content);

        _content.Write(content);
        GetParentFiles(path, out var name)[name] = entry;
        return this;
    }

    /// <summary>加入 unpacked 檔案（實際內容在 app.asar.unpacked，asar 內沒有 offset）</summary>
    public AsarBuilder AddUnpacked(string path, byte[] content)
    {
        GetParentFiles(path, out var name)[name] = new JsonObject
        {
            ["size"] = content.Length,
            ["unpacked"] = true,
            ["integrity"] = Integrity(content),
        };
        return this;
    }

    /// <summary>加入 symlink（只有 link，沒有 offset / size）</summary>
    public AsarBuilder AddLink(string path, string target)
    {
        GetParentFiles(path, out var name)[name] = new JsonObject { ["link"] = target };
        return this;
    }

    public byte[] Build()
    {
        var json = Encoding.UTF8.GetBytes(_header.ToJsonString());
        int alignedJsonLength = (json.Length + 3) & ~3;
        int headerPickleSize = 8 + alignedJsonLength;
        var content = _content.ToArray();

        var output = new byte[8 + headerPickleSize + content.Length];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)headerPickleSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)(headerPickleSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)json.Length);
        json.CopyTo(output, 16);
        content.CopyTo(output, 8 + headerPickleSize);
        return output;
    }

    /// <summary>從 asar bytes 讀出檔案內容的起點（8 + headerPickleSize）</summary>
    public static int GetDataStart(byte[] asar) => 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(asar.AsSpan(4));

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private JsonObject GetParentFiles(string path, out string name)
    {
        var parts = path.Split('/');
        var files = _rootFiles;
        foreach (var dir in parts[..^1])
        {
            if (files[dir] is not JsonObject node)
            {
                node = new JsonObject { ["files"] = new JsonObject() };
                files[dir] = node;
            }
            files = (JsonObject)node["files"]!;
        }
        name = parts[^1];
        return files;
    }

    private static JsonObject Integrity(byte[] content)
    {
        var blocks = new JsonArray();
        for (int i = 0; i < content.Length; i += BlockSize)
            blocks.Add(Sha256Hex(content[i..Math.Min(content.Length, i + BlockSize)]));

        return new JsonObject
        {
            ["algorithm"] = "SHA256",
            ["hash"] = Sha256Hex(content),
            ["blockSize"] = BlockSize,
            ["blocks"] = blocks,
        };
    }
}
