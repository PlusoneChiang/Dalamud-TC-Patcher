# Dalamud TC Patcher

以手寫 C# 補丁為官方 Dalamud 加入 TC 相容性與本地化修正，產出可供本機測試與打包的套件。上游原始碼保持不動。

## 實作方式

`dalamud.tc.patcher/Patches/` 每個功能一份檔案，同時定義執行邏輯與修改位置。Patcher 使用 Roslyn 編譯要寫入 DLL 的程式碼，再由 Mono.Cecil 注入及接上指定方法；編譯用的公開化參考副本不會改變原有成員的輸出可見性。

`upstream/` 以 submodule 追蹤官方 Dalamud 原始碼，供開發補丁時參考；實際套用補丁的 DLL 仍來自下載的官方套件，不需編譯 upstream。

FFCS、Lumina 等來源透過 `external/` 的 submodule 管理。先自行建置這些專案，Patcher 只讀取 `--dependencies` 指定的 DLL，不處理來源下載、checkout 或建置快取。

## 執行流程

1. 初始化 Patcher 的外部 submodule，建置相依 DLL。
2. Patcher 依 manifest 下載並驗證官方套件。
3. 放入已建置的相依 DLL，編譯與套用補丁。
4. 寫回 DLL、重建檔案雜湊與報告，發布輸出目錄。

## 本機執行

需具備專案所要求的 SDK／reference pack、Git 與可用的 NuGet 還原環境。先從儲存庫根目錄執行：

```sh
git submodule update --init -- upstream
git submodule update --init --recursive -- external/FFXIVClientStructs external/Lumina external/Lumina.Excel
./scripts/patch-dalamud.sh
```

腳本每次執行都會清空並重建整個 `artifacts/`，再建置相依 DLL 與 Patcher、下載官方套件並套用補丁，預設輸出到 `artifacts/patched-dalamud/`。也可用 `./scripts/patch-dalamud.sh artifacts/另一個目錄` 指定其中的輸出目錄；先前的產物與下載快取都會清除。

需要個別執行時，可用 `dotnet msbuild build-dependencies.proj` 建置相依，再以 Patcher 的 `--dependencies` 指定 DLL 目錄。
