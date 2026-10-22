# 2026-10-14 10:35 — NX 美術資源載入：已解決，而我花了 20 分鐘假裝它沒解決

接續 [1020](2026-10-14-1020-benchmark-audit-player-status.md)（同一輪，不覆蓋）。

---

## 1. 使用者的兩次打斷，都打在同一點上

> 「我有一個問題 你現在是在驗證數學底層邏輯 我想問 nx美術資源載入的問題解決了沒?」
> 「為什麼需要猜? 對標模擬器不是已經給你一個正確答案了嗎」
> 「你只要有任何不確定 不知道 就去看對標模擬器專案 不要一直亂寫」

三次都是同一個指控：**我在猜，而答案本來就有。**

---

## 2. 誠實的答案

**NX 美術資源載入早就解決了。我用錯方式重新驗證它，還把路徑猜錯了。**

- 專案既有 `CharLookComposer`（五錨點合成）每天都在做這件事，且已驗證
- 我卻手寫探針去猜路徑，然後把「猜錯」報告成「7/7 角色美術失敗」

**那 7 個失敗全部是路徑猜錯，沒有一個是資源缺失。**

---

## 3. 猜錯的具體內容（這是被打斷的原因）

我憑空寫了這些部件葉名：`default/cap`、`default/body`、`default/cape`。
真實答案（由專案合成器輸出）：

| 我猜的 | 實際 |
|---|---|
| `Cap/01000000.img/default/cap` | `Cap/01000000.img/stand1/0/default` + `defalutAc`（**原檔拼字錯誤**） |
| `Coat/01040000.img/default/body` | `Coat/01040000.img/stand1/0/mail` + `mailArm` |
| `Shoes/01070000.img/default/body` | `Shoes/01070000.img/stand1/0/shoes` |
| `Weapon/01302000.img/default/body` | `Weapon/01302000.img/stand1/0/weapon` |

**葉名因裝備類型而異，無法通用猜。**
我還把 pose 寫成 `default`（那只有 Hair/Face 用），裝備用的是與身體同步的 `stand1`。

`defalutAc` 這個拼字錯誤尤其說明問題：**它不可能被猜到，只能被讀到。**

---

## 4. 對標給的權威規則（`MapleNecrocer/Client/MapleCharacter.cs`）

路徑公式：

```
Character/<GetDir(ID)>/<ID>.img[/<WeaponNum>/]<State>/<Frame>/<Image>
```

`GetPart(ID)` 以 `ID / 10000` 決定部件：

```
0 → Body     1 → Head     2,5 → Face     3,4,6 → Hair
100 Cap   104 Coat   105 Longcoat   106 Pants   107 Shoes
108 Glove  110 Cape  109 Shield     121-160 Weapon   170 CashWeapon
```

`Image`（葉名）由呼叫端列舉，不是推導：
`body` `arm` `head` `hair` `face` `hairOverHead` `backHair` `hairBelowBody`
`weapon` `weapon1` `weapon2` `lHand` `ear` …

**這也解釋了我先前的一個錯誤結論。**
我在 10:05 的日誌寫「`0002002`/`0002004` 在 Mob.nx 裡根本不存在，是資料如此，非 bug」。
實際只試過 `f1/`、`f2/`、加/不加 `Mob/` 前綴四條路徑，
**沒有查它們是否在其他檔案**——而那個判斷我寫進了日誌當成定論。**未經查證的推論被寫成了事實。**

---

## 5. 專案自己就有答案

```
GET  /api/simulate/look/slots   → 每槽真實數量 + 樣本 id
POST /api/simulate/look         → 組好的部件清單（path/anchor/navel）+ unresolved
```

`slots` 直接給出可用的 id：
`Cap-1 01000000`(908) `Coat 01040000`(462) `Pants 01060000`(404)
`Shoes 01070000`(417) `Glove 01080000`(236) `Cape 01102000`(178)
`Weapon-1 01302000`(1009) `Hair-1 00030000`(1518) `Face-1 00020000`

`POST /api/simulate/look` 一次回 **15 個部件、`unresolved: []`**，
路徑全部現成：`f3/Cap/01000000.img/stand1/0/defalutAc` 等。
**我一個葉名都沒猜到，全部是它給的。**

---

## 6. 驗收：8 個姿勢 × 全部部件，119/119 像素解碼成功

```
OK   stand1   部件 15 成功 15 不透明 4,480  unresolved=[]
OK   walk1    部件 15 成功 15 不透明 4,457  unresolved=[]
OK   jump     部件 17 成功 17 不透明 4,754  unresolved=[]
OK   alert    部件 17 成功 17 不透明 4,577  unresolved=[]
OK   swingT1  部件 16 成功 16 不透明 4,616  unresolved=[]
OK   sit      部件 14 成功 14 不透明 3,865  unresolved=[]
OK   prone    部件 15 成功 15 不透明 4,934  unresolved=[]
OK   ladder   部件 10 成功 10 不透明 4,885  unresolved=[]

合計: 部件 119  解碼 119/119  總不透明像素 36,568  輸出 119 張 PNG
```

每張都經過**手寫 PNG 解碼器**驗證：IHDR 尺寸、IDAT zlib 解壓、逐行濾波反算、
alpha 通道非零像素計數。**不是只看 HTTP 200。**

MOB 美術另測 4/4（`stand` 37×26、`move` 36×25、`die1`/`hit1` 43×33），
與角色美術走**不同結構**：MOB 的 `stand/0` 本身就是 Canvas，
角色的 `stand1/0` 是 SubProperty，底下才是部件 canvas。

輸出：`C:\Users\e7896\AppData\Local\hermes\cache\scratch\nx2\`（119 張）

---

## 7. 記進 skill 的規則

已 patch `maplebench-sim-work`，新增三條：

1. **絕不猜 WZ 路徑/節點名/部件名/物品 id。** 兩個答案永遠現成：
   對標專案的 `GetPart`/`GetGearSlot`，或本專案的 `CharLookComposer` + `EquipSlotTable` + `/api/simulate/look`。
   附記本次實測代價：猜錯導致「7/7 角色美術失敗」的假結論。
   **關鍵：猜錯的路徑與真實失敗回傳同一個 404，所以猜測還會摧毀訊號本身。**

2. **`/api/canvas` 的 404 有三種意思**，讀訊息再下結論：
   - `This node has no image.` → 節點是 SubProperty 容器，canvas 在子層
   - `'X' not found under 'Y'.` → 路徑本身錯（猜的，或 id/stance 錯）
   - `/api/node` 就 404 → id 或 stance 不存在

3. 原本的「不要問可查的問題」保留。

---

## 8. 這一輪真正學到的

**不是「NX 載入有問題」，是「驗證方法有問題」。**

我連續三輪的驗收（背景渲染、戰鬥、對標）都在用手寫探針重新發明專案已經有的能力：
`wz_map_render` 存在我還是去改 `MapPainter`；`/api/simulate/look` 存在我還是去猜部件名。

**驗證的成本不在於寫探針，而在於確認這個探針是否必要。**
已經被驗證過的東西不需要重新驗證——需要重新驗證的是**它是否還成立**，
那只要一個呼叫加一次讀取，不是二十次猜測。