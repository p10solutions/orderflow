import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';

// Requires a fresh registry/book. verify-local --load-only owns that lifecycle.
const base = process.env.BASE_URL ?? 'http://127.0.0.1:5000';
const pids = (process.env.ORDERFLOW_HOST_PIDS ?? '').split(',').filter(Boolean).map(Number);
const execute = promisify(execFile);
const profiles = [];
let nextOrder = 0;

async function hostResources() {
  if (!pids.length) return { unavailable: 'ORDERFLOW_HOST_PIDS was not supplied' };
  try {
    if (process.platform === 'win32') {
      const { stdout } = await execute('powershell.exe', ['-NoProfile', '-Command',
        `Get-Process -Id ${pids.join(',')} | Select-Object Id,CPU,WorkingSet64,PeakWorkingSet64 | ConvertTo-Json -Compress`], { windowsHide: true });
      const parsed = JSON.parse(stdout);
      return (Array.isArray(parsed) ? parsed : [parsed]).map(item => ({ pid: item.Id,
        cpuSeconds: item.CPU, residentBytes: item.WorkingSet64, peakResidentBytes: item.PeakWorkingSet64 }));
    }
    const { stdout } = await execute('getconf', ['CLK_TCK']);
    const ticks = Number(stdout.trim());
    return await Promise.all(pids.map(async pid => {
      const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
      const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
      const status = await readFile(`/proc/${pid}/status`, 'utf8');
      const kb = name => Number(status.match(new RegExp(`^${name}:\\s+(\\d+)`, 'm'))?.[1] ?? 0) * 1024;
      return { pid, cpuSeconds: (Number(fields[11]) + Number(fields[12])) / ticks,
        residentBytes: kb('VmRSS'), peakResidentBytes: kb('VmHWM') };
    }));
  } catch (error) { return { unavailable: error.message }; }
}

async function post(order) {
  const start = performance.now();
  const response = await fetch(`${base}/orders`, { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify(order), signal: AbortSignal.timeout(15000) });
  let body = await response.json();
  // 202 is part of the HTTP contract, especially with durable commits under saturation.
  // Measure through terminal confirmation; never hide the wait from the latency budget.
  if (response.status === 202) {
    const deadline = performance.now() + 30000;
    while (body.status === 'PendingConfirmation' && performance.now() < deadline) {
      await delay(100);
      const query = await fetch(`${base}/orders/${order.id}`, { signal: AbortSignal.timeout(15000) });
      assert.equal(query.status, 200, 'An admitted intention must remain queryable');
      body = await query.json();
    }
    assert.notEqual(body.status, 'PendingConfirmation', 'Durable confirmation must converge within 30s');
  }
  return { code: response.status === 202 ? 200 : response.status, initialCode: response.status,
    body, milliseconds: performance.now() - start };
}

async function profile(name, count, concurrency, makeOrder) {
  const samples = [];
  let pendingResponses = 0;
  let index = 0;
  const started = performance.now();
  await Promise.all(Array.from({ length: concurrency }, async () => {
    while (index < count) {
      const current = index++;
      const result = await post(makeOrder(current));
      if (result.initialCode === 202) pendingResponses++;
      assert.equal(result.code, 200, `${name}: expected completed decision`);
      assert.equal(result.body.status, 'Accepted', `${name}: tiny orders stay within risk limits`);
      samples.push(result.milliseconds);
    }
  }));
  const seconds = (performance.now() - started) / 1000;
  samples.sort((a, b) => a - b);
  const percentile = p => Number(samples[Math.min(samples.length - 1, Math.ceil(samples.length * p) - 1)].toFixed(3));
  const result = { name, count, concurrency, pendingResponses, seconds: Number(seconds.toFixed(3)),
    throughputPerSecond: Number((count / seconds).toFixed(2)),
    latencyMilliseconds: { p50: percentile(.5), p95: percentile(.95), p99: percentile(.99), max: percentile(1) } };
  profiles.push(result);
  console.log(JSON.stringify(result));
}

function uniqueOrder() {
  return { id: crypto.randomUUID(), symbol: 'PETR4', side: nextOrder++ % 2 ? 'Sell' : 'Buy', quantity: 1, price: '0.01' };
}

const before = await hostResources();
const workloadStarted = performance.now();
const duplicate = uniqueOrder();
await profile('normal', 100, 1, index => index === 0 ? duplicate : uniqueOrder());
await profile('burst', 300, 32, uniqueOrder);
await profile('capacity', 9600, 64, uniqueOrder);
const saturated = await post(uniqueOrder());
assert.equal(saturated.code, 503);
assert.equal(saturated.body.code, 'RegistryCapacityReached');
await profile('same-identity-at-capacity', 100, 64, () => duplicate);
const after = await hostResources();
const workloadSeconds = (performance.now() - workloadStarted) / 1000;
const cpu = Array.isArray(before) && Array.isArray(after) ? after.map(item => {
  const initial = before.find(previous => previous.pid === item.pid);
  const seconds = initial ? item.cpuSeconds - initial.cpuSeconds : null;
  return { pid: item.pid, consumedCpuSeconds: seconds,
    averageCpuCores: seconds === null ? null : Number((seconds / workloadSeconds).toFixed(4)) };
}) : null;
const resources = { before, after, cpu, workloadSeconds,
  note: 'CPU delta measures host process time during the workload; RSS snapshots and OS peak working set include startup. Logs remain enabled. Client CPU is excluded.' };
const budget = { uniqueIntentionsPerExecution: 10000, residentBytesPerHost: 512 * 1024 * 1024,
  normalP95Milliseconds: 250, burstP95Milliseconds: 1000,
  expectedUsage: 'Single local demonstration session, 1 sequential request normally and bursts of 32 concurrent requests; saturation is an admission test, not a production throughput target.' };
const result = { timestamp: new Date().toISOString(), base, result: 'PASS', profiles, resources, budget,
  admission: '10001st unique intention rejected; known identity remains usable without a second effect',
  budgetObserved: { normalLatency: profiles[0].latencyMilliseconds.p95 <= budget.normalP95Milliseconds,
    burstLatency: profiles[1].latencyMilliseconds.p95 <= budget.burstP95Milliseconds,
    memory: Array.isArray(after) ? after.every(item => item.peakResidentBytes <= budget.residentBytesPerHost) : null },
  limitations: 'One machine and one run; no production SLA or exactly-once claim. Durable recovery is tested separately in HostRecoveryTests. Concurrent duplicate requests here exercise completed entries; pending waiters are covered by registry concurrency tests.' };
await mkdir('docs/evidence', { recursive: true });
await writeFile('docs/evidence/performance.json', JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
