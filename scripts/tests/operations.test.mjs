import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { trackChild, stopChild, waitChild } from '../lib/process-lifecycle.mjs';
import { verifyCompose } from '../lib/compose-verification.mjs';

test('cleanup observes a child already terminated by signal', { timeout: 5000 }, async () => {
  const tracked = trackChild(spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { windowsHide: true, stdio: 'ignore' }));
  tracked.child.kill('SIGTERM');
  await tracked.completion;
  assert.equal(tracked.result.signal, 'SIGTERM');
  await stopChild(tracked, 20, 20);
});

test('spawn failure is observed without waiting for an exit event', { timeout: 5000 }, async () => {
  const tracked = trackChild(spawn('orderflow-nonexistent-executable', [], { windowsHide: true, stdio: 'ignore' }));
  await assert.rejects(waitChild(tracked, 1000), /ENOENT/);
  await stopChild(tracked, 20, 20);
});

test('forced termination has a deadline even when kill fails', { timeout: 1000 }, async () => {
  const child = Object.assign(new EventEmitter(), { exitCode: null, signalCode: null, pid: 123, kill: () => false });
  await assert.rejects(stopChild(trackChild(child), 10, 10), /did not exit/);
});

test('kill error does not falsely mark a running child as terminated', { timeout: 1000 }, async () => {
  const child = Object.assign(new EventEmitter(), { exitCode: null, signalCode: null, pid: 123,
    kill() { this.emit('error', new Error('EPERM')); return false; } });
  const tracked = trackChild(child);
  await assert.rejects(stopChild(tracked, 10, 10), /did not exit/);
  assert.equal(tracked.result, undefined);
  assert.equal(tracked.error.message, 'EPERM');
});

for (const failedStep of ['config', 'info', 'inspect', 'up', 'checks', 'logs', 'down', 'checks+logs+down', 'none']) {
  test(`Compose evidence survives ${failedStep}`, async () => {
    let saved;
    const calls = [];
    const docker = async args => {
      const step = args[0] === 'compose' ? args[3] : args[0] === 'image' ? 'inspect' : args[0];
      calls.push(step);
      if (failedStep.split('+').includes(step)) throw new Error(`failure:${step}`);
      const stdout = step === 'config' && args.includes('--images') ? 'orderflow-generator\norderflow-accumulator'
        : step === 'inspect' ? '[{"Id":"sha256:test","RepoTags":["orderflow:test"],"Created":"test"}]'
        : step === 'info' ? '28.1.1' : 'logs';
      return { stdout, stderr: '' };
    };
    const result = await verifyCompose({ docker,
      check: async () => { if (failedStep.includes('checks')) throw new Error('failure:checks'); },
      writeLogs: async () => {}, writeEvidence: async evidence => { saved = structuredClone(evidence); } });
    assert.deepEqual(saved, result);
    assert.equal(result.result, failedStep === 'none' ? 'PASS' : 'FAIL');
    if (failedStep.includes('checks')) assert.equal(result.error, 'failure:checks');
    if (failedStep.includes('logs')) assert.equal(result.logsError, 'failure:logs');
    if (failedStep.includes('down')) assert.equal(result.cleanupError, 'failure:down');
    if (failedStep === 'config') assert.equal(result.daemon, 'NOT_CHECKED');
    if (calls.includes('up')) assert.ok(calls.includes('down'), 'Teardown runs after partially failed up');
    assert.match(result.imageBuild, /^NOT_EXECUTED/);
  });
}
