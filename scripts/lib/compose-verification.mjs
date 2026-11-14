// Keep evidence truthful even when diagnostics or teardown fail.
export async function verifyCompose({ docker, check, writeLogs, writeEvidence }) {
  const result = { timestamp: new Date().toISOString(), result: 'FAIL', composeConfig: 'NOT_CHECKED',
    daemon: 'NOT_CHECKED', imageBuild: 'NOT_EXECUTED: runner requires prebuilt images', images: [], checks: [], cleanup: 'NOT_REQUIRED' };
  const compose = (...args) => docker(['compose', '-p', 'orderflow-verification', ...args]);
  let started = false;
  try {
    await compose('config', '--quiet');
    result.composeConfig = 'PASS';
    result.daemon = (await docker(['info', '--format', '{{.ServerVersion}}'])).stdout.trim();
    const names = (await compose('config', '--images')).stdout.trim().split(/\r?\n/).filter(Boolean);
    if (!names.length) throw new Error('Compose declares no images');
    const inspection = await docker(['image', 'inspect', ...names]);
    result.images = JSON.parse(inspection.stdout).map(image => ({ id: image.Id, tags: image.RepoTags, created: image.Created }));
    started = true; // A failed up can still create resources that require cleanup.
    await compose('up', '--no-build', '-d');
    result.composeUp = 'PASS';
    await check(compose, result);
    result.result = 'PASS';
  } catch (error) {
    result.error = error.message;
  } finally {
    if (started) {
      try {
        const logs = await compose('logs', '--no-color');
        await writeLogs(logs.stdout + logs.stderr);
      } catch (error) { result.logsError = error.message; result.result = 'FAIL'; }
      try { await compose('down', '--volumes'); result.cleanup = 'PASS: verification containers/network/isolated volumes removed; images retained'; }
      catch (error) { result.cleanupError = error.message; result.cleanup = 'FAIL'; result.result = 'FAIL'; }
    }
    await writeEvidence(result);
  }
  return result;
}
