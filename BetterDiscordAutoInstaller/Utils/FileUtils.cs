using System.Text;

namespace BetterDiscordAutoInstaller.Utils;

public static class FileUtils
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 先寫到同資料夾的暫存檔，完整寫完後再一次取代目標檔（同磁碟的 rename 是原子操作）。
    /// 避免下載中斷、程式被關掉或關機時留下只寫一半的檔案。
    /// </summary>
    public static async Task ReplaceFileAsync(string path, byte[] content, int retries = 5)
    {
        var temp = CreateTempPath(path);
        try
        {
            await File.WriteAllBytesAsync(temp, content);

            // 剛結束的 Discord process 可能還沒完全釋放檔案 handle，稍微重試幾次
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < retries && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 以「暫存檔 + 取代」的方式寫入文字檔（UTF-8 無 BOM），保證不會留下半個檔案。
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var temp = CreateTempPath(path);
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string CreateTempPath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        // 檔名以 .tmp 結尾，BetterDiscord 的 plugin 監看不會誤載入
        return Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清不掉暫存檔不影響主流程
        }
    }
}
