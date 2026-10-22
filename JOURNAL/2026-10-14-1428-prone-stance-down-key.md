# 修正：方向鍵下不再停在 stand，趴下姿勢有了

時間：2026-10-14 14:28（追加，不覆蓋）

## 症狀
按住 ↓ 240 個 frame，`action` 240/240 都是 `stand`，畫面沒有趴下。

## 為什麼會卡住
`MapSimSessionStore.cs:980` 把 DOWN 折成 `climb = -1`（爬梯下降）。
沒有梯子時 `FindLadderForClimb` 回 null → climb 分支被跳過 →
`LastAction` 保持上面賦的 stand/walk。所以 DOWN 永遠不會變成別的姿勢。

## 修法：DOWN 的兩個意思拆成兩個參數
同一顆鍵的兩個語義不能是同一個參數，所以分開傳：

- `climb`  = DOWN 當「下梯」
- `prone`  = DOWN 當「趴下」，就是沒有梯子可下時的意思

`CharacterPhysics.StepCore` 用「哪個分支已經拿走這個 tick」來分辨：
climb 分支在 `:281` return（找到梯子）、ladder-top 在 `:292` return（已在梯上）。
控制流走到我新增的判斷時，梯子已經明確拒絕了這次 DOWN —— 那正是趴下的條件。

插在三個 early-return 之後、prone 判斷之前（prone 優先，趴著不能揮擊）。

## 驗證
- 伺服器：按住 ↓ → `action: "prone"`；放開 → `action: "stand"`
- 客戶端：`avatar.load('prone')` 成功 → `state: 'prone'`, `frames: 1`
  （封存實測 `/api/simulate/look/stances` 回 prone frameCount=1，所以是 hold 不是 cycle）
- 畫面：合成圖確認角色是水平伸展（頭在左、身體向右延伸），不是站姿，臉可見
- 無 regression：平地走 y 振幅仍 0px

## 順帶
對標 Stance.h 的 enum 還有 ALERT / DEAD / SIT / FLY / SWING* 未接線。
SWING 的 stance 怎麼依武器分類（裸手/斧/劍 → swingT/swingO/swingP）正在查對標，
不在這裡猜。
