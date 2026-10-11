#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 把 GitHub 事件轉成繁中 Discord 訊息：時間 / 版本 / 內容 三個重點。
#
# 為什麼不用 Qbox-project/pipeline-pling：
#   它沒有 message 模板參數，內容格式完全鎖死，無法改成中文或調整版面。
#   Discord embed 的 description 不支援 # 標題語法，所以「大字」只能用
#   embed 的 title 欄位（渲染為大標粗體），這是 embed 裡唯一能放大的地方。
# ─────────────────────────────────────────────────────────────
set -euo pipefail

: "${DISCORD_WEBHOOK_URL:?未設定 DISCORD_WEBHOOK_URL}"
: "${GITHUB_EVENT_PATH:?未設定 GITHUB_EVENT_PATH}"
: "${GITHUB_EVENT_NAME:?未設定 GITHUB_EVENT_NAME}"

REPO="${GITHUB_REPOSITORY}"
ACTOR="${GITHUB_ACTOR}"
SHA7="${GITHUB_SHA:0:7}"
BRANCH="${GITHUB_REF_NAME}"
RUN_URL="https://github.com/$REPO/actions/runs/${GITHUB_RUN_ID}"
TZ_NAME="Asia/Taipei"

# 機器人提交不發（對應 pipeline-pling 的 skip-bots）
if [[ "$ACTOR" == *"[bot]" ]]; then
  echo "略過機器人提交：$ACTOR"
  exit 0
fi

# ─────────────────────────────────────────────────────────────
# 組裝三個重點：時間 / 版本 / 內容
# ─────────────────────────────────────────────────────────────
if [[ "$GITHUB_EVENT_NAME" == "pull_request_target" ]]; then
  ACTION="$(jq -r '.action' "$GITHUB_EVENT_PATH")"
  MERGED="$(jq -r '.pull_request.merged // false' "$GITHUB_EVENT_PATH")"
  HEADLINE="$(jq -r '.pull_request.title' "$GITHUB_EVENT_PATH")"
  PR_NUM="$(jq -r '.pull_request.number' "$GITHUB_EVENT_PATH")"
  BASE="$(jq -r '.pull_request.base.ref' "$GITHUB_EVENT_PATH")"
  HEAD="$(jq -r '.pull_request.head.ref' "$GITHUB_EVENT_PATH")"

  # 版本欄顯示 PR 的目標分支（那是這個 PR 會影響的版本線）
  VERSION="$BASE ← $HEAD"
  LINK="https://github.com/$REPO/pull/$PR_NUM"

  # 色碼十進位對照：3447003=#3498DB藍 3066993=#2ECC71綠 10181046=#9B59B6紫
  #                  15158332=#E74C3C紅 9807270=#95A5A6灰
  case "$ACTION" in
    opened)           HEADLINE_TAG="新的 PR";      COLOR=3447003 ;;
    reopened)         HEADLINE_TAG="PR 重開";      COLOR=3447003 ;;
    synchronize)      HEADLINE_TAG="PR 有更新";    COLOR=3066993 ;;
    ready_for_review) HEADLINE_TAG="PR 可審核";    COLOR=10181046 ;;
    closed)
      if [[ "$MERGED" == "true" ]]; then
        HEADLINE_TAG="PR 已合併"; COLOR=3066993
      else
        HEADLINE_TAG="PR 已關閉"; COLOR=15158332
      fi ;;
    *)                HEADLINE_TAG="PR 動作";      COLOR=9807270 ;;
  esac
  FOOTER="$ACTOR 提交 · [查看 PR #$PR_NUM]($LINK)"

else  # push
  COUNT="$(jq '.commits | length' "$GITHUB_EVENT_PATH")"
  HEADLINE="$(jq -r '[.commits[] | .message | split("\n")[0]
                     | if length > 110 then .[0:107] + "…" else . end]
                     | .[0:3] | join("\n")' "$GITHUB_EVENT_PATH")"
  [[ "$COUNT" -gt 3 ]] && HEADLINE="$HEADLINE
…等 $COUNT 筆"

  HEADLINE_TAG="更新推送"
  VERSION="$SHA7 · $BRANCH"
  COLOR=1752220                  # #1ABC9C 青
  LINK="https://github.com/$REPO/commit/$SHA7"
  FOOTER="$ACTOR 提交 · [查看這次變更]($LINK)"
fi

# ─────────────────────────────────────────────────────────────
# 時間：台北時區，UTC+8
# ─────────────────────────────────────────────────────────────
NOW="$(TZ="$TZ_NAME" date '+%Y-%m-%d %H:%M')"

# ─────────────────────────────────────────────────────────────
# 送出。Discord 對預設 UA 會回 403（Cloudflare error 1010），必須自訂。
# ─────────────────────────────────────────────────────────────
PAYLOAD="$(jq -n \
  --arg tag    "$HEADLINE_TAG" \
  --arg now    "$NOW" \
  --arg ver    "$VERSION" \
  --arg body   "$HEADLINE" \
  --arg footer "$FOOTER" \
  --arg color  "$COLOR" \
  '{
    username: "NX編輯器BOT",
    embeds: [{
      title: $tag,
      color: ($color | tonumber),
      fields: [
        { name: "🕐 時間", value: $now,   inline: true  },
        { name: "🏷 版本", value: $ver,   inline: true  },
        { name: "📝 內容", value: $body,  inline: false }
      ],
      footer: { text: $footer }
    }]
  }')"

HTTP="$(curl -s -o /tmp/discord_resp.txt -w '%{http_code}' \
  -X POST "$DISCORD_WEBHOOK_URL" \
  -H 'Content-Type: application/json' \
  -H 'User-Agent: GitHub-Actions-Discord-Notify/1.0' \
  -d "$PAYLOAD")"

if [[ "$HTTP" == "204" || "$HTTP" == "200" ]]; then
  echo "Discord 已接收（HTTP $HTTP）"
  exit 0
fi

echo "::error::Discord 拒收（HTTP $HTTP）"
cat /tmp/discord_resp.txt
exit 1