using System.Text;
using BetterDiscordAutoInstaller.Tests.TestSupport;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Tests;

/// <summary>
/// RunLog：TeeLogWriter（console 逐字不變、file 每行加時間前綴、file 失敗不影響 console）與 OpenLogFile（建資料夾、附加、輪替、失敗回傳 null）。
/// 不碰 Console.Out / Console.SetOut（全域狀態，會干擾平行執行的其他測試），也不碰真實的 %LocalAppData%。
/// console 的 NewLine 固定成 "\n"，Windows / macOS / Linux 跑出來的字串都一樣。
/// </summary>
public sealed class RunLogTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 15, 8, 30, 15, 123, TimeSpan.FromHours(8));
    private const string P = "[08:30:15.123] ";

    private static StringWriter NewConsole() => new() { NewLine = "\n" };

    private static TeeLogWriter NewTee(StringWriter console, TextWriter file) => new(console, file, () => FixedTime);

    // ── TeeLogWriter：console ──

    [Fact]
    public void ConsoleReceivesExactlyWhatWasWritten()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.WriteLine("BetterDiscordAutoInstaller 自動更新");
        tee.Write("\r下載中 10%");
        tee.Write("\r下載中 100%");
        tee.WriteLine();
        tee.Write('[');
        tee.Write("x]y".ToCharArray(), 0, 2);
        tee.Write("請輸入選項 (1-3): ");

        Assert.Equal("BetterDiscordAutoInstaller 自動更新\n\r下載中 10%\r下載中 100%\n[x]請輸入選項 (1-3): ", console.ToString());
    }

    [Fact]
    public void EncodingAndNewLineComeFromConsole()
    {
        using var console = new StreamWriter(new MemoryStream(), new UTF8Encoding(false)) { NewLine = "\r\n" };
        using var file = new StringWriter();
        using var tee = new TeeLogWriter(console, file, () => FixedTime);

        Assert.Same(console.Encoding, tee.Encoding);
        Assert.Equal("\r\n", tee.NewLine);
    }

    // ── TeeLogWriter：file 的時間前綴 ──

    [Fact]
    public void FilePrefixesEveryLineWithTime()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.Write("第一行\n第二行\n");
        tee.WriteLine("第三行");

        Assert.Equal($"{P}第一行\n{P}第二行\n{P}第三行\n", file.ToString());
    }

    [Fact]
    public void LineBuiltFromSeveralWritesHasOnePrefix()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.Write("Discord ");
        tee.Write("is ");
        tee.Write("running");
        tee.WriteLine();
        tee.Write("next");

        Assert.Equal($"{P}Discord is running\n{P}next", file.ToString());
    }

    [Fact]
    public void CarriageReturnDoesNotStartANewLine()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.Write("\rDownloading 10%");
        tee.Write("\rDownloading 100%");
        tee.WriteLine();
        tee.WriteLine("done");

        Assert.Equal($"{P}\rDownloading 10%\rDownloading 100%\n{P}done\n", file.ToString());
    }

    [Fact]
    public void CrLfNewLineIsOneLineBreak()
    {
        using var console = new StringWriter { NewLine = "\r\n" };
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.WriteLine("a");
        tee.Write("b");

        Assert.Equal("a\r\nb", console.ToString());
        Assert.Equal($"{P}a\r\n{P}b", file.ToString());
    }

    [Fact]
    public void PrefixTimeIsTakenWhenTheLinesFirstCharacterIsWritten()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        var time = new DateTimeOffset(2026, 11, 15, 23, 59, 59, 0, TimeSpan.Zero);
        using var tee = new TeeLogWriter(console, file, () => time);

        tee.WriteLine("a");
        // 換行之後還沒寫下一行，不能先寫出前綴
        Assert.Equal("[23:59:59.000] a\n", file.ToString());

        time = time.AddMilliseconds(1500);
        tee.Write("b");

        Assert.Equal("[23:59:59.000] a\n[00:00:00.500] b", file.ToString());
    }

    [Fact]
    public void EveryWriteOverloadIsPrefixed()
    {
        var console = NewConsole();
        using var file = new StringWriter();
        using var tee = NewTee(console, file);

        tee.WriteLine();
        tee.Write('x');
        tee.Write('\n');
        tee.Write('y');
        tee.Write("--line1\nline2--".ToCharArray(), 2, 11);
        tee.Write('\n');
        tee.Write("span 中文\n".AsSpan());
        tee.Write(42);
        tee.WriteLine("!");

        Assert.Equal("\nx\nyline1\nline2\nspan 中文\n42!\n", console.ToString());
        Assert.Equal($"{P}\n{P}x\n{P}yline1\n{P}line2\n{P}span 中文\n{P}42!\n", file.ToString());
    }

    // ── TeeLogWriter：file 失敗 ──

    [Fact]
    public void FileFailureIsSwallowedAndFileIsNotUsedAgain()
    {
        var console = NewConsole();
        var file = new ThrowingWriter();
        using var tee = NewTee(console, file);

        var ex = Record.Exception(() =>
        {
            tee.WriteLine("first");
            tee.Write('x');
            tee.Write("abc".ToCharArray(), 0, 3);
            tee.WriteLine("second");
            tee.Flush();
        });

        Assert.Null(ex);
        Assert.Equal("first\nxabcsecond\n", console.ToString());
        Assert.Equal(1, file.WriteCalls);
        Assert.Equal(0, file.FlushCalls);
    }

    // ── TeeLogWriter：Dispose ──

    [Fact]
    public void DisposeClosesFileButNotConsole()
    {
        var console = NewConsole();
        var file = new StringWriter();
        var tee = NewTee(console, file);

        tee.WriteLine("before");
        tee.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Write("x"));
        console.Write("console still open");
        Assert.Equal("before\nconsole still open", console.ToString());
        Assert.Equal($"{P}before\n", file.ToString());
    }

    [Fact]
    public void DisposeEndsAnUnfinishedLineInTheFile()
    {
        var console = NewConsole();
        var file = new StringWriter();
        var tee = NewTee(console, file);

        tee.Write("Enter your choice (1-3): ");
        tee.Dispose();

        Assert.Equal("Enter your choice (1-3): ", console.ToString());
        Assert.Equal($"{P}Enter your choice (1-3): \n", file.ToString());
    }

    // ── RunLog.Start / 標頭 ──

    [Fact]
    public void StartWithoutPathDoesNothing()
    {
        Assert.Null(RunLog.Start(null, ["3"]));
    }

    [Fact]
    public void HeaderFormat()
    {
        var now = new DateTimeOffset(2026, 11, 15, 8, 5, 9, TimeSpan.FromHours(8));
        string version = PathUtils.BdaiVersion;

        Assert.Equal($"===== 2026-11-15 08:05:09 +08:00  BetterDiscordAutoInstaller v{version}  args: 3 =====",
            RunLog.FormatHeader(now, ["3"]));
        Assert.Equal($"===== 2026-11-15 08:05:09 +08:00  BetterDiscordAutoInstaller v{version}  args: --auto x =====",
            RunLog.FormatHeader(now, ["--auto", "x"]));
        Assert.Equal($"===== 2026-11-15 08:05:09 +08:00  BetterDiscordAutoInstaller v{version}  args: (none) =====",
            RunLog.FormatHeader(now, []));
    }

    // ── OpenLogFile ──

    [Fact]
    public void OpenLogFileCreatesMissingDirectory()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("BetterDiscordAutoInstaller", "logs", "test.log");

        using (var writer = RunLog.OpenLogFile(path, RunLog.MaxBytes))
        {
            Assert.NotNull(writer);
            writer.Write("hello");
        }

        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(10L)] // 未超過上限
    [InlineData(4L)] // 剛好等於上限也不算超過
    public void OpenLogFileAppendsWhenNotOverLimit(long maxBytes)
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("test.log");
        File.WriteAllText(path, "old\n");

        using (var writer = RunLog.OpenLogFile(path, maxBytes))
        {
            Assert.NotNull(writer);
            writer.Write("new\n");
        }

        Assert.Equal("old\nnew\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // .1 已存在：被覆蓋
    public void OpenLogFileRotatesWhenOverLimit(bool previousBackupExists)
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("test.log");
        File.WriteAllText(path, "0123456789\n");
        if (previousBackupExists) File.WriteAllText(path + ".1", "older backup");

        using (var writer = RunLog.OpenLogFile(path, 10))
        {
            Assert.NotNull(writer);
            Assert.Equal(0, new FileInfo(path).Length);
            writer.Write("new\n");
        }

        Assert.Equal("new\n", File.ReadAllText(path));
        Assert.Equal("0123456789\n", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void OpenLogFileReturnsNullWhenParentIsAFile()
    {
        using var temp = new TempDirectory();
        string blocker = temp.Combine("blocker");
        File.WriteAllText(blocker, "not a folder");

        Assert.Null(RunLog.OpenLogFile(Path.Combine(blocker, "logs", "test.log"), RunLog.MaxBytes));
        Assert.Null(RunLog.OpenLogFile(Path.Combine(blocker, "test.log"), RunLog.MaxBytes));
        Assert.Null(RunLog.OpenLogFile(blocker + Path.DirectorySeparatorChar, RunLog.MaxBytes));
    }

    [Fact]
    public void OpenLogFileReturnsNullForInvalidPath()
    {
        Assert.Null(RunLog.OpenLogFile("", RunLog.MaxBytes));
        Assert.Null(RunLog.OpenLogFile("bad\0name.log", RunLog.MaxBytes));
    }

    [Fact]
    public void OpenLogFileSucceedsWhileAnotherHandleSharesTheFile()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("test.log");

        using var other = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var writer = RunLog.OpenLogFile(path, RunLog.MaxBytes);

        Assert.NotNull(writer);
        writer.Write("a\n");
        Assert.Equal("a\n", ReadShared(path));

        // 另一個同時執行的本工具也開得了
        using var second = RunLog.OpenLogFile(path, RunLog.MaxBytes);
        Assert.NotNull(second);
    }

    [Fact]
    public void OpenLogFileSucceedsWhenRotationIsBlockedByAnotherInstance()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("test.log");
        File.WriteAllText(path, "0123456789\n");

        // Windows 上開著的檔案不能改名：不輪替，仍要能開檔（Linux / macOS 則照常輪替）
        using var running = RunLog.OpenLogFile(path, RunLog.MaxBytes);
        using var writer = RunLog.OpenLogFile(path, 10);

        Assert.NotNull(running);
        Assert.NotNull(writer);
    }

    [Fact]
    public void OpenLogFileWritesUtf8WithoutBomAndFlushesEveryWrite()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("test.log");

        using var writer = RunLog.OpenLogFile(path, RunLog.MaxBytes);
        Assert.NotNull(writer);
        Assert.True(writer.AutoFlush);

        writer.Write("中文 log\n");

        // 還沒 Dispose 就已經寫到磁碟（開機執行時視窗可能被直接關掉）
        Assert.Equal(Encoding.UTF8.GetBytes("中文 log\n"), ReadSharedBytes(path));
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadShared(string path) => Encoding.UTF8.GetString(ReadSharedBytes(path));

    /// <summary>每次寫入都丟例外的 file（模擬磁碟滿）。TextWriter 的其他 Write 多載最後都會走到 Write(char)</summary>
    private sealed class ThrowingWriter : TextWriter
    {
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            WriteCalls++;
            throw new IOException("There is not enough space on the disk.");
        }

        public override void Flush()
        {
            FlushCalls++;
            throw new IOException("There is not enough space on the disk.");
        }
    }
}
