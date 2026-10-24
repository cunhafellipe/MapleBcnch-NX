#!/bin/sh
# End-to-end in-place save check against a scratch COPY of Morph.nx.
# The real archives under $NX_FIXTURE_DIR are never written to.
set -e
# The instance comes from GATE_BASE and from nowhere else (task-139).  There is
# deliberately no default port: this used to carry one (5210), and an instance
# older than this tree answers every one of these requests happily.
GATE_BASE="${GATE_BASE:-}"
if [ -z "$GATE_BASE" ]; then
  echo '>>> RED: GATE_BASE is not set, so there is no backend to measure.'
  echo '       Set it to the instance under test, for example:'
  echo "         GATE_BASE='http://127.0.0.1:<port>' sh $0"
  echo '       There is no default port on purpose: an older instance answers every one of these requests.'
  exit 1
fi
# The trailing-slash strip is load-bearing: a doubled slash 404s every request.
BASE=$(printf '%s' "$GATE_BASE" | sed 's:/*$::')/api
SCRATCH='C:\nxscratch'

echo "--- open ---"
curl -sS -X POST "$BASE/files/open" -H 'Content-Type: application/json' \
  -d '{"path":"C:\\nxscratch\\Morph.nx","readOnly":false}' | tee /c/nxtmp/open.json
echo
echo "--- unlock (readonly:false) ---"
FID=$(sed -n 's/.*"id":"\([^"]*\)".*/\1/p' /c/nxtmp/open.json | head -1)
echo "fileId=$FID"
curl -sS -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":false}' -w '\nHTTP %{http_code}\n'
echo "--- lock then in-place save must be refused ---"
curl -sS -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":true}' -o /dev/null -w 'lock HTTP %{http_code}\n'
curl -sS -X POST "$BASE/files/save" -H 'Content-Type: application/json' \
  -d "{\"fileId\":\"$FID\"}" -w '\nHTTP %{http_code}\n'
echo "--- unlock again, then in-place save ---"
curl -sS -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":false}' -o /dev/null -w 'unlock HTTP %{http_code}\n'
curl -sS -X POST "$BASE/files/save" -H 'Content-Type: application/json' \
  -d "{\"fileId\":\"$FID\"}" -w '\nHTTP %{http_code}\n'