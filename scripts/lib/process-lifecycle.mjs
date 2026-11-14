// Observe immediately: an exit/error event cannot be recovered by subscribing during cleanup.
export function trackChild(child) {
  let result;
  let error;
  const completion = new Promise(resolve => {
    const finish = value => { if (!result) { result = value; resolve(value); } };
    child.on('error', failure => {
      error = failure;
      // A failed kill can also emit error; it does not mean a running process exited.
      if (!child.pid) finish({ error: failure });
    });
    child.once('exit', (code, signal) => finish({ code, signal }));
    if (child.exitCode !== null || child.signalCode !== null)
      finish({ code: child.exitCode, signal: child.signalCode });
  });
  return { child, completion, get result() { return result; }, get error() { return error; } };
}

async function within(completion, milliseconds) {
  let timer;
  try {
    return await Promise.race([completion.then(() => true), new Promise(resolve => { timer = setTimeout(() => resolve(false), milliseconds); })]);
  } finally { clearTimeout(timer); }
}

export async function stopChild(tracked, gracefulMs = 5000, forcedMs = 5000) {
  if (tracked.result) return;
  tracked.child.kill('SIGTERM');
  if (await within(tracked.completion, gracefulMs)) return;
  tracked.child.kill('SIGKILL');
  if (!await within(tracked.completion, forcedMs)) throw new Error(`Process ${tracked.child.pid} did not exit after forced termination`, { cause: tracked.error });
}

export async function waitChild(tracked, timeoutMs) {
  if (!await within(tracked.completion, timeoutMs)) {
    await stopChild(tracked);
    throw new Error(`Process ${tracked.child.pid} exceeded ${timeoutMs}ms`);
  }
  const { code, signal, error } = tracked.result;
  if (error) throw error;
  if (code !== 0) throw new Error(`Process ${tracked.child.pid} exited: code=${code}, signal=${signal}`);
}
