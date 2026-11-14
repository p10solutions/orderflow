import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
const base = process.env.BASE_URL || 'http://127.0.0.1:5000';
// Requires a fresh VALE3 book; browser tests reserve VIIA4.
const order = { id: crypto.randomUUID(), symbol: 'VALE3', side: 'Buy', quantity: 99999, price: '999.99' };
const results = [];
async function post(payload) {
  const response = await fetch(`${base}/orders`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload) });
  const body = await response.json();
  results.push({ code: response.status, body });
  return { code: response.status, body };
}
assert.equal((await (await fetch(`${base}/status`)).json()).connected, true);
const accepted = await post(order);
assert.equal(accepted.code, 200);
assert.equal(accepted.body.status, 'Accepted');
const duplicate = await post({ ...order, price: '999.990' });
assert.deepEqual(duplicate, accepted);
assert.equal((await post({ ...order, quantity: 1 })).code, 409);
const rejected = await post({ ...order, id: crypto.randomUUID(), quantity: 3 });
assert.equal(rejected.code, 200);
assert.equal(rejected.body.status, 'Rejected');
assert.match(rejected.body.reason, /Exposure limit/);
assert.equal((await post({ ...order, id: crypto.randomUUID(), price: '0.001' })).code, 400);
assert.deepEqual(await (await fetch(`${base}/orders/${order.id}`)).json(), accepted.body);
assert.equal((await fetch(base)).status, 200);
await mkdir('docs/evidence', { recursive: true });
await writeFile('docs/evidence/http-smoke.json', JSON.stringify({ timestamp: new Date().toISOString(), base, results }, null, 2));
console.log('HTTP + real FIX smoke passed: accepted, equivalent retry, conflict, risk rejection, invalid input, query, static UI.');
