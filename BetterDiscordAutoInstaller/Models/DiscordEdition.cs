namespace BetterDiscordAutoInstaller.Models;

public enum DiscordEdition
{
    Discord,       // Stable
    DiscordCanary, // Canary
    DiscordPTB     // PTB
}

public static class DiscordEditionExtensions
{
    public static string ToExecutableName(this DiscordEdition edition) => $"{edition}.exe";
}