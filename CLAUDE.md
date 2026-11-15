# BetterDiscordAutoInstaller

.NET 10 主控台工具：Discord（Stable／PTB／Canary）更新後，讓 BetterDiscord（BD）繼續保持注入與最新版，並更新一個 BD plugin。主要平台是 Windows；macOS 為實驗性支援；Linux 不支援執行（但可以建置）。

## 結構

| 路徑 | 職責 |
|---|---|
| `Program.cs` | 選單與自動更新流程（每個 edition：等 updater 閒置 → 需要時更新 Discord → 注入 → 更新 asar／plugin → 確保 Discord 在執行） |
| `Platform/` | `IPlatformService` 與 Windows、macOS 實作（路徑、啟動／關閉 Discord、updater 忙碌判斷、開機自動執行） |
| `Services/` | Discord 版本與程序、BD asar 更新與注入、GitHub CI artifact、plugin 更新、設定檔 |
| `Utils/` | asar 解析與 hash、原子寫入、updater log 分析、開機自啟登錄檔、路徑、中英文字串 |

## 建置與檢查

- 檢查：`scripts/verify.sh`（build 警告視為錯誤 → `tests/` 底下的測試 → `dotnet format whitespace`）。
- 發布（self-contained single file）：`dotnet publish BetterDiscordAutoInstaller -c Release -r <win-x64|osx-arm64|osx-x64> --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none`。不要開 trimming（System.Text.Json 會用到反射）。
- Release 版本號由 csproj 產生：`1.0.<距 2026-10-01 的天數>.<時分 Hmm>`。

## 容易踩的規則

- 覆寫 Discord 可能開著的檔案前，一律先關閉 Discord，並用「暫存檔＋rename」寫入（`FileUtils`）。
- Discord 的 delta 更新會驗證舊檔 hash：讓 Discord 更新前，要把我們的 `index.js` 注入（以及 BD 的 `resources\app` migration）還原成原廠的 bytes。
- `app-*` 資料夾依版本號排序，不要用字串排序；忽略 `.app-*`（還在安裝中）。
- 判斷 BD 要不要更新：比對 release 的 `checksums.txt`（asar 內每個檔案的 SHA256）與本機 asar 實際算出的值，不要用 tag 或 `misc.json`。
- single-file 發布時 `Assembly.Location` 是空字串，版本要從 assembly name 取得。
- 測試不得依賴開發者的電腦（已安裝的 Discord、登錄檔、網路）：用 fixture 與暫存目錄；存取 internal 成員用 `InternalsVisibleTo`，不要用反射。
