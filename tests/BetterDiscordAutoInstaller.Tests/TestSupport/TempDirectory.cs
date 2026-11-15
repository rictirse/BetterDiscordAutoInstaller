namespace BetterDiscordAutoInstaller.Tests.TestSupport;

/// <summary>
/// 每個測試專用的暫存目錄（Path.GetTempPath() 底下的唯一名稱），Dispose 時整個刪除。
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>在暫存目錄底下組路徑（一律用 Path.Combine，Linux / Windows 都能用）</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>建立暫存目錄底下的子資料夾並回傳完整路徑</summary>
    public string CreateDirectory(params string[] parts) => Directory.CreateDirectory(Combine(parts)).FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 清不掉暫存目錄不影響測試結果
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
