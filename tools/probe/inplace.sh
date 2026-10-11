#!/bin/sh
# End-to-end in-place save check against a scratch COPY of Morph.nx.
# The real archives in C:\MUWORK\GAME\MPSH are never written to.
set -e
BASE=http://127.0.0.1:5210/api
SCRATCH='C:\nxscratch'

echo "--- open ---"
curl -s -X POST "$BASE/files/open" -H 'Content-Type: application/json' \
  -d '{"path":"C:\\nxscratch\\Morph.nx","readOnly":false}' | tee /c/nxtmp/open.json
echo
echo "--- unlock (readonly:false) ---"
FID=$(sed -n 's/.*"id":"\([^"]*\)".*/\1/p' /c/nxtmp/open.json | head -1)
echo "fileId=$FID"
curl -s -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":false}' -w '\nHTTP %{http_code}\n'
echo "--- lock then in-place save must be refused ---"
curl -s -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":true}' -o /dev/null -w 'lock HTTP %{http_code}\n'
curl -s -X POST "$BASE/files/save" -H 'Content-Type: application/json' \
  -d "{\"fileId\":\"$FID\"}" -w '\nHTTP %{http_code}\n'
echo "--- unlock again, then in-place save ---"
curl -s -X POST "$BASE/files/$FID/readonly" -H 'Content-Type: application/json' \
  -d '{"readOnly":false}' -o /dev/null -w 'unlock HTTP %{http_code}\n'
curl -s -X POST "$BASE/files/save" -H 'Content-Type: application/json' \
  -d "{\"fileId\":\"$FID\"}" -w '\nHTTP %{http_code}\n'