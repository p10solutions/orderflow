import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createWriteStream } from 'node:fs';
import { access, mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { trackChild, stopChild, waitChild } from './lib/process-lifecycle.mjs';

// Run after the Release build. Every invocation owns fresh, isolated host processes.
const root = fileURLToPath(new URL('../', import.meta.url));
const output = resolve(root, '.tools/verification');
await mkdir(output, { recursive: true });
const dataRoot = await mkdtemp(resolve(output, 'data-'));
await mkdir(resolve(root, 'docs/evidence'), { recursive: true });
const dotnet = process.env.DOTNET_EXE ?? 'dotnet';
const children = [];
const logs = [];
const checks = [];
let base;
let failure;

async function freePort() {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  await new Promise((resolveClose, reject) => server.close(error => error ? reject(error) : resolveClose()));
  return port;
}

function startHost(name, port, fixPort) {
  const directory = process.argv.includes('--published')
    ? resolve(root, '.tools/publish', name) : resolve(root, 'src', name + '.Host');
  const dll = process.argv.includes('--published')
    ? resolve(directory, `${name}.dll`) : resolve(directory, `bin/Release/net10.0/${name}.dll`);
  const child = spawn(dotnet, [dll], {
    cwd: directory,
    windowsHide: true,
    env: { ...process.env, ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
      Fix__Host: '127.0.0.1', Fix__AcceptHost: '127.0.0.1', Fix__Port: String(fixPort),
      Storage__Directory: resolve(dataRoot, name),
      ConnectionStrings__Orders: process.env[name === 'OrderGenerator' ? 'ORDERFLOW_GENERATOR_DATABASE' : 'ORDERFLOW_ACCUMULATOR_DATABASE'] ?? (name === 'OrderGenerator'
        ? 'Host=127.0.0.1;Port=15433;Database=generator;Username=orderflow;Password=orderflow-local'
        : 'Host=127.0.0.1;Port=15432;Database=accumulator;Username=orderflow;Password=orderflow-local') },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const log = createWriteStream(resolve(output, `${name}.log`));
  logs.push(log);
  child.stdout.pipe(log, { end: false });
  child.stderr.pipe(log, { end: false });
  child.on('error', error => log.write(`${error.stack}\n`));
  children.push(trackChild(child));
  return child;
}

async function ready(predicate, description, timeout = 20000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const failed = children.find(tracked => tracked.result || tracked.error);
    if (failed) throw new Error(`Host failed: ${failed.error?.message ?? `code=${failed.result.code}, signal=${failed.result.signal}`}; inspect ${output}`);
    try { if (await predicate()) return; } catch { /* Hosts may still be starting. */ }
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${description}; inspect ${output}`);
}

async function request(path, body, raw = false) {
  const response = await fetch(`${base}${path}`, {
    ...(body === undefined ? {} : { method: 'POST', headers: { 'content-type': 'application/json' },
      body: raw ? body : JSON.stringify(body) }), signal: AbortSignal.timeout(10000),
  });
  const text = await response.text();
  let data;
  try { data = JSON.parse(text); } catch { data = text; }
  return { code: response.status, body: data };
}

async function runNode(script, env = {}, timeout = 180000) {
  const child = spawn(process.execPath, [resolve(root, script)], {
    cwd: root, windowsHide: true, env: { ...process.env, ...env }, stdio: 'inherit',
  });
  await waitChild(trackChild(child), timeout);
}

try {
  const httpPort = await freePort();
  let fixPort = await freePort();
  while (fixPort === httpPort) fixPort = await freePort();
  base = `http://127.0.0.1:${httpPort}`;
  startHost('OrderGenerator', httpPort, fixPort);
  await ready(async () => (await request('/status')).code === 200, 'HTTP readiness');
  const template = { id: crypto.randomUUID(), symbol: 'VALE3', side: 'Buy', quantity: 99999, price: '999.99' };
  const offline = await request('/orders', template);
  assert.equal(offline.code, 503);
  assert.equal(offline.body.code, 'FixUnavailable');
  assert.equal((await request(`/orders/${template.id}`)).code, 404);
  checks.push('offline 503 does not register or send');

  startHost('OrderAccumulator', 0, fixPort);
  await ready(async () => (await request('/status')).body.connected === true, 'real FIX logon');
  checks.push('HTTP readiness and real FIX logon');

  if (process.argv.includes('--load-only')) {
    await runNode('scripts/load.mjs', { BASE_URL: base, ORDERFLOW_HOST_PIDS: children.map(tracked => tracked.child.pid).join(',') }, 600000);
    checks.push('normal, burst and capacity load profiles');
  } else {
    for (const invalid of ['{', 'null', '{}', JSON.stringify({ ...template, quantity: '100' }),
      JSON.stringify({ ...template, quantity: 1.1 }), JSON.stringify({ ...template, price: 10 }),
      JSON.stringify({ ...template, symbol: null }), JSON.stringify({ ...template, id: null }),
      JSON.stringify({ ...template, price: null }), JSON.stringify({ ...template, price: '0.001' })]) {
      assert.equal((await request('/orders', invalid, true)).code, 400, `Invalid HTTP input ${invalid}`);
    }
    checks.push('HTTP binding: malformed JSON, null, missing fields, wrong types and invalid values return 400');
    const accepted = await request('/orders', template);
    assert.equal(accepted.code, 200);
    assert.equal(accepted.body.status, 'Accepted');
    assert.ok(accepted.body.orderId && accepted.body.execId);
    assert.deepEqual(await request('/orders', { ...template, price: '999.990' }), accepted);
    assert.equal((await request('/orders', { ...template, quantity: 1 })).code, 409);
    const rejected = await request('/orders', { ...template, id: crypto.randomUUID(), quantity: 3 });
    assert.equal(rejected.code, 200);
    assert.equal(rejected.body.status, 'Rejected');
    assert.match(rejected.body.reason, /Exposure limit/);
    assert.deepEqual(await request(`/orders/${template.id}`), accepted);
    assert.equal((await request(`/orders/${crypto.randomUUID()}`)).code, 404);
    assert.equal((await request('/')).code, 200);
    checks.push('acceptance, equivalent retry, conflict, risk rejection, query, unknown order and static frontend');
    if (!process.argv.includes('--skip-browser')) {
      const env = { BROWSER_BASE_URL: base,
        BROWSER_EVIDENCE_DIR: resolve(root, 'docs/evidence/implementation-browser'),
        BROWSER_CHANNEL: process.env.BROWSER_CHANNEL ?? (process.platform === 'win32' ? 'msedge' : 'chromium') };
      await runNode('web/tests/browser.mjs', env);
      await access(resolve(root, 'web/tests/recovery.mjs'));
      await runNode('web/tests/recovery.mjs', env);
      checks.push('real browser acceptance/rejection and network recovery scenarios');
    }
  }
} catch (error) {
  failure = error;
  throw error;
} finally {
  const cleanup = await Promise.allSettled(children.map(tracked => stopChild(tracked)));
  await Promise.all(logs.map(log => new Promise(resolveEnd => log.end(resolveEnd))));
  const errors = cleanup.filter(item => item.status === 'rejected').map(item => item.reason);
  const result = { timestamp: new Date().toISOString(), result: failure || errors.length ? 'FAIL' : 'PASS', base, checks,
    publishedArtifacts: process.argv.includes('--published'),
    browserSkipped: process.argv.includes('--skip-browser') || process.argv.includes('--load-only'),
    ...(failure ? { error: failure.message } : {}),
    cleanup: errors.length ? errors.map(error => error.message) : 'PASS' };
  const evidenceName = process.argv.includes('--load-only') ? 'implementation-load-runner.json' : 'implementation-smoke.json';
  await writeFile(resolve(root, `docs/evidence/${evidenceName}`), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
  if (errors.length) {
    if (failure) console.error('Cleanup errors:', errors);
    else throw new AggregateError(errors, 'Host cleanup failed');
  }
}

