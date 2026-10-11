# NX 直開技術路線 — 追風WZ編輯器 (MapleBench)

> 狀態:**已完成並驗證**。NX 讀取、轉換、渲染、img 匯出、**NX 寫回**全部通過驗收。
> 日期:2026-10-09

---

## 0. 結論

| 問題 | 答案 |
|---|---|
| NX 是什麼? | **自製 PKG4 二進位容器**,魔術 `0x34474B50`。**不是 FlatBuffers** |
| 有加密嗎? | **完全沒有**。全檔 plaintext。唯一壓縮是畫布像素的 LZ4/WebP |
| 讀取難度 | **低**。52-byte 頭 + 20-byte 定長節點記錄,無變長編碼、無 schema |
| 寫回難度 | **中**。樹可精確重建;畫布直傳位元組;代價見 §6 |
| 授權地雷 | NoLifeNx 是 AGPL-3.0。**必須 clean-room**,本專案已照規定做 |
| 目標是否封閉 | **是**。本機 16 個 `.nx` 全是 PKG4,與 OpenStory `NxFiles.h` **16/16 吻合** |

---

## 1. 格式規格

### 1.1 Header(52 bytes,packed)

```
0x00 u32 magic         = 0x34474B50 ("PKG4")
0x04 u32 node_count
0x08 u64 node_offset
0x10 u32 string_count
0x14 u64 string_offset
0x1C u32 bitmap_count
0x20 u64 bitmap_offset
0x28 u32 audio_count
0x2C u64 audio_offset
```

**四張表的落檔順序是「音訊在點陣圖之前」**,與欄位宣告順序相反:
**node block → string table → audio table → bitmap table**。
照欄位順序寫完整性檢查會把 16 個檔中的 3 個誤判成損壞。

### 1.2 Node 記錄(20 bytes,packed)

```
+0  u32 name       字串表索引
+4  u32 children   第一個子節點在節點表中的「索引」(非位元組偏移)
+8  u16 num        子節點數
+10 u16 type
+12    8 bytes     union,恆為 8 bytes
```

節點表是 **BFS 層序**。節點可被多個父節點引用(見 §1.8)。

### 1.3 七種節點型別

| type | 名稱 | union | 對應 WZ | 可帶子節點? |
|---|---|---|---|---|
| 0 | none | — | SubProperty | 是 |
| 1 | integer | i64 | Short/Int/**Long 合一** | 否 |
| 2 | real | f64 | Float/**Double 合一** | 否 |
| 3 | string | u32 索引(+4 bytes 補零) | String | 否 |
| 4 | vector | i32[2] | Vector | 否 |
| 5 | bitmap | u32 idx, u16 w, u16 h | Canvas | **是** |
| 6 | audio | u32 idx, u32 len | Sound/RawData | 否 |

> **`bitmap` 不是葉節點。** 1,298,868 個 bitmap 節點中有 **1,296,108 個(99.8%)帶著子節點**
> —— origin、delay、head、lt、rb、_inlink、_outlink。
> 實作早期把它當葉值,在 Morph.nx 上丟掉 8,977 個屬性(四分之一的檔案)。

實測:只有 `none` 與 `bitmap` 能帶子節點;其餘五型在 990 萬個節點中沒有一個帶過子節點。

### 1.4 字串池 / 畫布 / 音效

- **字串**:`u16 byteLength` + UTF-8(無 NUL 結尾)。整檔共享去重。
- **畫布**:節點存 `{index,width,height}`;payload 前有 `u32 compressedLength`。
  `RIFF????WEBP` → WebP(實測 100% 為 VP8L lossless);其他 → LZ4 block。
  解壓後一律 `width*height*4` bytes BGRA8888。
- **音效**:`audio_table[index]` 直指位元組,**沒有長度前綴**(點陣圖才有)。
  payload 前 82 bytes 是 WZ 音效標頭,第 82 bytes 起是 `FF F2`(MP3 sync)。**未加密。**

> 音效差點誤判:前 32 bytes 在所有檔案中相同,像密文;實際上那只是共用標頭。
> 判斷是否加密必須看過標頭之後的內容。

### 1.5 排序與重複名稱

- **排序成立**:0 個父節點的子節點未排序 → 二分搜尋讀取器安全
- **比較基準是 UTF-8 位元組順序**,不是 UTF-16 碼元順序
- **同名子節點合法**:`Quest.nx/Say.img` 有兩個 `1029`,`String.nx/Npc.img` 有兩個 `2104`
  → `WzImage.AddProperty` 會拋例外,必須改用 `WzPropertyCollection.Add`

### 1.6 NX 是 DAG,不是樹

| 檔案 | 節點數 | 走訪次數 | 多重父節點 |
|---|---:|---:|---:|
| Character.nx | 3,616,825 | 10,966,164 | **353,153** |
| Mob.nx | 539,770 | 591,815 | 21,263 |
| Npc.nx | 222,641 | 243,398 | 8,631 |
| Item.nx | 148,328 | 174,071 | 5,598 |

合計 **394,041 個節點被一個以上的父節點引用**。MapleLib 的屬性只能有一個 Parent,
因此轉換**必須為每一條路徑各產生一份**。

---

## 2. 證據鏈

1. `OpenStory-Beidou/vendor/NoLifeNx/nlnx/` — 完整 C++ 讀取器(AGPL,僅對照)
2. `MapleLib/.../WzToNxSerializer.cs` — 獨立 C# 寫入端,欄位順序與 (1) 吻合
3. `wz2nx_gui.exe`(IDA)— 原生 x64 + 靜態 LZ4
4. 16 個真實 `.nx` 檔(2.96 GB)實測
5. 兩份獨立委外研究:全生態 **0 個 `.fbs`、0 個 FlatBuffers 套件**

---

## 3. 實作(8 檔,約 2,500 行)

| 檔案 | 職責 |
|---|---|
| `NxArchive.cs` | mmap 零拷貝、header、4 張偏移表、結構完整性檢查 |
| `NxNode.cs` | 20B 定長節點視圖、7 型別 union、路徑解析 |
| `NxCanvas.cs` | 畫布/音效 payload 定位、編碼嗅探 |
| `NxCanvasDecoder.cs` | WebP(VP8L)+ LZ4 → BGRA → Bitmap |
| `NxPngProperty.cs` | 延遲像素的 `WzPngProperty` 子類,讓 `GetBitmap()` 自動走 NX |
| `NxToWzFile.cs` | NX → MapleLib 物件樹 |
| `NxWzExport.cs` | 讓 NX 樹能寫成 WZ(img 匯出) |
| `NxWriter.cs` | MapleLib 樹 → NX(寫回) |

**為什麼轉成 MapleLib 物件而不是 `WzNode`**:下游 141 個 endpoint、存檔路徑、匯入引擎
全部走 `WzFile`/`WzImage`/`WzImageProperty`;`WzNode` 只是地圖編輯器的編輯層。


---

## 4. 驗證

### 4.1 讀取器:C# vs Python

`nxparse.py`(純標準庫)與 C# 讀取器**獨立**實作,輸出同一組正規化欄位:

```
archives compared: 16   differing fields: 0
agree on every field including SHA-256 record digests
0 orphans, 0 unsorted
```

### 4.2 轉換:NX → MapleLib 樹(16/16 零分歧)

用 MapleLib 自己的枚舉器走訪產生的樹,與 NX 端**逐層鎖步比對**:

```
16 / 16 archives:  images=True  properties=True  canvases=True  first divergence: (none)
```

共轉出 **20,868,582 個屬性、2,208,332 張畫布**,總耗時約 18 秒。

### 4.3 端到端(實際啟動應用程式)

```
POST /api/files/open { path: "Morph.nx" }
  -> { kind: "nx", readOnly: true, dirty: false, dirtyNodeCount: 0 }
GET  /api/inspect?path=f1/0001.img/walk/0
  -> type=Canvas, "64 x 55", format=Format2
     head(-3,-51)  lt(-32,-55)  origin(32,55)  rb(32,0)
POST /api/search { query: "origin" }
  -> f1/1103.img/windspear/2/origin  "44, 85"
GET  /api/canvas?path=...             -> PNG,肉眼驗證正確
POST /api/files/save { fileId: "f1" } -> 400 明確拒絕,檔案零損傷
```

### 4.4 img 匯出:位元級無損

不經任何 Bitmap 的原始位元組比對(NX 解碼 vs MapleLib 寫入再解壓):

| 檔案 | 畫布 | 比對位元組 | 差異 |
|---|---:|---:|---:|
| Etc.nx | 10 / 10 | 73,224 | **0** |
| Item.nx | 27,265 / 27,265 | 473,517,652 | **0** |
| Morph.nx | 1,888 / 1,888 | 63,480,856 | **0** |
| **合計** | **29,163** | **537,071,732** | **0** |

> 曾誤判為「GDI+ 預乘 alpha 造成 ±7 偏移」。真實原因是 MapleLib 的 `CompressPng`
> 自動挑最窄格式,把畫布存成 BGRA4444(解壓後剛好是預期的一半)。
> 教訓:**拿 Bitmap 比 Bitmap 無法區分哪一邊失真;差異的比例(1:2)比數值(±7)更能指出問題。**

### 4.5 NX 寫回:樹等價性(16/16)

驗收標準是**按名稱與值配對的樹走訪**,不是記錄順序摘要。
理由:NX 讀取器只要求子節點「連續」且「依名稱有序」,全域編號無關緊要。

```
16 / 16 archives:  TREE EQUIVALENCE: IDENTICAL   (PASS=16 FAIL=0)
```

| 檔案 | 往返 | 原大小 | 寫出大小 |
|---|---:|---:|---:|
| Base.nx | 158 ms | 12,768 | 12,581 |
| Etc.nx | 712 ms | 3.5 MB | 3.5 MB |
| Item.nx | 875 ms | 16.7 MB | 43.1 MB |
| Mob.nx | 3.9 s | 817 MB | 920 MB |
| Map.nx | 15.4 s | 679 MB | 686 MB |
| **Character.nx** | **23.9 s** | 186 MB | **758 MB** |

---

## 5. 寫回的三個關鍵實作決定

**5.1 畫布 payload 直傳(不是最佳化,是可用性)**

第一版會解碼再用 SkiaSharp 重新編碼。實測後推翻:

- 重編碼 Mob.nx 的 91,093 張畫布**跑了 28 分鐘未完**,記憶體 5.4 GB
- 按比例推算 Character.nx 的 1,831,573 張要 **9 小時**
- 產出還比原檔大 65%(SkiaSharp 走 VP8X,原生檔是 VP8L)

**但 NX 畫布本來就是合法 WebP。** 重編碼只是換個等價編碼,沒有人受益。
改成從來源檔案直接複製位元組後:Mob.nx 3.9 秒、Character.nx 23.9 秒,畫布保持 VP8L 原樣。

**5.2 字串節點的 union 必須補齊到 8 bytes**

只寫 u32 索引會讓後面每筆記錄位移 4 bytes。Base.nx 有 106 個字串節點 × 4 = **424 bytes**,
正好把字串表推進節點區塊裡,讀取器判定 malformed。

**5.3 同名子節點必須穩定排序**

NX 允許同名子節點,而讀取器用名稱定位時拿到的是**先出現**的那個。
`List.Sort` 的 tie-break 必須在排序**之前**就填好順序值(Quest.nx 的 `Say.img` 驗證到了)。

---

## 6. 已知限制(全部實測,非推測)

1. **型別塌縮**:NX 只有 i64 與 f64,WZ 的 Short/Int/Long、Float/Double 全部合併,無法還原。
2. **音訊格式標記**:`SoundType` 讀成 `Raw` 而非 `MP3`,需改 vendored 的 MapleLib 才能修。
3. **寫回的 DAG 膨脹**:Character.nx 186 MB → 758 MB,需讓 MapleLib 支援多父引用才能根治。
4. **被編輯的畫布需重新編碼**:直傳只對未修改的畫布有效,效能未量測。

## 7. 目前維持唯讀的原因

寫回能力已完成並驗證,但 NX 仍以唯讀開啟:

- 唯讀之下沒有可寫回的變更,寫回此刻沒有實際用途
- 要讓它有用必須先允許編輯 NX,那是會改變資料風險的行為決定
- Character.nx 寫出後膨脹 4 倍,若要實用應先解決 DAG 共享

| 形態 | 內容 | 風險 |
|---|---|---|
| **保守**(現況) | NX 唯讀 + img 匯出(已驗證位元級無損) | 零 |
| 積極 | NX 原生編輯 + 寫回,需解決重編碼效能與 DAG 膨脹 | 中 |

## 8. 驗證工具

`agent-wz/src/NxVerify` — 獨立驗證器,不依賴 UI:

```
nxverify <file.nx>...        # header、樹結構、型別分布、SHA-256 記錄摘要
nxverify --convert <file.nx> # NX->MapleLib 轉換的逐層鎖步比對
nxverify --canvas <file.nx>  # 實際解碼畫布
nxverify --canvas-save <f>   # 輸出 PNG 以肉眼驗證
nxverify --imgexport <f>     # img 匯出 + MapleLib 讀回 + 像素比對
nxverify --pixelbytes <f>    # 原始位元組比對(不經 GDI+)
nxverify --nxwrite <f>       # 寫回 + 樹等價性驗收
nxverify --unsorted <f>      # 找出排序或重複名問題
```

`nxparse.py` 是獨立的 Python 參考解析器,兩者互相對照。
`restore-eol.py` 修復編輯造成的換行符號雜訊——這個 repo 有多個檔案在 git index 中是
mixed line endings,任何編輯都可能產生不可審閱的 diff(其 .gitattributes 註解裡正好記錄過這個教訓)。

