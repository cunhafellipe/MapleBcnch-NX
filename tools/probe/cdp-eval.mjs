// Minimal CDP evaluate helper: node cdp-eval.mjs <port> <file-with-expression>
// Prints the JSON value returned by the expression. No project deps.
const PORT = process.argv[2];
const FILE = process.argv[3];
const { readFileSync } = await import('node:fs');
const expr = readFileSync(FILE, 'utf8');

const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find((t) => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (ev) => {
  const d = JSON.parse(ev.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
});
const cdp = (method, params = {}) => new Promise((res) => {
  const i = ++id; pending.set(i, res);
  ws.send(JSON.stringify({ id: i, method, params }));
});
const r = await cdp('Runtime.evaluate', {
  expression: expr, awaitPromise: true, returnByValue: true,
});
if (r.result?.exceptionDetails) {
  console.error('EXC:', r.result.exceptionDetails.text,
    r.result.exceptionDetails.exception?.description ?? '');
  process.exit(1);
}
console.log(JSON.stringify(r.result?.result?.value, null, 2));
ws.close();
process.exit(0);