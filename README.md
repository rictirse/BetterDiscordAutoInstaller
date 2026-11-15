# BetterDiscordAutoInstaller

## About

**BetterDiscordAutoInstaller-CS** is a lightweight, high-performance C# rewrite of the original [BetterDiscordAutoInstaller](https://github.com/zwylair/BetterDiscordAutoInstaller/tree/master). Built with .NET 10, it provides a seamless and automated experience for patching and maintaining BetterDiscord across all Discord builds (Stable, PTB, Canary).

It runs on **Windows** and **macOS**. Windows gets the full feature set. On macOS it keeps BetterDiscord up to date but leaves the injection itself to BetterDiscord (see [macOS](#macos)).

### Key Features & Enhancements

* **C# & .NET 10 Engine**: Native binary performance without requiring a Python environment.
* **Auto-Setup for Missing Clients** *(Windows)*: Automatically downloads and installs Discord if it is missing from your system.
* **Smart Non-Intrusive Updates**: Queries Discord and BetterDiscord versions via API. If your installation is up to date, it **will not** forcibly close your running Discord client.
* **Startup Integration**: Built-in option to create or remove a Windows startup shortcut, or a macOS login LaunchAgent, that runs the auto-update.
* **Multi-Client Patching** *(Windows)*: Automatically detects and injects `betterdiscord.asar` into Discord, Discord PTB, and Discord Canary.
* **Bilingual Interface**: Automatically detects system locale and presents prompts in English or Traditional Chinese (`zh`).

---

## Interactive Menu & Command-Line Usage

When launched without parameters, the application presents an interactive menu:

```text
==========================================
Please select an option:
1. Enable auto-update on Windows startup
2. Disable auto-update on Windows startup
3. Run auto-update now
==========================================
```

On macOS, options 1 and 2 read "Enable / Disable auto-update at macOS login".

### CLI Arguments for Automation

You can pass arguments directly to execute tasks silently (ideal for Windows Task Scheduler, launchd, or custom scripts):

```bash
# Run auto-update directly (Windows)
BetterDiscordAutoInstaller.exe 3

# Run auto-update directly (macOS)
./BetterDiscordAutoInstaller 3
```

---

## Getting Started

### Prerequisites

* **OS**: Windows 10 / 11 (x64), or macOS 12+ (Apple Silicon or Intel)
* **Runtime**: [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) *(or use a self-contained release)*

### Quick Start (Windows)

1. Download the latest `BetterDiscordAutoInstaller.exe` from the [Releases](https://github.com/rictirse/BetterDiscordAutoInstaller/releases/latest) section.
2. Run the executable and choose **Option 1** to enable auto-updates on Windows startup.
3. The app will generate a Windows Startup shortcut configured with parameter `3` to keep BetterDiscord silently updated every time you boot your PC.

---

## macOS

### How it differs from Windows

On Windows this tool injects BetterDiscord itself. It also drives Discord's own updates: it restores the stock files first, because Discord's delta updater rejects modified files.

On macOS the tool **does not inject BetterDiscord and does not touch `Discord.app`**. It only:

* keeps `~/Library/Application Support/BetterDiscord/data/betterdiscord.asar` and the bundled plugin up to date,
* checks whether BetterDiscord is loaded in each Discord app and tells you if it is not,
* closes Discord while it replaces those files, then reopens it with `open -a`.

Why not inject on macOS:

* BetterDiscord 1.14+ injects on macOS by editing the app bundle: `Discord.app/Contents/Resources/app.asar` is renamed to `betterdiscord.app.asar`, and `app/index.js` is added to load BetterDiscord first. The official BetterDiscord installer does the same.
* Discord updates on macOS by replacing the whole `.app` through ShipIt, which removes the injection every time. BetterDiscord 1.14.6 already handles this from inside Discord. After ShipIt installs a new version, a helper script waits for Gatekeeper to accept the unmodified bundle. It then patches the bundle, relaunches Discord, and reverts the patch if Gatekeeper refuses it.
* An outside tool patching the bundle would run into two problems. Patching before Gatekeeper's first check makes macOS report the app as damaged. And macOS 13+ requires the **App Management** (or Full Disk Access) permission to modify another app's bundle. A command-line tool started by Terminal or launchd usually does not have that permission.
* Because ShipIt replaces the whole app, the delta-update problem that the Windows flow works around does not exist on macOS. Discord's own updates are left alone.

### Setup

1. Install Discord into `/Applications` (or `~/Applications`) from [discord.com/download](https://discord.com/download) and open it once.
2. Run the official [BetterDiscord installer](https://betterdiscord.app) **once**. After that, BetterDiscord re-applies its injection after every Discord update.
3. Put `BetterDiscordAutoInstaller` in a folder you own, for example `~/Applications/BetterDiscordAutoInstaller/`. `settings.json` is stored next to the binary, so the folder must be writable.
4. Make the binary executable and clear the download quarantine flag:

   ```bash
   chmod +x ~/Applications/BetterDiscordAutoInstaller/BetterDiscordAutoInstaller
   ```

   ```bash
   xattr -d com.apple.quarantine ~/Applications/BetterDiscordAutoInstaller/BetterDiscordAutoInstaller
   ```

5. Run it from Terminal and choose **Option 1**. This writes `~/Library/LaunchAgents/local.BetterDiscordAutoInstaller.plist`, which runs `BetterDiscordAutoInstaller 3` once at every login. Output goes to `~/Library/Logs/BetterDiscordAutoInstaller.log`. macOS may show a "Background Items Added" notification; you can manage the item in **System Settings → General → Login Items**. Option 2 removes the plist.

### Paths used on macOS

| What | Path |
| --- | --- |
| Discord | `/Applications/Discord.app` (falls back to `~/Applications/Discord.app`) |
| Discord PTB / Canary | set `discord_ptb_installed_path` / `discord_canary_installed_path` in `settings.json`, e.g. `/Applications/Discord PTB.app` |
| Discord data and updater state | `~/Library/Application Support/discord` (`discordptb`, `discordcanary`) |
| BetterDiscord data and plugins | `~/Library/Application Support/BetterDiscord` |
| Login item | `~/Library/LaunchAgents/local.BetterDiscordAutoInstaller.plist` |

### Limits on macOS

* No automatic Discord install. If Discord is missing, the tool prints the download link and stops.
* No Discord version check or Discord update handling. Discord and ShipIt update themselves.
* `use_betterdiscord_ci_releases` has no effect, because the official BetterDiscord loader always loads `betterdiscord.asar`.
* The tool waits for Discord's updater before closing Discord. On macOS it treats Discord as busy in these cases:
  * BetterDiscord's post-update lock (`$TMPDIR/betterdiscord-migrate-<channel>.lock`) exists.
  * An updater log under `~/Library/Application Support/discord/logs` was written in the last few seconds.
  * A new host was staged recently (`app-*` folder or `ShipIt_request.json`).
  * Discord started less than 30 seconds ago.

  The Windows log-text markers are **not** used on macOS, because the macOS log format has not been checked.

### Not yet verified on a real Mac

The macOS support was built and cross-compiled on Windows. These parts have **not** been tested on a Mac:

* launching (`open -a`), quitting (SIGTERM, then force kill after 5 s), and process detection by name (`Discord`, `Discord PTB`, `Discord Canary`),
* the LaunchAgent running at login,
* the updater busy detection above (log file name, staging folders, lock path),
* detection of the BetterDiscord loader in `Discord.app/Contents/Resources/app/index.js`.

Please open an issue with the console output if something does not work.

---

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Self-contained single-file builds for each platform:

```bash
dotnet publish BetterDiscordAutoInstaller -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

```bash
dotnet publish BetterDiscordAutoInstaller -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

```bash
dotnet publish BetterDiscordAutoInstaller -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

Use `osx-arm64` for Apple Silicon Macs and `osx-x64` for Intel Macs. The output is in `BetterDiscordAutoInstaller/bin/Release/net10.0/<rid>/publish/`.

The macOS builds can be cross-compiled from Windows. The .NET 10 SDK ad-hoc signs the macOS binary, which Apple Silicon requires. Copying the binary through a zip made on Windows drops the executable bit, so run the `chmod +x` and `xattr` steps above on the Mac. If macOS still refuses to run it, re-sign it ad hoc on the Mac:

```bash
codesign --force --sign - BetterDiscordAutoInstaller
```

---

## Credits & Acknowledgments

This project is a C# port and enhanced rewrite inspired by the original Python tool **[BetterDiscordAutoInstaller](https://github.com/zwylair/BetterDiscordAutoInstaller/tree/master)** created by **[Zwylair](https://github.com/zwylair)**.

Special thanks to Zwylair for the original concept and foundation!

---

## License

Distributed under the [MIT License](https://www.google.com/search?q=./LICENSE).
