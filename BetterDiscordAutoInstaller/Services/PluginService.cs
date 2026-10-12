using BetterDiscordAutoInstaller.Helper;
using BetterDiscordAutoInstaller.Models;
using System.Net;
using System.Net.Sockets;

namespace BetterDiscordAutoInstaller.Services;

public class PluginService
{
    private static readonly HttpClient HttpClient;

    static PluginService()
    {
        // 建立客製化的 SocketsHttpHandler
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // 停用 WPAD 代理搜尋
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),

            // 【關鍵修復】強迫 Socket 連線只採用 IPv4，避開 IPv6 卡死的問題
            ConnectCallback = async (context, cancellationToken) =>
            {
                // 解析 DNS 取得 IP 清單
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);

                // 優先過濾出 IPv4 地址
                var ipv4Address = entry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                                 ?? entry.AddressList.FirstOrDefault();

                if (ipv4Address == null)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                var socket = new Socket(ipv4Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                try
                {
                    socket.NoDelay = true;
                    await socket.ConnectAsync(new IPEndPoint(ipv4Address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        HttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        HttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        );
    }

    public async Task DownloadPluginAsync(PluginInfo plugin)
    {
        // 檢查檔案是否存在且比對版本
        if (plugin.FileExists)
        {
            string? localVersionStr = plugin.GetInstalledVersion();

            // 嘗試線上檢查版本
            string? remoteVersionStr = await FetchRemotePluginVersionAsync(plugin.Url);

            if (!string.IsNullOrEmpty(localVersionStr) && !string.IsNullOrEmpty(remoteVersionStr))
            {
                if (Version.TryParse(localVersionStr, out var localVer) &&
                    Version.TryParse(remoteVersionStr, out var remoteVer))
                {
                    if (localVer >= remoteVer)
                    {
                        Console.WriteLine($"[INFO] Plugin {plugin.Name} is up-to-date (Version {localVersionStr}). Skipping download.");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"[INFO] Update found for {plugin.Name}: {localVersionStr} -> {remoteVersionStr}");
                    }
                }
                else if (string.Equals(localVersionStr, remoteVersionStr, StringComparison.OrdinalIgnoreCase))
                {
                    // 若版本號無法用 System.Version 解析（如非標準語意化版本），降級為字串比較
                    Console.WriteLine($"[INFO] Plugin {plugin.Name} is already at latest version ({localVersionStr}).");
                    return;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(plugin.SavePath)!);
        Console.WriteLine($"[INFO] Downloading plugin {plugin.Name}...");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            using var response = await HttpClient.GetAsync(plugin.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(plugin.SavePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            await contentStream.CopyToAsync(fileStream, cts.Token);

            string? installedVer = plugin.GetInstalledVersion();
            Console.WriteLine($"[INFO] Plugin {plugin.Name} successfully installed. (Version: {installedVer ?? "Unknown"})");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[ERROR] Failed to download plugin {plugin.Name}: Connection timed out (15s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download plugin {plugin.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 輕量讀取遠端 JS 檔標頭，萃取 @version
    /// </summary>
    private async Task<string?> FetchRemotePluginVersionAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // 使用 ResponseHeadersRead 搭配串流，避免一次下載整個大檔案
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            string? line;
            int linesRead = 0;
            while ((line = await reader.ReadLineAsync()) != null && linesRead < 50)
            {
                var version = PluginHeaderParser.ExtractVersion(line);
                if (version != null)
                {
                    return version;
                }
                linesRead++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not fetch remote version for check: {ex.Message}");
        }

        return null;
    }

    public async Task<bool> CheckNeedsUpdateAsync(PluginInfo plugin)
    {
        // 1. 檔案不存在，肯定需要下載
        if (!plugin.FileExists) return true;

        // 2. 取得本地與線上版本號
        string? localVersionStr = plugin.GetInstalledVersion();
        string? remoteVersionStr = await FetchRemotePluginVersionAsync(plugin.Url);

        if (string.IsNullOrEmpty(localVersionStr) || string.IsNullOrEmpty(remoteVersionStr))
        {
            // 若無法抓到版本，預設允許更新/覆蓋
            return true;
        }

        if (Version.TryParse(localVersionStr, out var localVer) &&
            Version.TryParse(remoteVersionStr, out var remoteVer))
        {
            // 遠端版本比本地新才需要更新
            return remoteVer > localVer;
        }

        // 若版本非標準數字格式，降級為字串比對
        return !string.Equals(localVersionStr, remoteVersionStr, StringComparison.OrdinalIgnoreCase);
    }
}