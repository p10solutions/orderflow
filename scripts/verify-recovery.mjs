import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { trackChild, stopChild } from './lib/process-lifecycle.mjs';

// Run exclusively through with-postgres: never interrupt an operational database.
const connection = process.env.ORDERFLOW_TEST_DATABASE;
assert.match(connection ?? '', /Database=orderflow_test;/);
const port = /Port=([0-9]+)/i.exec(connection)[1];
const docker = args => execFileSync('docker', args, { encoding: 'utf8', windowsHide: true, timeout: 30000 });
const names = docker(['ps', '--filter', 'name=orderflow-test-', '--format', '{{.Names}}']).trim().split(/\r?\n/);
const container = names.find(name => {
  const state = JSON.parse(docker(['inspect', name]))[0];
  return state.NetworkSettings.Ports['5432/tcp']?.some(binding => binding.HostPort === port);
});
assert.ok(container?.startsWith('orderflow-test-'), 'Requires the disposable PostgreSQL wrapper');
const sql = (text, database = 'postgres') => docker([
  'exec', container, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', database, '-Atc', text,
]).trim();
const output = resolve('.tools/verification');
await mkdir(output, { recursive: true });
await mkdir('docs/evidence', { recursive: true });
const storage = await mkdtemp(resolve(output, 'recovery-'));
const children = [];
const logs = { OrderGenerator: '', OrderAccumulator: '' };
const application = `recovery-${crypto.randomUUID()}`;
const result = { result: 'FAIL', checks: [], cleanup: 'NOT_EXECUTED' };
let disabled = false;

async function freePort() {
  const server = createServer().listen(0, '127.0.0.1');
  await once(server, 'listening');
  const value = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return value;
}
const httpPort = await freePort();
let fixPort = await freePort();
while (fixPort === httpPort) fixPort = await freePort();
const base = `http://127.0.0.1:${httpPort}`;
function start(name) {
  const directory = resolve('src', `${name}.Host`);
  const child = spawn(process.env.DOTNET_EXE ?? 'dotnet', [resolve(directory, `bin/Release/net10.0/${name}.dll`)], {
    cwd: directory, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: {
      ...process.env, ASPNETCORE_URLS: base, Fix__Host: '127.0.0.1', Fix__AcceptHost: '127.0.0.1',
      Fix__Port: String(fixPort), Storage__Directory: resolve(storage, name),
      ConnectionStrings__Orders: process.env[name === 'OrderGenerator'
        ? 'ORDERFLOW_GENERATOR_DATABASE' : 'ORDERFLOW_ACCUMULATOR_DATABASE'] + `;Application Name=${application}-${name}`,
    },
  });
  child.stdout.on('data', data => logs[name] += data);
  child.stderr.on('data', data => logs[name] += data);
  const tracked = trackChild(child);
  children.push(tracked);
  return tracked;
}
async function until(predicate) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (children.some(child => child.result)) throw Error('Host exited unexpectedly; inspect recovery logs');
    try { if (await predicate()) return; } catch (error) {
      if (!(error instanceof TypeError)) throw error;
    }
    await delay(100);
  }
  throw Error('Recovery verification timed out');
}
const get = path => fetch(base + path, { signal: AbortSignal.timeout(1500) });
try {
  start('OrderAccumulator');
  const generator = start('OrderGenerator');
  await until(async () => (await (await get('/status')).json()).connected === true);
  sql('ALTER DATABASE orderflow_test ALLOW_CONNECTIONS false');
  disabled = true;
  sql("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='orderflow_test' " +
    "AND pid NOT IN (SELECT pid FROM pg_locks WHERE locktype='advisory')");
  await until(() => logs.OrderGenerator.includes('Recovery temporarily unavailable'));
  assert.equal((await get('/health/live')).status, 200);
  result.checks.push('transient work connection failure preserves HTTP and host lease');

  sql('ALTER DATABASE orderflow_test ALLOW_CONNECTIONS true');
  disabled = false;
  const id = crypto.randomUUID();
  const payload = { Symbol: 'PETR4', Side: 'Buy', Quantity: 1, Price: 0.01 };
  sql(`INSERT INTO generator.intents(id,payload,created_utc,updated_utc) VALUES ` +
    `('${id}','${JSON.stringify(payload)}','now','now')`, 'orderflow_test');
  let accepted;
  await until(async () => {
    const response = await get(`/orders/${id}`);
    if (!response.ok) return false;
    accepted = await response.json();
    return accepted.status === 'Accepted';
  });
  assert.ok(logs.OrderGenerator.includes('Recovery attempted 1 pending FIX orders'));
  assert.equal(sql("SELECT cents FROM accumulator.exposures WHERE symbol='PETR4'", 'orderflow_test'), '1');
  const retry = await fetch(base + '/orders', { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ id, symbol: 'PETR4', side: 'Buy', quantity: 1, price: '0.01' }),
    signal: AbortSignal.timeout(5000) });
  assert.deepEqual(await retry.json(), accepted);
  assert.equal(sql("SELECT cents FROM accumulator.exposures WHERE symbol='PETR4'", 'orderflow_test'), '1');
  result.checks.push('same worker resumes durable recovery through real FIX without duplicate exposure');

  sql(`SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='${application}-OrderGenerator' ` +
    "AND pid IN (SELECT pid FROM pg_locks WHERE locktype='advisory')");
  await Promise.race([generator.completion, delay(10000).then(() => { throw Error('Lost lease did not stop host'); })]);
  assert.ok(logs.OrderGenerator.includes('StopHost'));
  result.checks.push('loss of the original host lease stops the Generator');
  result.result = 'PASS';
} catch (error) {
  result.error = error.message;
  process.exitCode = 1;
} finally {
  const errors = [];
  if (disabled) {
    try { sql('ALTER DATABASE orderflow_test ALLOW_CONNECTIONS true'); } catch (error) { errors.push(error.message); }
  }
  const cleanup = await Promise.allSettled(children.map(child => stopChild(child)));
  errors.push(...cleanup.filter(item => item.status === 'rejected').map(item => item.reason.message));
  result.cleanup = errors.length ? errors : 'PASS';
  if (errors.length) { result.result = 'FAIL'; process.exitCode = 1; }
  for (const [name, log] of Object.entries(logs)) await writeFile(resolve(output, `${name}-recovery.log`), log);
  await writeFile('docs/evidence/implementation-database-recovery.json', JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
}
