# TASK: MapleBench 自治檢察官 — 只回報缺陷，不要修

## 你的角色

你是**獨立檢察官**。找出 MapleBench 模擬器的真實缺陷，**回報發現，不要動任何程式碼**。
另一個 agent 正在同一棵樹上工作；你改任何檔案都會製造衝突。

## 唯一產出

把結果寫到 **`FINDINGS.md`**（就在這個目錄），格式：

```
## F<n>  <一句話標題>
- **檔案:行號**: Services/MapEditor/MobPatrol.cs:648
- **類型**: 邏輯錯誤 / 與對標不符 / 沈默失敗 / 死碼 / 錯誤前提
- **證據**: 具體的數字、欄位值、或你實際跑過的命令與輸出
- **重現**: 一條可以貼進終端機的命令（curl 或 python），別人照跑會得到同樣結果
- **對標**: C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\Client\<file>.cs:<行>（若適用）
- **影響**: 使用者會看到什麼
```

沒有重現步驟的發現不要寫進去。沒有證據的不要寫。

## 領域規則（我已經付過學費的，不要重蹈）

1. **絕對不要猜 WZ 路徑、節點名、part 名或 item id。** 路徑錯和資源不存在都回 404，
   訊號會被摧毀。要查就去查 `_necrocer_src/MapleNecrocer` 或專案本身。
2. **註解寫的行號不保證是對的。** 註解說 `Mob.cs:356` 但程式寫成別的東西 ——
   這種「註解對、程式錯」實際發生過。**每個『忠實移植』都要自己去開那個行號比對。**
3. **量測本身要驗。** 用 `(mobId, foothold)` 當 dictionary key 會讓兩隻怪互相覆蓋，
   測出「完美對調」的假象。
4. **「跑起來」不是驗證。** 每個發現都要真的跑出數字。

## 已知狀態（不要重複報告，但要懷疑我的結論）

我今天實測修好並驗證了四項：
- 怪物左右瘋狂翻轉（根因：`Mob.cs:354` 的 `switch(Random.Next(200))` 是四個精確值無 default，
  被寫成四個區間）
- 角色左右鏡像（`drawAvatar` 只鏡像位置沒鏡像圖）
- 角色水平邊界（改成位置性 + 夾位置）
- 動畫審計器（自設計）

**兩個我沒修、已知存在**：
- **攻擊從不生效**：角色貼著怪（28px）、`onGround=true`、`facing` 正確、
  四種武器都試過，`damage` 恆為 `[]`，`action` 永遠是 `walk`/`stand`。
- **無輸入時角色會漂移**：送 `{}`（什麼都沒按），x 仍以約 2.2px/幀右移，`action=walk`。

**我要你做的是找我没找的**，不是複述我已知的。

## 怎麼測

服務在 http://127.0.0.1:5590（可能沒開；沒開就用 `dotnet run` 開，或只做靜態審查）。
開圖用：
```
curl -s -X POST http://127.0.0.1:5590/api/files/open-many -H "Content-Type: application/json" \
  -d '{"paths":["C:/MUWORK/GAME/MPSHS/Map.nx"]}'
```
建 session：
```
curl -s -X POST http://127.0.0.1:5590/api/simulate/session \
  -H "Content-Type: application/json" \
  -d '{"path":"f6/Map/Map0/000000002.img","weapon":"1302000.img"}'
```
（檔案 id 每次重啟會變，`f6` 可能變成別的。先 `curl -s http://127.0.0.1:5590/api/files` 查。）

跑幀：`POST /api/simulate/session/{id}/frame`（單幀）或 `/frames`（`{"count":10,"first":{...}}`）。

## 優先搜尋方向

1. **`Swing()` 為什麼永遠不命中** — 這是最有價值的一條。從 `MapSimSessionStore.Swing()`
   往下追：`weapon.Box.IsEmpty`？`reach.Overlaps(mobBox)`？`MobAnimation.For` 取到的
   stance/frameIndex 對不對？哪一步先失敗，用數字證明。
2. **角色無輸入漂移** — `CharacterPhysics` 哪裡沒歸零速度。
3. 其他沈默失敗：欄位送出但沒人讀、端點回 200 但實際沒做事、例外被 `catch {}` 吞掉。
4. 與對標不符的移植（記得規則 2）。

## 範圍限制

- **只寫 `FINDINGS.md`。不要改任何 .cs / .js 檔案。**
- 預算的前一半就要開始寫檔，不要讀完才動手。
- 寧可少而實，不要多而虛。找不到就寫「我沒找到」，那也是有價值的答案。