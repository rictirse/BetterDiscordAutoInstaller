#!/usr/bin/env bash
# 建置檢查：build（警告視為錯誤）→ test → 空白格式。任何一步失敗即以非 0 結束。
# 用法：scripts/verify.sh            （於 repo 根目錄或 worktree 根目錄執行）
# - 測試：tests/ 底下所有不是 -windows 的測試專案（目前還沒有）。
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() {
  local name=$1; shift
  echo "::step:: $name"
  if ! "$@"; then echo "::fail:: $name"; exit 1; fi
}

tests() {
  local rc=0 p
  while IFS= read -r p; do
    grep -qE '<TargetFrameworks?>[^<]*-windows' "$p" && continue
    dotnet test "$p" --no-build || rc=1
  done < <(find tests -name '*.csproj' 2>/dev/null | sort)
  return $rc
}

step build  dotnet build BetterDiscordAutoInstaller.slnx --nologo -clp:NoSummary -warnaserror
step test   tests
step format dotnet format whitespace BetterDiscordAutoInstaller.slnx --verify-no-changes --no-restore
echo "::pass::"
