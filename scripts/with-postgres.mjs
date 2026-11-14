import { execFileSync, spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { trackChild, waitChild } from './lib/process-lifecycle.mjs';

const name = `orderflow-test-${crypto.randomUUID()}`;
const docker = args => execFileSync('docker', args, { encoding: 'utf8', windowsHide: true, timeout: 120000 });
let started = false;
try {
  docker(['run', '-d', '--rm', '--name', name, '-e', 'POSTGRES_PASSWORD=local-test-only',
    '-e', 'POSTGRES_DB=orderflow_test', '-p', '127.0.0.1::5432', 'postgres:17.7-bookworm']);
  started = true;
  let ready = false;
  for (let attempt = 0; attempt < 100; attempt++) {
    try { docker(['exec', name, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'orderflow_test']); ready = true; break; }
    catch { await delay(200); }
  }
  if (!ready) throw new Error('PostgreSQL readiness timeout');
  docker(['exec', name, 'psql', '-U', 'postgres', '-d', 'orderflow_test', '-c', 'CREATE SCHEMA generator; CREATE SCHEMA accumulator;']);
  const state = JSON.parse(docker(['inspect', name]))[0];
  const port = state.NetworkSettings.Ports['5432/tcp'][0].HostPort;
  const connection = `Host=127.0.0.1;Port=${port};Database=orderflow_test;Username=postgres;Password=local-test-only`;
  const [command, ...args] = process.argv.slice(2);
  if (!command) throw new Error('Usage: node scripts/with-postgres.mjs <command> [args]');
  const child = spawn(command, args, { windowsHide: true, stdio: 'inherit', env: { ...process.env,
    ORDERFLOW_TEST_DATABASE: connection,
    ORDERFLOW_GENERATOR_DATABASE: connection + ';Search Path=generator',
    ORDERFLOW_ACCUMULATOR_DATABASE: connection + ';Search Path=accumulator' } });
  await waitChild(trackChild(child), 900000);
} finally {
  if (started) docker(['rm', '-f', name]);
}
