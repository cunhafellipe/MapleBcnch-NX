/**
 * Traditional Chinese (zh-Hant) strings.
 *
 * Keyed by the English source text, because that is what the code says at the
 * call site -- see the long comment at the top of i18n.js for why.
 *
 * Style follows the project's own Traditional Chinese naming (追風WZ編輯器) and
 * the vocabulary of the reference client, so the same idea does not arrive in
 * two guises: 存檔 not 保存, 節點 not 结点, 屬性 not 属性, 匯出/匯入 rather
 * than 導出/導入, 訊息 not 信息. Interpolation placeholders ({count}) are part
 * of the key and must survive translation.
 *
 * Anything absent here falls back to the English text, with one console warning
 * per key, so a gap shows up in the console rather than as a blank label.
 */

export const zhHant = {
  // ---- brand / shell ----------------------------------------------------
  'MapleBenchX': 'MapleBenchX',
  'WZ & NX Workbench': 'WZ 與 NX 工作台',
  'Go to a node or command': '跳至節點或指令',
  'Jump to any node, or run a command · Ctrl P': '跳至任何節點，或執行指令 · Ctrl P',
  'Explorer': '檔案總覽',
  'Editors': '編輯器',
  'Browse and edit any WZ node · Ctrl 1': '瀏覽並編輯任何 WZ 節點 · Ctrl 1',
  'Focused editors · Ctrl 2': '專用編輯器 · Ctrl 2',
  'Editor mode': '編輯模式',
  'Unsaved changes — click to review': '未儲存的變更 — 點擊檢視',
  'Undo · Ctrl Z': '復原 · Ctrl Z',
  'Redo · Ctrl Shift Z': '重做 · Ctrl Shift Z',
  'Undo': '復原',
  'Redo': '重做',
  'Write changes to disk · Ctrl S': '將變更寫入磁碟 · Ctrl S',
  'Save': '儲存',
  'Save all': '全部儲存',
  'Saved': '已儲存',
  'Saving…': '儲存中…',
  'Import from another client · Ctrl I': '從其他客戶端匯入 · Ctrl I',
  'Open Dual Client View': '開啟雙客戶端檢視',
  'Open another client beside this one': '在此客戶端旁開啟另一個',
  'Theme, shortcuts and more': '佈景、快捷鍵與其他',
  'More': '更多',

  // ---- rail -------------------------------------------------------------
  'Files': '檔案',
  'Find names or values in open archives': '在開啟的封存中搜尋名稱或數值',
  'Pinned nodes': '釘選的節點',
  'Unsaved changes': '未儲存的變更',
  'Open client or archive': '開啟客戶端或封存',
  'Filter tree': '篩選樹狀結構',
  'Filter tree': '篩選樹狀結構',
  'Inspector': '屬性面板',
  'Name (A to Z)': '名稱（由 A 到 Z）',
  'Name (Z to A)': '名稱（由 Z 到 A）',
  'Type, then name': '先類型後名稱',
  'File order': '檔案順序',
  'NAME': '名稱',
  'VALUE': '數值',
  'Drop a .wz, .ms, .nx or .img to open the file picker': '拖放 .wz、.ms、.nx 或 .img 以開啟檔案選擇器',
  '? shortcuts': '? 快捷鍵',

  // ---- status bar -------------------------------------------------------
  'Keyboard shortcuts': '鍵盤快捷鍵',
  'General': '一般',
  'Editing': '編輯',
  'Jump to any node': '跳至任何節點',
  'Command palette': '指令面板',
  'Search all open files': '搜尋所有開啟的檔案',
  'Close every open archive': '關閉所有開啟的封存',
  'Undo / Redo': '復原 / 重做',
  'Explorer / Editors': '檔案總覽 / 編輯器',
  'Back / Forward': '上一頁 / 下一頁',
  'Move through the tree': '在樹狀結構中移動',
  'Expand / Collapse': '展開 / 收合',
  'Reorder the selected node among its siblings': '在同層節點中重新排列所選節點',
  // The left column of the shortcut list is the key. Ctrl P and Del stay Latin
  // because they are keys; this one is prose that happens to sit there.
  'Drag a row': '拖曳某一列',
  'Between rows reorders; onto a row moves it there': '拖到兩列之間可重新排序；拖到某列上則移至該處',
  'Type-ahead jump': '輸入字母跳轉',
  'Rename': '重新命名',
  'Delete selected': '刪除所選項目',
  'Expand everything below': '展開底下全部內容',
  'Collapse everything below': '收合底下全部內容',
  'Copy / Cut / Paste nodes': '複製 / 剪下 / 貼上節點',
  'Pin / unpin the current node': '釘選 / 取消釘選目前節點',
  'Find and replace': '搜尋並取代',
  'Change every selected value — *1.5, +50, -10%, clamp 1 999': '變更所有選取值 — *1.5、+50、-10%、限制 1 999',
  'In the results panel: select every editable result': '在結果面板中：選取所有可編輯的結果',
  'Multi-select in the tree': '在樹狀結構中多重選取',
  'This list': '本清單',

  // ---- close ------------------------------------------------------------
  'Close file': '關閉檔案',
  'Close all archives': '關閉所有封存',
  'Close all {n} archives': '關閉全部 {n} 個封存',
  'Close {n} archives': '關閉 {n} 個封存',
  'Close {name} with unsaved changes?': '關閉有未儲存變更的 {name}？',
  'Close {n} archives with unsaved changes?': '關閉 {n} 個有未儲存變更的封存？',
  'Discard and close': '捨棄並關閉',
  'Discard and close {n}': '捨棄並關閉 {n} 個',
  'closed.': '已關閉。',
  '{n} archives closed.': '已關閉 {n} 個封存。',
  'Could not close that file': '無法關閉該檔案',
  'Could not close {name}': '無法關閉 {name}',

  // ---- add / edit -------------------------------------------------------
  'Add child…': '新增子項…',
  'Add a canvas from a picture…': '從圖片新增 Canvas…',
  'Replace canvas image…': '取代 Canvas 圖片…',
  'Pixel edit…': '像素編輯…',
  'Export this PNG': '匯出此 PNG',
  'Duplicate': '複製',
  'Move up': '上移',
  'Move down': '下移',
  'Copy': '複製',
  'Cut': '剪下',
  'Paste': '貼上',
  'Paste into': '貼到',
  'Pin': '釘選',
  'Unpin': '取消釘選',
  'Delete': '刪除',
  'Set value on {n} nodes…': '設定 {n} 個節點的值…',
  'Open': '開啟',
  'Expand': '展開',
  'Collapse': '收合',
  'Expand everything': '展開全部',
  'Collapse everything': '全部收合',

  // ---- app shell: close / discard ---------------------------------------
  '{n} unsaved change has not been written to disk.': '有 {n} 項未儲存的變更尚未寫入磁碟。',
  '{n} unsaved changes have not been written to disk.': '有 {n} 項未儲存的變更尚未寫入磁碟。',
  'This archive holds unsaved work that has not been written to disk.': '此封存有尚未寫入磁碟的未儲存內容。',
  'They hold unsaved work that has not been written to disk.': '它們有尚未寫入磁碟的未儲存內容。',
  'Closing discards it. The file on disk is not changed.': '關閉會將其捨棄。磁碟上的檔案不會變動。',
  '{names} hold unsaved work. ': '{names} 有未儲存的內容。',
  'Closing discards them. Nothing on disk is changed.': '關閉會將其捨棄。磁碟上的內容不會變動。',
  ' and {n} more': '，另有 {n} 個',
  'Could not read the open files': '無法讀取已開啟的檔案',

  // ---- app shell: panes / rail ------------------------------------------
  'Pinned': '已釘選',
  'Results': '搜尋結果',

  // ---- app shell: topbar / dual view -----------------------------------
  'Import': '匯入',
  'Exit Dual View': '結束雙客戶端檢視',
  'Dual View': '雙客戶端檢視',
  'Dual Client View': '雙客戶端檢視',
  'Drag from source to target': '從來源拖曳到目標',
  'Return to Explorer': '回到檔案總覽',
  'Switch theme': '切換佈景',
  'About MapleBenchX': '關於 MapleBenchX',
  'Switch to Traditional Chinese': '切換為繁體中文',
  'Switch to English': '切換為英文',
  '已切換為繁體中文': '已切換為繁體中文',
  'Switched to English': '已切換為英文',

  // ---- app shell: welcome ----------------------------------------------
  'Open client': '開啟客戶端',
  'Open client or archive…': '開啟客戶端或封存…',
  'Choose a MapleStory client folder or WZ archive': '選擇 MapleStory 客戶端資料夾或 WZ 封存',
  'Import from client': '從客戶端匯入',
  'Import from another client…': '從其他客戶端匯入…',
  'Import selected content from a classic or split client with dependency review':
    '從傳統或分流客戶端匯入所選內容，並一併檢查相依項目',
  'Reopen last session ({n} file)': '重新開啟上次的工作階段（{n} 個檔案）',
  'Reopen last session ({n} files)': '重新開啟上次的工作階段（{n} 個檔案）',
  'Open a MapleStory client to browse and edit its WZ archives, or import content from another client with dependency review. Changes stay in the session until you save.':
    '開啟 MapleStory 客戶端以瀏覽並編輯其 WZ 封存，或從其他客戶端匯入內容並檢查相依項目。變更會保留在工作階段中，直到你儲存。',
  'Recent': '最近使用',
  'Nothing selected': '尚未選取',
  'Pick a node in the tree and its details appear here.': '在樹狀結構中選取節點，這裡就會顯示其詳細資料。',

  // ---- app shell: importer / dual gates ---------------------------------
  'Open the client you want to edit first': '請先開啟要編輯的客戶端',
  'MapleBenchX imports into an open writable client. Open that client before choosing content from a read-only source.':
    'MapleBenchX 匯入的目標必須是已開啟且可寫入的客戶端。請先開啟該客戶端，再從唯讀來源選擇內容。',
  'Dual Client View needs a writable target client before it can open a read-only source beside it.':
    '雙客戶端檢視需要先有可寫入的目標客戶端，才能在旁邊開啟唯讀來源。',
  'Not now': '暫不',
  'MapleBenchX': 'MapleBenchX',

  // ---- app shell: open / encryption ------------------------------------
  'Opening {name}…': '正在開啟 {name}…',
  '{name} opened — {version} encryption{extra}.': '已開啟 {name} — {version} 加密{extra}。',
  'Could not open that file': '無法開啟該檔案',
  'Custom IV, e.g. 4D23C72B (optional)': '自訂 IV，例如 4D23C72B（選填）',
  'Try a specific encryption': '嘗試指定的加密方式',
  'A file that will not parse has almost always been read with the wrong encryption for the region it came from. Try the others before assuming the file is damaged.':
    '無法解析的檔案，幾乎都是因為讀取了與其來源地區不符的加密方式。請先試試其他選項，再判定檔案已損毀。',
  'Try again': '重試',

  // ---- app shell: breadcrumb / status -----------------------------------
  'Back · Alt ←': '上一頁 · Alt ←',
  'Forward · Alt →': '下一頁 · Alt →',
  '{n} more level': '另有 {n} 層',
  '{n} more levels': '另有 {n} 層',
  '{n} child': '{n} 個子項',
  '{n} children': '{n} 個子項',
  '{name} ({n} file)': '{name}（{n} 個檔案）',
  '{name} ({n} files)': '{name}（{n} 個檔案）',

  // ---- app shell: clipboard / UOL --------------------------------------
  'Path copied.': '已複製路徑。',
  'Could not copy to the clipboard.': '無法複製到剪貼簿。',
  'Copied {value}.': '已複製 {value}。',
  'The link target "{link}" could not be resolved from here.': '無法從這裡解析連結目標「{link}」。',

  // ---- app shell: dirty chip / history ----------------------------------
  '{n} unsaved': '{n} 項未儲存',
  'unsaved': '未儲存',
  '{n} map-editor edits save from the Map Editor section; the Save button here writes the session archives.':
    '有 {n} 項地圖編輯器的變更，請在地圖編輯器分區中儲存；這裡的儲存按鈕寫入的是工作階段中的封存。',
  'Undo {what} · Ctrl Z': '復原 {what} · Ctrl Z',
  'Redo {what} · Ctrl Shift Z': '重做 {what} · Ctrl Shift Z',

  // ---- app shell: undo / redo ------------------------------------------
  'Nothing left to undo.': '沒有可復原的內容。',
  'Undid: {what}': '已復原：{what}',
  'Could not undo': '無法復原',
  'Nothing left to redo.': '沒有可重做的內容。',
  'Redid: {what}': '已重做：{what}',
  'Could not redo': '無法重做',

  // ---- add / edit: the Add-child dialog --------------------------------
  'Image  (.img container)': 'Image（.img 容器）',
  'Directory  (folder)': 'Directory（資料夾）',
  'Canvas  (picture)': 'Canvas（圖片）',
  'SubProperty  (folder of properties)': 'SubProperty（屬性資料夾）',
  'UOL  (link to another node)': 'UOL（連到其他節點的連結）',
  'Name': '名稱',
  'Value': '值',
  'Value (optional)': '值（選填）',
  'No picture chosen': '尚未選擇圖片',
  'Picture': '圖片',
  'Choose a picture…': '選擇圖片…',
  'Optional. Leave it empty to create a blank 1x1 canvas you can paint later.': '選填。留空會建立 1x1 的空白 Canvas，之後再自行繪製。',
  'Add a child node': '新增子節點',
  'Inside {name}': '位於 {name} 之中',
  'Type': '類型',
  'Add': '新增',
  'Cancel': '取消',
  'Give the node a name first.': '請先為節點命名。',
  'Image format changed': '圖片格式已變更',
  'Added {name}.': '已新增 {name}。',
  'Could not add that node': '無法新增該節點',
  'Canvas image replaced. Save the file to write it to disk.': '已取代 Canvas 圖片。請儲存檔案以寫入磁碟。',
  'Could not replace the canvas image': '無法取代 Canvas 圖片',

  // ---- duplicate / delete / paste / copy --------------------------------
  'Could not duplicate': '無法複製',
  'Duplicated {n} node.': '已複製 {n} 個節點。',
  'Duplicated {n} nodes.': '已複製 {n} 個節點。',
  '{n} nodes': '{n} 個節點',
  'Delete {label}?': '要刪除 {label} 嗎？',
  'This removes the node and everything under it. Nothing is written to disk until you save, and you can undo this straight away.':
    '這會移除該節點及其底下的所有內容。在你儲存之前不會寫入磁碟，且可以立即復原。',
  'Deleted {n} node.': '已刪除 {n} 個節點。',
  'Deleted {n} nodes.': '已刪除 {n} 個節點。',
  'Could not delete': '無法刪除',
  'Nothing copied yet.': '尚未複製任何內容。',
  'Moved {n} node.': '已移動 {n} 個節點。',
  'Moved {n} nodes.': '已移動 {n} 個節點。',
  'Copied {n} node.': '已複製 {n} 個節點。',
  'Copied {n} nodes.': '已複製 {n} 個節點。',
  'Cut {n} node.': '已剪下 {n} 個節點。',
  'Cut {n} nodes.': '已剪下 {n} 個節點。',
  'Could not paste': '無法貼上',

  // ---- expand / download ------------------------------------------------
  'Expanding…': '展開中…',
  'Opened {n} nodes and stopped there. Expanding a whole archive would take minutes, so this stops at a safe depth.':
    '已展開 {n} 個節點後停止。展開整個封存需要數分鐘，因此停在安全的深度。',
  'Partly expanded': '部分展開',
  'Expanded {n} nodes.': '已展開 {n} 個節點。',
  'Could not expand that node': '無法展開該節點',
  'Building the download — it will save on its own when it is ready.': '正在建立下載檔 — 準備好後會自動儲存。',

  // ---- save -------------------------------------------------------------
  'No changes to save.': '沒有可儲存的變更。',
  ' — {n} unsaved change': ' — {n} 項未儲存的變更',
  ' — {n} unsaved changes': ' — {n} 項未儲存的變更',
  ' — unsaved work that cannot be listed': ' — 無法列出的未儲存內容',
  '{name} cannot be saved yet:\n{problems}': '{name} 目前無法儲存：\n{problems}',
  'Save changes': '儲存變更',
  'The new file is written alongside the original and swapped in only once it is complete. Close MapleStory first — Windows will not let the file be replaced while the client has it open.':
    '新檔案會寫在原檔旁邊，完成後才會取代原檔。請先關閉 MapleStory — 客戶端開啟檔案時，Windows 不允許取代該檔案。',
  'Keep a timestamped backup of the original': '保留原檔的時間戳記備份',
  'Saving reloads the file, which clears the undo history.': '儲存會重新載入檔案，並清除復原記錄。',
  'Save now': '立即儲存',
  'A save is already running — wait for it to finish.': '儲存已在進行中 — 請等待它完成。',
  'Saving {name}…': '正在儲存 {name}…',
  '{name} saved — {size}, {images} rewritten{backup}': '已儲存 {name} — {size}，重寫了 {images}{backup}',
  '{n} image': '{n} 張圖片',
  '{n} images': '{n} 張圖片',
  '\nBackup: {file}': '\n備份：{file}',
  'Saved and verified': '已儲存並驗證',
  'Could not save {name}': '無法儲存 {name}',
  'Save {name} as': '另存 {name} 為',
  'Full path to write to. The original is left untouched, and nothing is overwritten without asking.':
    '要寫入的完整路徑。原檔不會變動，且任何覆寫都會先詢問。',
  'Save a copy': '儲存副本',
  'Saving {name} to {target}…': '正在將 {name} 儲存至 {target}…',
  'Saved to {path} — {size} MB in {seconds}s.': '已儲存至 {path} — {size} MB，耗時 {seconds} 秒。',
  'Copy written': '已寫入副本',
  'Replace {name}?': '要取代 {name} 嗎？',
  'There is already a file at {target}. Writing this copy over it keeps the existing file as a timestamped .bak alongside, but it is no longer what that name points at.':
    '{target} 已有檔案。將這個副本寫上去會把現有檔案保留為帶時間戳記的 .bak，但該名稱將不再指向原本的內容。',
  'Replace it': '取代',
  'Opening the folder needs the desktop window; a browser tab cannot do it.': '開啟資料夾需要桌面視窗，瀏覽器分頁做不到。',
  'Could not open the folder for {name}.': '無法開啟 {name} 所在的資料夾。',

  // ---- reorder / drag ---------------------------------------------------
  'Already first.': '已經是第一個了。',
  'Already last.': '已經是最後一個了。',
  'Moved up.': '已上移。',
  'Moved down.': '已下移。',
  'Could not reorder': '無法重新排序',
  'Moved {name} to position {at}.': '已將 {name} 移到第 {at} 個位置。',
  'Could not move that': '無法移動',
  'Moved {name} into {target}.': '已將 {name} 移入 {target}。',
  'Moved {n} nodes into {target}.': '已將 {n} 個節點移入 {target}。',
  'A node of that name was already there, so it was renamed to {name}.':
    '該名稱的節點已經存在，因此已重新命名為 {name}。',
  '{n} clashed with names already there and were renamed.': '有 {n} 個與既有名稱衝突，已重新命名。',
  'Move {what} to {target}?': '要將 {what} 移至 {target} 嗎？',
  'another archive': '另一個封存',
  '{what} would be removed from {from} and added to {to}, leaving both with unsaved changes. Nothing is written to disk until you save, and you can undo this straight away.':
    '{what} 會從 {from} 移除並加入 {to}，兩者都會留下未儲存的變更。在你儲存之前不會寫入磁碟，且可以立即復原。',
  'its archive': '其所在的封存',
  'the other one': '另一個封存',
  'Move it': '移動',
  '{name} is not where it was when the drag started, so nothing was moved. Something else changed the tree in the meantime — try the drag again.':
    '{name} 已不在拖曳開始時的位置，因此沒有移動任何內容。這段期間有其他操作改變了樹狀結構 — 請重新拖曳一次。',

  // ---- tree pane controls ----------------------------------------------
  'Change how these children are ordered': '變更這些子項的排序方式',
  'Filter these children by property type': '依屬性類型篩選這些子項',
  'Types': '類型',

  // ---- section context menus -------------------------------------------
  'Edit item…': '編輯項目…',
  'Duplicate with a new serial': '以新的序號複製',
  'Copy item ID {id}': '複製項目 ID {id}',
  'Copy serial {sn}': '複製序號 {sn}',
  'Show in Explorer': '在檔案總覽中顯示',
  'Delete item': '刪除項目',
  'Add item…': '新增項目…',
  'Bulk add…': '批次新增…',
  'Reload the item list': '重新載入項目清單',
  'Bulk edit the selected mobs…': '批次編輯所選的 mob…',
  'Clear the selection': '清除選取',
  'Reload the mob list': '重新載入 mob 清單',
  'Reload this node': '重新載入此節點',
  'Search inside this…': '在此範圍內搜尋…',
  'Find and replace here…': '在此處搜尋並取代…',
  'Export images (ZIP)': '匯出圖片（ZIP）',
  'Export…': '匯出…',
  'Export as .img': '匯出為 .img',
  'Export as JSON': '匯出為 JSON',
  'Export as server XML': '匯出為伺服器 XML',
  'Copy path': '複製路徑',
  'Copy file path': '複製檔案路徑',
  'Save this file': '儲存這個檔案',
  'Save as…': '另存新檔…',
  'Allow editing': '允許編輯',
  'Open for reference only': '以唯讀方式開啟',
  'Could not change {name}': '無法變更 {name}',
  '{name} is now reference-only. Edits below it will be refused.': '{name} 現在為唯讀，底下的編輯將被拒絕。',
  '{name} can be edited again.': '{name} 又可以編輯了。',
  'Import this folder into another client…': '將這個資料夾匯入到其他客戶端…',
  'Import these {n} folders into another client…': '將這 {n} 個資料夾匯入到其他客戶端…',

  // ---- shortcuts --------------------------------------------------------
  'Cash Shop / Mobs / Game Data Search': '商城 / Mob / 遊戲資料搜尋',
  'Filter the current children': '篩選目前的子項',

  // ---- about ------------------------------------------------------------
  'Version {version}': '版本 {version}',
  'A Windows WZ editor and dependency-aware client importer for MapleStory.':
    '適用於 Windows 的 WZ 編輯器，並支援具相依性判斷的 Maplestory 客戶端匯入。',
  'Created by': '作者',
  'License': '授權條款',
  'Source formats': '來源格式',
  'Classic .wz and split Data clients': '傳統 .wz 與分流 Data 客戶端',
  'Saves are written to a temporary file and reopened for verification before the destination is replaced.':
    '儲存時會先寫入暫存檔並重新開啟驗證，之後才會取代目標檔案。',
  'View source on GitHub': '在 GitHub 上檢視原始碼',

  // ---- palette ----------------------------------------------------------
  'One importer for classic WZ and split Data clients, with dependencies included':
    '同一套匯入工具，適用於傳統 WZ 與分流 Data 客戶端，並包含相依項目',
  'Skill.wz with Skill001-003.wz is one archive in four files':
    'Skill.wz 搭配 Skill001-003.wz，其實是一個封存、四個檔案',
  'Browse a split archive as one tree…': '將分流封存當成一個樹狀結構瀏覽…',
  'Check {name} for duplicate image names…': '檢查 {name} 是否有重複的圖片名稱…',
  '{n} archives merged — a name in two of them resolves by mount order':
    '已合併 {n} 個封存 — 兩個封存中同名時依掛載順序解析',
  'Switch to Explorer': '切換至檔案總覽',
  'Switch to Editors': '切換至編輯器',
  'The workspace you were last in': '你上次所在的區域',
  'Open the {section} section': '開啟{section}分區',
  'Search the client database…': '搜尋客戶端資料庫…',
  'Toggle theme': '切換佈景',
  'Search all open files…': '搜尋所有開啟的檔案…',
  'Search inside the current node…': '在目前節點內搜尋…',
  'Find and replace…': '搜尋並取代…',
  'Show unsaved changes': '顯示未儲存的變更',
  'Show pinned nodes': '顯示釘選的節點',
  'Pin the current node': '釘選目前節點',
  'Export images under this node (ZIP)': '匯出此節點底下的圖片（ZIP）',
  'Export this node as JSON': '將此節點匯出為 JSON',
  'Copy the current path': '複製目前路徑',
  'No matches. Type > for commands.': '沒有相符結果。輸入 > 以查看指令。',
  'Dropping opens the picker': '拖放會開啟檔案選擇器',
  '{name} closed.': '已關閉 {name}。',
  'A dropped file does not carry the folder it came from, so it cannot be opened directly. Pick "{name}" here instead — the folder you last browsed is already open.':
    '拖放進來的檔案不會帶有來源資料夾，因此無法直接開啟。請在這裡選擇「{name}」— 你上次瀏覽的資料夾已經開啟了。',
};
