# MapleBench

MapleBench is a Windows WZ editor and dependency-aware client importer for MapleStory. Browse and edit archive data, preview visual assets, work with common game-data sections, and move content between client versions with verified saves.

> [!IMPORTANT]
> Always work on copies of game data and keep an independent backup. MapleBench verifies its output before replacing a destination and normally creates timestamped backups, but no editor can protect against every damaged source file, storage failure, or interrupted write.

---

# 專案狀態在哪裡

**專案控制權威在 [`pm/`](pm/README.md)。** 這份 README 說這個工具是什麼；
`pm/` 說這個專案現在到哪裡、接下來做什麼、以及為什麼。

```powershell
python tools/pm.py status     # 整個專案，一個畫面
python tools/pm.py next       # 接下來 3 件事
python tools/pm.py show W-030 # 單一工作項目的完整內容
python tools/pm.py check      # 結構稽核
python tools/pm.py gate       # 建置、部署、跑完所有門檻
```

- 狀態只存在 `pm/work/*.md` 的 frontmatter，`tools/pm.py` 是唯一寫入者。
- `pm/STATE.md` 由 `status` 生成，不可手改。
- **沒有 `evidence=` 就不准 close 一個工作項目**（工具會拒絕）。
- `JOURNAL/` 是歷史，**不是**用來回答「現在狀態是什麼」的。
  見 [`pm/archive-index.md`](pm/archive-index.md)。

新接手（人或 agent）請先讀：`pm/README.md` → `pm/RULES.md` →
`pm/GATE.md` → `pm/TRAPS.md` → `pm/PARITY.md`。五分鐘內可以上手。

---

# 這個專案是誰做出來的

先講人，再講功能。

**這個分支不是某一個人做出來的。** 下面每一位都是它的作者——
少了任何一位，它都不會是現在的樣子。

### 🧑‍💻 主要貢獻者

| | 貢獻 |
|---|---|
| **[xkiro-dev](https://github.com/xkiro-dev)**<br>*上游 MapleBench* | **整個 WZ 編輯器的基礎**。這個分支從它分出來 |
| **追風技術 陳維克** | **繁體中文優化版本**。沒有那份介面，中文使用者在這個工作流程裡根本不會用這套工具 |
| **[g9502995](https://github.com/g9502995)** | **通用 WZ/IMG/NX 資產工具（22 個 `wz_asset_*`）與在 NX 封裝內建立地圖**。直接推送，19 個檔案 +1,261 行 |
| **[e78-png](https://github.com/e78-png)** | **本分支的整合與發布**。繁中版面、Discord 通知、MCP 伺服器與合併協調 |
| **AI agent** | **實作、除錯、驗證每一個環節**，包含大部分程式碼撰寫。多項關鍵修復是 agent 實跑後找出來的，不是讀程式碼猜的 |

### 為什麼他們排前面

這個分支的每一行能力，背後都有具體的人：

- **陳維克的繁中版**是地基。語言是這個分支存在的理由之一——
  讓 agent 和中文使用者用同一套工具共用一個介面。那份工作在其他人動手之前就已經完成了。
- **g9502995 的 22 個通用資產工具**讓這套東西不再只會做地圖：
  現在 agent 可以操作任意 WZ/IMG/NX 節點、開來源、加節點、匯出 canvas。
  同一批提交也修好了「NX 封裝內無法建立新地圖」——那原本不是格式限制，
  是一個沒被轉換的型別檢查擋住了所有 NX 目標。
- **xkiro-dev 的 MapleBench** 是整套架構的來源。NX 支援、地圖編輯器、
  自我驗證與 agent 介面都是建構在它之上。

`JOURNAL/` 記錄了每一輪做了什麼、憑什麼這麼說、以及沒做什麼。
**那個日誌比提交訊息更值得讀**——它連 agent 犯過並自己抓到的錯都留著。

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
| **Agent 介面** | 無 | `MapleMcp` — 47 個工具的 MCP 伺服器（17 地圖 + 8 封裝拆分 + 22 通用資產），agent 只經 MCP 就能跑完整流程 |
| **通用資產操作**（g9502995） | 無 | 22 個 `wz_asset_*` 工具：任意 WZ/IMG/NX 節點的開啟、新增、刪除、改名、搜尋、匯出、上傳 canvas、撤銷 |
| **NX 內建地圖**（g9502995） | 無 | 可直接在 NX 封裝裡建立新地圖——原本所有 NX 目標都會被錯誤地拒絕 |
| **繁體中文** | 無 | 全介面繁中，2,321 條翻譯。執行期切換，不需重啟 |
| **地圖結構規格** | 無 | `docs/MAP-STRUCTURE.md` — 10 張真實地圖解剖出的規格，每條規則都有出處 |
| **可玩性模擬** | 無 | 模擬器：用客戶端物理走一遍地圖，驗證角色走不走得過去；鏈完整但地面中斷的地圖，`/validate` 抓不到 |

### 協作方式

這個分支是**多人與 AI agent 協作**的成果，詳細貢獻見上方
「這個專案是誰做出來的」。其中多項關鍵修復（NX 素材調色盤、
放置後存檔崩潰、`t()` 字串串接的查表錯誤）**都是 agent 實跑驗證後
找出來的，不是讀程式碼猜的**。

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
