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
        if (plugin.IsInstalled())
        {
            Console.WriteLine($"[INFO] Plugin {plugin.Name} is already installed.");
            return;
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

            Console.WriteLine($"[INFO] Plugin {plugin.Name} successfully installed.");
        }
        catch (OperationCanceledException oce)
        {
            Console.WriteLine($"[ERROR] Failed to download plugin {plugin.Name}: Connection timed out (15s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to download plugin {plugin.Name}: {ex.Message}");
        }
    }
}