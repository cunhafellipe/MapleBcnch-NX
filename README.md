# MapleBench

MapleBench is a Windows WZ editor and dependency-aware client importer for MapleStory. Browse and edit archive data, preview visual assets, work with common game-data sections, and move content between client versions with verified saves.

> [!IMPORTANT]
> Always work on copies of game data and keep an independent backup. MapleBench verifies its output before replacing a destination and normally creates timestamped backups, but no editor can protect against every damaged source file, storage failure, or interrupted write.

---

## 這個分支做了什麼

**上游**：[xkiro-dev/MapleBench](https://github.com/xkiro-dev/MapleBench) ·
分支 `map-generation-baseline` · 27 個提交在初始發布之上

上游是一個成熟的 WZ 編輯器。這個分支把它推向一個新的目標：
**讓 AI agent 依提示詞、用現成素材生成指定風格的地圖。**

### 與源專案的不同

| 面向 | 上游 | 這個分支 |
|---|---|---|
| **NX 支援** | 沒有 | 反向工程出 `PKG4` 二進位格式：讀取、轉換、**寫回**、img 匯出。8,400,000 節點的 `Map.nx` 開檔 0.5 秒 |
| **地圖編輯器** | 沒有 | 完整的 map editor：圖層、圖塊、foothold 鏈、傳送門、怪物生成、minimap 重新產生 |
| **自我驗證** | 存檔前驗證位元組 | `/validate` — 斷鏈、無效引用、link stub、層別不一致。**刻意的壞地圖能被機器抓到** |
| **批次語意** | 每次編輯一筆 undo | `/batch` — 300 次操作 = **一筆** undo（undo 堆疊只有 200 格） |
| **Agent 介面** | 無 | `MapleMcp` — 41 個工具的 MCP 伺服器（11 地圖 + 8 封裝拆分 + 22 通用資產），agent 只經 MCP 就能跑完整流程 |
| **繁體中文** | 無 | 全介面繁中，2,321 條翻譯。執行期切換，不需重啟 |
| **地圖結構規格** | 無 | `docs/MAP-STRUCTURE.md` — 10 張真實地圖解剖出的規格，每條規則都有出處 |

### 協作方式

這個分支由 **`e78-png`** 與 **AI agent 協作**完成。

AI agent 參與了實作、除錯與驗證的每一個環節——包含大部分的程式碼撰寫、
以及相當比例的除錯工作。特別值得一提的是，本分支多項關鍵修復
（NX 素材調色盤、放置後存檔崩潰、`t()` 字串串接的查表錯誤）
**都是 agent 實跑驗證後找出來的，不是讀程式碼猜的**。

`JOURNAL/` 記錄了每一輪做了什麼、憑什麼這麼說、以及沒做什麼——
包含 agent 犯過並自己抓到的錯。這個日誌比提交訊息更值得讀。

### 致謝

本分支的開發基礎來自 **追風技術 陳維克** 提供的 **繁體中文優化版本**，
以及上游 [xkiro-dev/MapleBench](https://github.com/xkiro-dev/MapleBench)。

沒有那份繁中版本，本分支的介面在地圖生成的工作流程裡根本不會被使用——
語言是這個分支存在的理由之一（讓 agent 與中文使用者共用同一套工具），
而那份工作是由前人打下的。這個分支在其上繼續往前：NX 寫回、地圖編輯器、
自我驗證與 agent 介面。

## Features

- Browse and edit WZ archive trees, including the reverse-engineered NX format.
- Preview canvas data, pixels, linked images, and animations.
- Search and replace values across loaded archives.
- Work with mobs, NPCs, skills, strings, cash-shop data, and game-data search.
- Import selected content from classic WZ or split Data clients after reviewing its names, effects, sounds, set data, and referenced art.
- Edit maps: layers, tiles, objects, foothold chains, portals, life spawns, minimaps.
- Validate a map's rules before trusting it, and undo a whole batch in one step.
- Save through a temporary candidate file that is flushed, reopened, and verified before it replaces the destination.

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) when building from source
- Microsoft Edge WebView2 Runtime (normally included with current Windows installations)

## Run from source

```powershell
dotnet restore MapleBench.sln
dotnet run --project MapleBench
```

To open the interface in the default browser instead of the desktop window:

```powershell
dotnet run --project MapleBench -- --browser
```

## Build

```powershell
dotnet build MapleBench.sln -c Release
```

Create a self-contained Windows build in `dist/standalone`:

```powershell
dotnet publish MapleBench/MapleBench.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:StaticWebAssetsEnabled=false `
  -p:EnableCompressionInSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:PublishReadyToRunComposite=false `
  -p:SatelliteResourceLanguages=en `
  -o dist/standalone
```

## Saving and data safety

Edits remain in memory until you save. For supported WZ saves, MapleBench writes a sibling candidate file, flushes it to disk, reopens and verifies it, and only then swaps it into place. When a destination is replaced, timestamped backups are retained by default. Split-client reference files are treated as read-only, and `.ms` containers are not overwritten.

These checks reduce risk; they are not a substitute for a separate backup. Keep original archives outside the working directory until you have tested the edited client.

## Repository layout

- `MapleBench` — the desktop host, web interface, application services, and embedded assets
  - `Services/Nx/` — the reverse-engineered PKG4 format: reader, writer, canvas codec
  - `Services/MapEditor/` — map editing, validation, batch operations
  - `Services/MapModel/` — the map data model
- `MapleMcp` — MCP server exposing 10 map tools to agents
- `dependencies/MapleLib` — the WZ parsing, serialization, cryptography, and packet dependency
- `docs/MAP-STRUCTURE.md` — measured map-structure specification
- `tools/probe/` — verification harnesses; `tools/anatomy/` — the sampler that produced the spec
- `JOURNAL/` — what was done, on what evidence, and what was not

NuGet package dependencies are restored automatically by the .NET SDK.

MapleBench is built on [MapleLib](https://github.com/lastbattle/MapleLib). See the repository history for the full contributor record.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).

The NX format work is an independent clean-room implementation. NoLifeNx
(AGPL-3.0) was used as a read-only reference for the binary layout; no AGPL code
is included in this repository.

MapleStory and related names and assets are trademarks or copyrighted works of Nexon and their respective owners. MapleBench is an independent project and is not affiliated with or endorsed by Nexon.
