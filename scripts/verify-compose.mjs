import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';
import { verifyCompose } from './lib/compose-verification.mjs';
import { trackChild, waitChild } from './lib/process-lifecycle.mjs';

// Reserved verification project. Build first with: docker compose -p orderflow-verification build
const execute = promisify(execFile);
const server = createServer();
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const port = server.address().port;
await new Promise((resolveClose, reject) => server.close(error => error ? reject(error) : resolveClose()));
async function reservePort() {
  const socket = createServer();
  socket.listen(0, '127.0.0.1');
  await once(socket, 'listening');
  const result = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  return result;
}
const env = { ...process.env, ORDERFLOW_HTTP_PORT: String(port), ORDERFLOW_DATA_ID: `orderflow-verification-${crypto.randomUUID()}`,
  ACCUMULATOR_DB_PORT: String(await reservePort()), GENERATOR_DB_PORT: String(await reservePort()) };
const base = `http://127.0.0.1:${port}`;
const docker = args => execute('docker', args,
  { env, windowsHide: true, timeout: 120000, maxBuffer: 10 * 1024 * 1024 });
await mkdir('.tools/verification', { recursive: true });
await mkdir('docs/evidence', { recursive: true });
const result = await verifyCompose({ docker,
  writeLogs: logs => writeFile('.tools/verification/docker-compose.log', logs),
  writeEvidence: evidence => writeFile('docs/evidence/implementation-docker.json', JSON.stringify(evidence, null, 2)),
  check: async (compose, result) => {
  const deadline = Date.now() + 30000;
  let connected = false;
  while (Date.now() < deadline) {
    try {
      connected = (await (await fetch(`${base}/status`, { signal: AbortSignal.timeout(1000) })).json()).connected === true;
      if (connected) break;
    } catch { /* Wait for the published HTTP server and internal FIX connection. */ }
    await delay(100);
  }
  assert.equal(connected, true, 'Containers must establish FIX logon');
  result.checks.push('real FIX connection over internal container network');
  const post = async order => {
    const response = await fetch(`${base}/orders`, { method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify(order), signal: AbortSignal.timeout(10000) });
    return { code: response.status, body: await response.json() };
  };
  const exposures = async () => {
    const response = await fetch(`${base}/exposures`);
    assert.equal(response.status, 200);
    return (await response.json()).items;
  };
  const initial = await exposures();
  assert.deepEqual(initial.map(item => item.symbol), ['PETR4', 'VALE3', 'VIIA4']);
  assert.ok(initial.every(item => item.exposure === 0 && item.limit === 100000000));
  const order = { id: crypto.randomUUID(), symbol: 'VALE3', side: 'Buy', quantity: 99999, price: '999.99' };
  const accepted = await post(order);
  assert.equal(accepted.code, 200);
  assert.equal(accepted.body.status, 'Accepted');
  assert.deepEqual(await post({ ...order, price: '999.9900' }), accepted);
  assert.equal((await post({ ...order, quantity: 1 })).code, 409);
  assert.equal((await post({ ...order, id: crypto.randomUUID(), quantity: 3 })).body.status, 'Rejected');
  const snapshot = await exposures();
  assert.equal(snapshot.find(item => item.symbol === 'VALE3').exposure, 99998000.01);
  const sell = { id: crypto.randomUUID(), symbol: 'PETR4', side: 'Sell', quantity: 10, price: '2.00' };
  assert.equal((await post(sell)).body.status, 'Accepted');
  assert.equal((await exposures()).find(item => item.symbol === 'PETR4').exposure, -20);
  await post(sell);
  assert.equal((await exposures()).find(item => item.symbol === 'PETR4').exposure, -20);
  await compose('stop', 'accumulator');
  const unavailable = await fetch(`${base}/exposures`);
  assert.equal(unavailable.status, 503);
  assert.equal((await unavailable.json()).code, 'ExposureUnavailable');
  await compose('start', 'accumulator');
  const recoveryDeadline = Date.now() + 30000;
  while (Date.now() < recoveryDeadline) {
    if ((await (await fetch(`${base}/status`)).json()).connected) {
      const response = await fetch(`${base}/exposures`);
      if (response.ok) break;
    }
    await delay(200);
  }
  assert.equal((await exposures()).find(item => item.symbol === 'PETR4').exposure, -20);
  result.checks.push('exposure snapshot: zero assets, acceptance, rejection, retry, signed sale and restart');
  assert.equal((await fetch(base)).status, 200);
  const { stdout: state } = await compose('ps', '--format', 'json');
  const containers = state.trim().startsWith('[') ? JSON.parse(state) : state.trim().split(/\r?\n/).map(line => JSON.parse(line));
  const accumulator = containers.find(container => container.Service === 'accumulator');
  assert.ok(accumulator, 'Accumulator container must be running');
  assert.equal((accumulator.Publishers ?? []).some(port => port.PublishedPort > 0), false, 'FIX must not be published to the host');
  result.checks.push('acceptance, rejection, equivalent retry, conflict and static frontend', 'FIX port is not published');
  const browser = spawn(process.execPath, ['web/tests/browser.mjs'], { windowsHide: true, stdio: 'inherit', env: {
    ...env, BROWSER_BASE_URL: base, BROWSER_EVIDENCE_DIR: resolve('docs/evidence/implementation-docker-browser'),
    BROWSER_CHANNEL: process.env.BROWSER_CHANNEL ?? (process.platform === 'win32' ? 'msedge' : 'chromium'),
  } });
  await waitChild(trackChild(browser), 60000);
  result.checks.push('browser validation, acceptance and rejection against containers');
  }
});
console.log(JSON.stringify(result, null, 2));
if (result.result !== 'PASS') process.exitCode = 1;
