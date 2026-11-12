#!/usr/bin/env bash
set -euo pipefail

usage() {
    printf '用法：%s [輸出目錄]\n' "${0##*/}"
    printf '建置相依與 Patcher，下載官方 Dalamud 並套用補丁。\n'
    printf '每次執行先清空並重建 artifacts/，輸出目錄必須位於其中。\n'
    printf '輸出目錄相對於儲存庫根目錄，預設為 artifacts/patched-dalamud。\n'
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    exit 0
fi
if [[ $# -gt 1 || "${1:-}" == -* ]]; then
    usage >&2
    exit 2
fi

patcher_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$patcher_root"
artifacts_root="$patcher_root/artifacts"
output="${1:-artifacts/patched-dalamud}"
if [[ "$output" != /* ]]; then
    output="$patcher_root/$output"
fi
while [[ "$output" == */ ]]; do
    output="${output%/}"
done
case "$output" in
    "$artifacts_root/"*) ;;
    *) printf '輸出目錄必須位於 artifacts/ 內：%s\n' "$output" >&2; exit 1 ;;
esac
case "$output/" in
    */../*|*/./*) printf '輸出路徑不可包含 . 或 .. 區段。\n' >&2; exit 1 ;;
esac
if [[ -L "$artifacts_root" ]]; then
    printf 'artifacts 不可為符號連結，已停止清理。\n' >&2
    exit 1
fi
if ! command -v dotnet >/dev/null 2>&1; then
    printf '找不到 dotnet，請先安裝專案要求的 .NET SDK。\n' >&2
    exit 1
fi

printf '[1/4] 清空並重建 artifacts…\n'
rm -rf -- "$artifacts_root"
mkdir -p -- "$artifacts_root"

printf '[2/4] 建置外部相依…\n'
dotnet msbuild build-dependencies.proj '-clp:ErrorsOnly;Summary' -m:1 -nr:false

printf '[3/4] 建置 Patcher…\n'
dotnet build dalamud.tc.patcher/dalamud.tc.patcher.csproj \
    --configuration Release --nologo --disable-build-servers \
    -m:1 -nr:false -p:UseSharedCompilation=false

printf '[4/4] 下載官方 Dalamud 並套用補丁…\n'
dotnet run --project dalamud.tc.patcher --configuration Release --no-build --no-restore -- \
    build --manifest manifests/dalamud.json \
    --dependencies artifacts/dependencies \
    --cache artifacts/cache \
    --output "$output"

printf '\n已完成。\n輸出目錄：%s\n產物報告：%s/versionInfo.json\n' "$output" "$output"
