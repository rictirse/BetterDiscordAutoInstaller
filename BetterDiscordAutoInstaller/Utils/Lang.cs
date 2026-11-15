using System.Globalization;

namespace BetterDiscordAutoInstaller.Utils;

public static class Lang
{
    // 判斷系統語系是否為中文 (包含 zh-TW, zh-CN, zh-HK 等)
    public static bool IsZh { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string zh, string en) => IsZh ? zh : en;
}
