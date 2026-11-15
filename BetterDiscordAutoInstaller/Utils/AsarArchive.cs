using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BetterDiscordAutoInstaller.Utils;

/// <summary>
/// 解析 Electron 的 asar 封裝檔，計算內部每個檔案的 SHA256。
/// 用途：
///   1. 驗證 asar 是否完整（下載中斷、被截斷、內容與 header 記錄的 integrity 不符都會回傳 null）
///   2. 與 BetterDiscord release 附帶的 checksums.txt 比對，判斷是否需要更新
/// </summary>
public static class AsarArchive
{
    public static Dictionary<string, string>? TryComputeFileHashes(string path)
    {
        try
        {
            return File.Exists(path) ? TryComputeFileHashes(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, string>? TryComputeFileHashes(byte[] data)
    {
        try
        {
            // asar 格式：[uint32 = 4][uint32 headerPickleSize][uint32 payloadSize][uint32 jsonLength][json...]
            if (data.Length < 16) return null;
            var span = data.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 4) return null;

            long headerPickleSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            long jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            long dataStart = 8 + headerPickleSize;
            if (16 + jsonLength > dataStart || dataStart > data.Length) return null;

            using var doc = JsonDocument.Parse(data.AsMemory(16, (int)jsonLength));
            if (!doc.RootElement.TryGetProperty("files", out var files)) return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return Walk(files, "", data, dataStart, result) && result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Walk(JsonElement files, string prefix, byte[] data, long dataStart, Dictionary<string, string> result)
    {
        foreach (var entry in files.EnumerateObject())
        {
            string name = prefix + entry.Name;
            var node = entry.Value;

            if (node.TryGetProperty("files", out var children))
            {
                if (!Walk(children, name + "/", data, dataStart, result)) return false;
                continue;
            }

            // unpacked 檔案不在 asar 內；symlink 沒有 offset
            if (node.TryGetProperty("unpacked", out var unpacked) && unpacked.ValueKind == JsonValueKind.True) continue;
            if (!node.TryGetProperty("offset", out var offsetEl) || !node.TryGetProperty("size", out var sizeEl)) continue;

            long offset = offsetEl.ValueKind == JsonValueKind.String
                ? long.Parse(offsetEl.GetString()!, CultureInfo.InvariantCulture)
                : offsetEl.GetInt64();
            long size = sizeEl.GetInt64();
            long start = dataStart + offset;
            if (offset < 0 || size < 0 || start + size > data.Length) return false;

            string hash = Convert.ToHexStringLower(SHA256.HashData(data.AsSpan((int)start, (int)size)));

            if (node.TryGetProperty("integrity", out var integrity) &&
                integrity.TryGetProperty("hash", out var expected) &&
                !hash.Equals(expected.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            result[name] = hash;
        }

        return true;
    }
}
