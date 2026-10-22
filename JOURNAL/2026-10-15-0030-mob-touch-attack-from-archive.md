# 2026-10-15 — 第 7 項：怪物攻擊。傷害與權限改用封存自己的數字

## 先更正前一輪的錯誤結論

前一輪（`2026-10-14-2300`）寫「這個存檔的怪沒有 `attack1`，所以 A 路線做出來等於沒做」。

**那個結論是錯的，而且是我自己的量測壞掉造成的。**

`/api/children` **只接受 `path` 參數** —— 根本沒有 `limit` / `offset`
（`Api/Endpoints.cs:685`）。我附加的 `&limit=3000&offset=…` 全部被忽略，
而我的迴圈裡又有 `-EA SilentlyContinue` 吞掉錯誤，於是「只取到 3 筆」被
我寫成「分佈是這樣」。

實際上端點完全正常：

```
GET /api/children?path=f5        -> HTTP 200, 346ms, 2054 筆
```

**這是本專案第四次記錄在案的「量測工具偽造結論」，全部是我自己寫的。**
前三次：PowerShell 的 `$input` 自動變數、`@($null).Count` 回 1、JS 測試挑了
WASD 的陰影鍵當正樣本。

## 全庫真實分佈（`mob_attack_scan.py`，16 執行緒，2053 隻全部可達、0 個傳輸失敗）

| 節點 | 數量 |
|---|---|
| `info/attack1` | **0** |
| `info/firstAttack` | **522** |
| `info/bodyAttack` | **1945** |
| `info/icon` | **0** |

`info/attack1` 真的是 0（前一個結論在這一點上碰巧是對的），
但 **`bodyAttack` 有 1945 隻** —— 而測試地圖上的 `2220100` 與 `2110200` 都有 `bodyAttack = 1`。

## Route A 的真正內容

C++ 對標的接觸傷害：

```
Mob.cpp:69     touchdamage = info["bodyAttack"].get_bool();
Mob.cpp:1848   MobAttack Mob::create_touch_attack() const {
Mob.cpp:1850       if (!touchdamage) return MobAttack();
Mob.cpp:1853       int32_t power = watk + status_value(WATK);
Mob.cpp:1856       int32_t attack = randomizer.next_int(minattack, maxattack);
```

**改動**：`MobPatrol.MobDamage` 從自己發明的 `MaxHp / 8` 換成封存的 `info/PADamage`，
並且用 `info/bodyAttack` 當閘（`Mob.cpp:1850`）。

| | 改前 | 改後 |
|---|---|---|
| 2220100 傷害 | `350/8` = **43** | 封存 `PADamage` = **90** |
| 2110200 傷害 | `300/8` = **37** | 封存 `PADamage` = **90** |
| 沒有 bodyAttack 的 108 隻 | 照樣打人 | 不能打（`Mob.cpp:1850`）|

**沒有**加擲骰子，也沒有加屬性值 —— `Mob.cpp:1856` 的區間擲骰是建立在
`power = watk + WATK 屬性` 上，而這個模擬器沒有屬性表。只取封存的 `watk` 本身
是對那條公式的第一個項目的誠實讀法。

## 觸發時機維持原樣，並且是有理由的

`Aggro` 仍然只在**被玩家打到**之後才設。理由：

- C# 對標 `MapleNecrocer/Client/Mob.cs:434-460`：怪只有挨打才反應，沒有仇恨半徑。
- C++ 的觸發在**伺服器端**：`MapMobs::create_attack`（`MapMobs.cpp:306-312`）
  只是轉呼叫 `create_touch_attack`，**觸發時機根本不在客戶端**，沒有可抄的程式碼。

所以「什麼時候打」是這個專案自己決定的，「能不能打」和「打多重」是存檔決定的。
前者維持對標 C# 的規則，後兩者換成存檔的數字。

## 實測

```
map path: f4/Map/Map1/100000004.img
target mob 2220100 x=2091 hp=350
walked to x=2064.975  player hp 50/50
provoked it with one swing
  +18 frames: mob hit for 50 -> player hp 0/50
```

玩家滿血只有 50，而封存的 `PADamage` 是 90，所以一擊必殺、`playerDamage` 報 50
（吸收到 HP 歸零為止）。舊的 `MaxHp/8` = 43 會需要兩下 —— 這是看得見的差別。

## 回歸

建置部署完成，三處 md5 一致（`avatar.js` 全程未動）。
爬梯 5/5、6.2 姿勢、6.4 施放、JS 邏輯 24/24 全過。

## 第 8 項的前提也順帶確立了

`info/icon` 全庫 **0** —— 怪沒有圖示節點 confirmed。縮圖只能用 `stand/0`。

## 環境教訓

`Map.nx` 開啟需要約 **13.5 GB**（它 678 MB，NX 會轉成完整記憶體樹）。
這一輪有一次因為只剩 10.6 GB 而開不起來，導致 Map.nx 缺席、檔號位移、
後面整串測試全 404 —— 看起來像程式壞了，實際是記憶體。
遇到「地圖突然 404」先看 `/api/files` 少了誰。
