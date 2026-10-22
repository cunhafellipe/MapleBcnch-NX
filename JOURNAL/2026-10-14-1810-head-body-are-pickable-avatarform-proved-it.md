# 2026-10-14 18:10 — Head/Body 確實可選：我先前的「永不提供」斷言被對標原始碼推翻

## 結論

`maplebench-sim-work` skill 與 `avatar.js` 註解裡都寫著：

> `Head` and `Body` are empty on purpose and always will be … **Never offer them as pickers.**

**這是錯的。** 對標 `MapleNecrocer` 的 avatar 編輯器有這兩個選擇器。

## 錯在哪：讀到一個真事實就當成完整事實

支撐那個斷言的兩個觀察都是真的：

1. `Body.cpp:31-32` 確實以名字讀 `00002000.img` / `00012000.img`
2. `/api/children?path=f3/Head` 確實 404

但兩者只證明**沒有 `Character/Head/` 這個資料夾**。對標實際是這樣做的：

| 行號 | 內容 |
|---|---|
| `AvatarForm.cs:496-499` | `case "Head": case "Body": CharacterDir = ""; break;` |
| `AvatarForm.cs:538-547` | `CharacterDir` 空時列舉的是 **archive 根層**，不是資料夾 |
| `AvatarForm.cs:559-562` | `case "Head": if (img.Text.Left(4) == "0000") continue;` |
| `AvatarForm.cs:563-566` | `case "Body": if (img.Text.Left(4) == "0001") continue;` |
| `AvatarForm.cs:627/629` | 縮圖 `front/head` / `stand1/0/body` |

Head 排除 `0000*`、Body 排除 `0001*`，在只有兩種前綴的集合上**等價於** Head=`0001*`、Body=`0000*`。
實測檔案：`00012000`–`00012011` 共 9 張頭皮，���00002000`–`00002011` 共 9 張體型。

**教訓：一個 404 只證明「那條路徑沒有東西」，不證明「這個東西不存在」。**
同一個 404 也會由打錯的路徑回來 —— 而我把「沒有 Head 資料夾」讀成了「沒有頭部選擇器」。

## 改動

**伺服器**
- `EquipSlotTable.cs`：新增 `EquipSlotDef.RootPrefix`；`Accepts()` 開始遵守它；
  `Def("Head","",rootPrefix:"0001")`、`Def("Body","",rootPrefix:"0000")`
- `LookService.SlotImages`：無資料夾的槽改列舉根層（新增 `RootImages`）
- `LookService.Compose`：Head/Body 改接到 `ComposeEquipped` 的 `body`/`head` 參數，
  **不再塞進 `equipped`** —— 選它們是「換掉角色本身的地基」，不是疊一層
- `LookService.ResolveImage`：folder 參數改 nullable
- 新增 `ImageIdOf`：同時接受 `01000000` 與 `01000000.img`

**客戶端**
- `avatar.js`：`PICKABLE_SLOTS` 加入 `Head`、`Body`
- `walkview.js`：`FOLDER_OF` 給空字串、新增 `ROOT_PREFIX_OF`、
  `THUMB_PATH` 加兩筆、`ACCEPTED_BY` 認前綴、
  `namesOf` 在資料夾為空時列舉根層、`appendPage` 用單一 `base` 組路徑

## 中途踩到並修掉的兩個缺陷

### 1. 條件寫反，把 17 個裝備槽全清空

我寫成 `if (!root && def.RootPrefix is null) return [];` —— 讀出來是
「有資料夾**且**無根前綴就回傳空」，剛好把所有裝備槽擋掉。

實測立刻現形：

```
Head     count=9  ✓        Weapon-1  count=0   ✗（本來 1009）
Body     count=9  ✓        Cap-1    count=0   ✗（本來 908）
Coat     count=0  ✗        Hair-1   count=0   ✗（本來 1518）
```

改成 `if (root && def.RootPrefix is null) return [];` 後全部復原
（Weapon-1=1009、Cap-1=908、Coat=462、Hair-1=1518、Face-1=540）。

> **教訓：動到 `SlotImages` 之後，要把每���槽的 count 都重讀一遍，不是只讀你改的那兩個。**
> 一個「新的槽有數字了、舊的槽全變 0」的狀態，比功能沒做出來更貴，因為它看起來像成功了。

### 2. 槽位規則只擋在 UI，沒擋在伺服器

`Accepts()` 原本**只**被 `SlotImages` 呼叫。實測：

| 輸入 | 修正前 | 修正後 |
|---|---|---|
| `{"Head":"00002004.img"}`（體型sheet塞進頭部槽） | parts=18，無錯誤 | `Head: 00002004.img is not a Head id` |
| `{"Cap-1":"01302000.img"}`（劍塞進帽子槽） | 靜默合成 | `Cap-1: 01302000.img is not a Cap-1 id` |
| `{"Coat":"00002000.img"}` | parts=15（少一層） | `Coat: 00002000.img not in Coat/` |

UI 有過濾，但**過濾的 UI 是便利不是契約**。閘門現在在 `Compose`：
`if (!ImageIdOf(image, out id) || !def.Accepts(id)) unresolved.Add(...)`

## 驗證（獨立實例 :5591，未動 :5590）

```
GET /api/simulate/look/slots
  Head (root) 9 / Body (root) 9
  Weapon-1 1009  Cap-1 908  Coat 462  Pants 404  Longcoat 491
  Cape 178  Shield 58  Glove 236  Shoes 417  Hair-1 1518  Face-1 540
  FaceAcc 139  Glass 90
```

縮圖路徑 7/7 全通：
`00012000.img/front/head`、`00002000.img/stand1/0/body`、
`00012009.img/front/head`、`00002009.img/stand1/0/body`、
`Hair/00030020.img/default/hairOverHead`、`Face/00020000.img/default/face`、
`Weapon/01302000.img/info/icon`

瀏覽器實測（UI 14 個頁籤，含 Head/Body）：

```
Head 頁籤  圖=9   ok=9   壞=0   首圖 f1/00012000.img/front/head
Body 頁籤  圖=9   ok=9   壞=0   首圖 f1/00002000.img/stand1/0/body
aria 標籤: Head 00012000..00012005, 00012009..00012011
```

**畫面實際部件路徑**（讀 `avatar.part.frames[idx].parts[].path`，
不是讀 slotMap 字串 —— 換了 slotMap 不等於換了畫面）：

```
Head=00012009 → ['00002000','00012009','00020000','00030000',裝備...]
Head=00012000 → ['00002000','00012000','00020000','00030000',裝備...]
Body=00002009 → ['00002009','00012000','00020000','00030000',裝備...]
Body=00002000 → ['00002000','00012000','00020000','00030000',裝備...]
```

四次都只有指定的那一張換掉，其餘 9 個槽完全不動。

## 部署狀態（誠實記錄）

- JS：`wwwroot` 與 `bin/.../wwwroot` md5 一致（`avatar.js` cd708bf0…、`walkview.js` 65a3cf2a…），**線上已生效**
- C#：`bin/.../MapleBenchX.dll` mtime 15:06:41 < 最新 `.cs` 17:37:39
  —— **bin 裡的 DLL 是舊的**。原因：`MSB3021/MSB3027` 顯示 pid 19300（`:5590` 的主線伺服器）
  持有 `bin/Release/net10.0-windows/MapleLib.dll`，要在不打斷主線的前提下推進到 `bin/`
  必須殺掉它。約束是「絕對不要 kill/restart MapleBenchX」，所以沒做。
  全部 C# 行為改在 :5591 獨立實例驗證完畢，主線自然重啟時即載入。

## 探針自身的三次錯判（記下來因為它們都長得像結論）

1. `parts[-1].split('/')` 取到葉節點名（`head`）而不是 sheet —— 印出 `sheets=[]`，
   看起來像「沒換」
2. 直接設 `viewer.equipSlot='Head'` 不重繪，於是拿到 Cap-1 的格子配 Head 槽名，
   寫出 `Head: 01000004.img` —— **這其實暴露了伺服器缺閘門**，否則會被我當成探針錯誤丟掉
3. `avatar.part.frames[i]` 假設是 part 陣列，實際是 `{frame,delay,parts,...}` 包裝

第 2 點是這輪唯一「探針錯了但結論對了」的情況：探針製造的髒資料，
剛好證明了伺服器少了一道該有的驗證。