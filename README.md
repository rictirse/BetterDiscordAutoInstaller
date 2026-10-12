# BetterDiscordAutoInstaller

## About

**BetterDiscordAutoInstaller-CS** is a lightweight, high-performance C# rewrite of the original [BetterDiscordAutoInstaller](https://github.com/zwylair/BetterDiscordAutoInstaller/tree/master). Built with .NET 10, it provides a seamless and automated experience for patching and maintaining BetterDiscord across all Discord builds (Stable, PTB, Canary).

### Key Features & Enhancements

* **C# & .NET 10 Engine**: Native binary performance without requiring a Python environment.
* **Auto-Setup for Missing Clients**: Automatically downloads and installs Discord if it is missing from your system.
* **Smart Non-Intrusive Updates**: Queries Discord and BetterDiscord versions via API. If your installation is up to date, it **will not** forcibly close your running Discord client.
* **Windows Startup Integration**: Built-in option to easily create or remove Windows startup shortcuts with automatic update arguments.
* **Multi-Client Patching**: Automatically detects and injects `betterdiscord.asar` into Discord, Discord PTB, and Discord Canary.
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

### CLI Arguments for Automation

You can pass arguments directly to execute tasks silently (ideal for Windows Task Scheduler or custom scripts):

```bash
# Run auto-update directly
BetterDiscordAutoInstaller.exe 3
```

---

## Getting Started

### Prerequisites

* **OS**: Windows 10 / 11 (x64)
* **Runtime**: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) *(or use a self-contained release)*

### Quick Start

1. Download the latest `BetterDiscordAutoInstaller.exe` from the [Releases](https://github.com/rictirse/BetterDiscordAutoInstaller/releases/latest) section.
2. Run the executable and choose **Option 1** to enable auto-updates on Windows startup.
3. The app will generate a Windows Startup shortcut configured with parameter `3` to keep BetterDiscord silently updated every time you boot your PC.

---

## Credits & Acknowledgments

This project is a C# port and enhanced rewrite inspired by the original Python tool **[BetterDiscordAutoInstaller](https://github.com/zwylair/BetterDiscordAutoInstaller/tree/master)** created by **[Zwylair](https://github.com/zwylair)**.

Special thanks to Zwylair for the original concept and foundation!

---

## License

Distributed under the [MIT License](https://www.google.com/search?q=./LICENSE).
