using System.Globalization;
using System.Text;

namespace BetterDiscordAutoInstaller.Utils;

/// <summary>
/// 執行紀錄：把主控台輸出（Console.Out）同時寫進 log 檔。
/// Windows 開機自動執行時主控台視窗幾秒後就關閉，出錯時要靠這個檔案查。
/// 只攔 Console.Out：輸入（Console.ReadLine，例如 GitHub token）不經過 Console.Out，不會進 log。
/// log 是附加功能，開檔、寫檔失敗都不能影響工具本身的執行。
/// </summary>
internal static class RunLog
{
    internal const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// 開始記錄。path 為 null，或開檔失敗時，回傳 null，Console.Out 不變（開檔失敗時在主控台印一行 [WARN]）。
    /// 成功時：寫入一行執行標頭，把 Console.Out 換成 TeeLogWriter。
    /// 回傳的物件 Dispose 時還原原本的 Console.Out 並關檔。
    /// </summary>
    public static IDisposable? Start(string? path, string[] args)
    {
        if (path == null) return null;

        var file = OpenLogFile(path, MaxBytes);
        if (file != null && !TryWriteHeader(file, args))
        {
            CloseQuietly(file);
            file = null;
        }

        if (file == null)
        {
            Console.WriteLine($"[WARN] Could not open the log file {path}. Continuing without a log.");
            return null;
        }

        var console = Console.Out;
        var writer = TextWriter.Synchronized(new TeeLogWriter(console, file, () => DateTimeOffset.Now));
        Console.SetOut(writer);
        return new Session(console, writer);
    }

    /// <summary>
    /// 建立資料夾。檔案超過 maxBytes 時，先改名成 path + ".1"（已存在就覆蓋），只保留一份舊檔。
    /// 以附加模式開檔（FileShare.ReadWrite，另一個執行中的本工具不會讓開檔失敗），
    /// UTF-8 不含 BOM，AutoFlush = true。任何 IO 或權限錯誤都回傳 null，不丟例外。
    /// </summary>
    internal static StreamWriter? OpenLogFile(string path, long maxBytes)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
            {
                try
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 另一個執行中的本工具開著這個檔案時（Windows）無法改名：這次繼續附加在原檔，下次執行再輪替
                }
            }

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        }
        catch (Exception)
        {
            // 路徑無效、父路徑是檔案、沒有權限、磁碟錯誤……一律當作沒有 log
            return null;
        }
    }

    /// <summary>執行標頭（只寫進檔案）。前面空一行，和上一次執行的內容分開</summary>
    internal static string FormatHeader(DateTimeOffset now, string[] args)
    {
        string time = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        string argText = args.Length == 0 ? "(none)" : string.Join(" ", args);
        return $"===== {time}  BetterDiscordAutoInstaller v{PathUtils.BdaiVersion}  args: {argText} =====";
    }

    private static bool TryWriteHeader(StreamWriter file, string[] args)
    {
        try
        {
            file.WriteLine();
            file.WriteLine(FormatHeader(DateTimeOffset.Now, args));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CloseQuietly(TextWriter writer)
    {
        try
        {
            writer.Dispose();
        }
        catch (Exception)
        {
            // 關檔時的 flush 失敗（例如磁碟滿）不影響工具執行
        }
    }

    /// <summary>Start 回傳的物件：Dispose 時先還原 Console.Out，再關閉 log 檔</summary>
    private sealed class Session(TextWriter originalOut, TextWriter writer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Console.SetOut(originalOut);
            CloseQuietly(writer);
        }
    }
}

/// <summary>
/// 把寫入的內容原封不動轉給 console，同時寫進 file，file 的每一行開頭加上 "[HH:mm:ss.fff] "。
/// 一行的開頭 = 第一次寫入，或前一個字元是 '\n'；'\r'（進度列）不算換行。
/// 時間取寫入該行第一個字元的時候（換行後不會先寫一個空的前綴）。
/// 寫 file 失敗時吞掉例外並停止寫 file，console 照常。Dispose 只關 file，不關 console。
/// 本身不是 thread-safe，交給 Console.Out 前要用 TextWriter.Synchronized 包住。
/// </summary>
internal sealed class TeeLogWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly Func<DateTimeOffset> _clock;

    // null = 已停用（寫入失敗或已 Dispose）
    private TextWriter? _file;
    private bool _atLineStart = true;

    public TeeLogWriter(TextWriter console, TextWriter file, Func<DateTimeOffset> clock)
        : base(console.FormatProvider)
    {
        _console = console;
        _file = file;
        _clock = clock;

        // WriteLine() / WriteLine(string) 由基底類別組成「內容 + NewLine」再呼叫 Write，換行字元要和 console 相同
        NewLine = console.NewLine;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value)
    {
        _console.Write(value);
        WriteToFile([value]);
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        _console.Write(value);
        WriteToFile(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        _console.Write(buffer, index, count);
        WriteToFile(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        _console.Write(buffer);
        WriteToFile(buffer);
    }

    public override void Flush()
    {
        _console.Flush();
        if (_file == null) return;

        try
        {
            _file.Flush();
        }
        catch (Exception)
        {
            DisableFile();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _file != null)
        {
            // 最後一行沒有換行結尾（例如輸入提示後 stdin 就關閉）時補上，下次執行的標頭前才會真的空一行
            if (!_atLineStart) WriteToFile(NewLine);
            DisableFile();
        }

        base.Dispose(disposing);
    }

    private void WriteToFile(ReadOnlySpan<char> text)
    {
        if (_file == null) return;

        try
        {
            while (!text.IsEmpty)
            {
                if (_atLineStart)
                {
                    _file.Write(_clock().ToString("[HH:mm:ss.fff] ", CultureInfo.InvariantCulture));
                    _atLineStart = false;
                }

                int newline = text.IndexOf('\n');
                if (newline < 0)
                {
                    _file.Write(text);
                    return;
                }

                _file.Write(text[..(newline + 1)]);
                _atLineStart = true;
                text = text[(newline + 1)..];
            }
        }
        catch (Exception)
        {
            // 例如磁碟滿：之後不再寫 file，工具照常執行
            DisableFile();
        }
    }

    /// <summary>停止寫 file 並關檔（關檔失敗也不往外丟）</summary>
    private void DisableFile()
    {
        var file = _file;
        _file = null;
        if (file == null) return;

        try
        {
            file.Dispose();
        }
        catch (Exception)
        {
            // 關檔時的 flush 失敗（例如磁碟滿）不影響工具執行
        }
    }
}
