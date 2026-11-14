import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { chromium } from 'playwright';

// Browser recovery checks use intercepted HTTP responses and never send financial orders.
// Serve a current frontend build at BROWSER_BASE_URL before running this script.
const baseURL = process.env.BROWSER_BASE_URL ?? 'http://127.0.0.1:5000';
const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL ?? 'msedge', headless: true });
const completed = [];

async function scenario(name, handler, run, prepare = async () => {}) {
  if (process.env.BROWSER_TRACE_SCENARIOS) console.log(`Recovery scenario: ${name}`);
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/status', route => route.fulfill({ json: { connected: true } }));
  await page.route('**/orders{,/**}', handler);
  try {
    await prepare(page);
    await page.goto(baseURL, { waitUntil: 'networkidle' });
    await page.getByText('Sessão FIX conectada', { exact: true }).waitFor();
    await run(page);
    assert.deepEqual(errors, [], 'No uncaught browser exceptions');
    completed.push(name);
  } catch (error) {
    throw new Error('Scenario failed: ' + name, { cause: error });
  } finally { await page.close(); }
}

const accepted = id => ({ id, status: 'Accepted', orderId: `order-${id}`, execId: `exec-${id}` });
const submit = page => page.getByRole('button', { name: 'Enviar ordem', exact: true }).click();
const retry = page => page.getByRole('button', { name: 'Tentar novamente com a mesma identidade', exact: true }).click();
const pending = page => page.getByRole('heading', { name: 'Aguardando confirmação', exact: true }).waitFor();
const final = page => page.getByRole('heading', { name: 'Ordem aceita', exact: true }).waitFor();

// Observe fetch settlement because the app need not consume a 404 response body.
const prepareDelayedResponses = page => page.addInitScript(() => {
  window.__settledQueries = 0;
  window.__settledPosts = 0;
  const fetchOriginal = window.fetch.bind(window);
  const timeoutOriginal = AbortSignal.timeout.bind(AbortSignal);
  AbortSignal.timeout = milliseconds => timeoutOriginal(milliseconds === 3000 ? 30000 : milliseconds);
  window.fetch = async (input, options) => {
    try { return await fetchOriginal(input, options); }
    finally {
      const path = new URL(String(input), location.href).pathname;
      if (path.startsWith('/orders/')) window.__settledQueries++;
      if (path === '/orders' && options?.method === 'POST') window.__settledPosts++;
    }
  };
});
try {
  for (const lostAfterDelivery of [false, true]) {
    const posts = [];
    const decisions = new Map();
    let applications = 0;
    await scenario(lostAfterDelivery ? 'lost-response-retry' : 'lost-post-retry', async route => {
      if (route.request().method() === 'GET') return route.fulfill({ status: 404 });
      const order = route.request().postDataJSON();
      posts.push(order);
      if (posts.length === 1 && !lostAfterDelivery) return route.abort('failed');
      if (!decisions.has(order.id)) {
        applications++;
        decisions.set(order.id, accepted(order.id));
      }
      if (posts.length === 1) return route.abort('failed');
      return route.fulfill({ json: decisions.get(order.id) });
    }, async page => {
      await submit(page);
      await pending(page);
      await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
      await page.getByText(/Identidade ainda não encontrada/).waitFor();
      await retry(page);
      await final(page);
      assert.equal(posts.length, 2);
      assert.deepEqual(posts[0], posts[1], 'Retry keeps UUID and immutable payload');
      assert.equal(applications, 1, 'Idempotent peer observes only one application');
    });
  }

  let releaseQuery;
  let queryStarted;
  const started = new Promise(resolve => { queryStarted = resolve; });
  const release = new Promise(resolve => { releaseQuery = resolve; });
  await scenario('stale-manual-query', async route => {
    if (route.request().method() === 'POST') {
      const order = route.request().postDataJSON();
      return route.fulfill({ status: 202, json: { id: order.id, status: 'PendingConfirmation' } });
    }
    queryStarted();
    await release;
    return route.fulfill({ json: accepted(new URL(route.request().url()).pathname.split('/').at(-1)) });
  }, async page => {
    await submit(page);
    await pending(page);
    await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
    await started;
    await page.getByRole('button', { name: 'Nova ordem →', exact: true }).click();
    await page.locator('#quantity').fill('7');
    const response = page.waitForResponse(response => response.request().method() === 'GET' && new URL(response.url()).pathname.startsWith('/orders/'));
    releaseQuery();
    await response;
    await page.getByRole('button', { name: 'Enviar ordem', exact: true }).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Ordem aceita', exact: true }).count(), 0);
    assert.equal(await page.locator('#quantity').inputValue(), '7');
    assert.equal(await page.locator('#quantity').isEnabled(), true);
  });

  let serviceCalls = 0;
  await scenario('distinct-503-causes', route => {
    serviceCalls++;
    return route.fulfill({ status: 503, json: serviceCalls === 1
      ? { code: 'FixUnavailable', error: 'FIX session unavailable' }
      : { code: 'RegistryCapacityReached', error: 'capacity' } });
  }, async page => {
    await submit(page);
    await page.getByText(/Sessão FIX indisponível/).waitFor();
    await page.getByRole('button', { name: 'Tentar novamente', exact: true }).click();
    await page.getByText(/A capacidade do registro foi atingida/).waitFor();
    assert.equal(await page.getByText(/Sessão FIX indisponível/).count(), 0);
  });

  const corrected = [];
  await scenario('400-correction-retains-id', route => {
    const order = route.request().postDataJSON();
    corrected.push(order);
    return corrected.length === 1
      ? route.fulfill({ status: 400, json: { code: 'ValidationFailed', error: 'Corrija o preço.', errors: { price: 'Preço recusado pelo servidor.' } } })
      : route.fulfill({ json: accepted(order.id) });
  }, async page => {
    await submit(page);
    await page.getByText('Preço recusado pelo servidor.', { exact: true }).waitFor();
    assert.equal(await page.locator('#price').isEnabled(), true);
    await page.locator('#price').fill('10.0100');
    await page.getByRole('button', { name: 'Tentar novamente', exact: true }).click();
    await final(page);
    assert.equal(corrected[0].id, corrected[1].id);
    assert.equal(corrected[1].price, '10.0100');
  });

  let validatedPosts = 0;
  await scenario('price-grammar', route => {
    validatedPosts++;
    return route.fulfill({ json: accepted(route.request().postDataJSON().id) });
  }, async page => {
    for (const price of ['00001.00', '1.' + '0'.repeat(31)]) {
      await page.locator('#price').fill(price);
      await submit(page);
      await page.getByText('Use um preço em centavos exatos (ex.: 10,25).', { exact: true }).waitFor();
    }
    assert.equal(validatedPosts, 0);
    await page.locator('#price').fill('0001.0100');
    await submit(page);
    await final(page);
    assert.equal(validatedPosts, 1);
  });

  for (const status of ['Accepted', 'Rejected']) {
    let queries = 0;
    await scenario(`terminal-${status}-after-poll-limit`, route => {
      const id = route.request().method() === 'POST'
        ? route.request().postDataJSON().id
        : new URL(route.request().url()).pathname.split('/').at(-1);
      if (route.request().method() === 'POST' || ++queries <= 20) {
        return route.fulfill({ status: 202, json: { id, status: 'PendingConfirmation' } });
      }
      return route.fulfill({ json: { ...accepted(id), status } });
    }, async page => {
      await submit(page);
      await page.getByText(/A consulta automática terminou/).waitFor();
      assert.equal(queries, 20, 'Automatic polling stops after twenty attempts');
      await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
      await page.getByRole('heading', { name: status === 'Accepted' ? 'Ordem aceita' : 'Ordem rejeitada', exact: true }).waitFor();
      assert.equal(await page.getByText(/A consulta automática terminou/).count(), 0);
      assert.equal(await page.getByText(/continua pendente de confirmação/).count(), 0);
    }, page => page.addInitScript(() => {
      // Accelerate only the polling interval; preserve fetch timeouts and all other timers.
      const original = window.setTimeout.bind(window);
      window.setTimeout = (callback, delay, ...args) => original(callback, delay === 1000 ? 5 : delay, ...args);
    }));
  }

  for (const failure of ['404', '500', 'network']) {
    let posts = 0;
    let release;
    let started;
    const startedPromise = new Promise(resolve => { started = resolve; });
    const releasePromise = new Promise(resolve => { release = resolve; });
    await scenario(`late-query-${failure}-after-accepted-retry`, async route => {
      if (route.request().method() === 'POST') {
        const id = route.request().postDataJSON().id;
        return ++posts === 1
          ? route.fulfill({ status: 202, json: { id, status: 'PendingConfirmation' } })
          : route.fulfill({ json: accepted(id) });
      }
      started();
      await releasePromise;
      return failure === 'network' ? route.abort('failed') : route.fulfill({ status: Number(failure) });
    }, async page => {
      await submit(page);
      await pending(page);
      await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
      await startedPromise;
      await retry(page);
      await final(page);
      release();
      await page.waitForFunction(() => window.__settledQueries > 0);
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await final(page);
      assert.equal(await page.getByText(/Identidade ainda não encontrada|Falha na consulta|entrega não está confirmada/).count(), 0);
      assert.equal(await page.getByRole('button', { name: 'Nova ordem →', exact: true }).isEnabled(), true);
    }, prepareDelayedResponses);
  }

  for (const failure of ['503', 'network']) {
    let posts = 0;
    let release;
    let started;
    const startedPromise = new Promise(resolve => { started = resolve; });
    const releasePromise = new Promise(resolve => { release = resolve; });
    await scenario(`late-retry-${failure}-after-confirmed-query`, async route => {
      if (route.request().method() === 'GET') {
        return route.fulfill({ json: accepted(new URL(route.request().url()).pathname.split('/').at(-1)) });
      }
      const id = route.request().postDataJSON().id;
      if (++posts === 1) return route.fulfill({ status: 202, json: { id, status: 'PendingConfirmation' } });
      started();
      await releasePromise;
      return failure === 'network'
        ? route.abort('failed')
        : route.fulfill({ status: 503, json: { code: 'FixUnavailable', error: 'offline' } });
    }, async page => {
      await submit(page);
      await pending(page);
      await retry(page);
      await startedPromise;
      await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
      await final(page);
      release();
      await page.waitForFunction(() => window.__settledPosts === 2);
      await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent === 'Nova ordem →' && !button.disabled));
      await final(page);
      assert.equal(await page.getByText(/Sessão FIX indisponível|A confirmação ainda não chegou/).count(), 0);
    }, prepareDelayedResponses);
  }

  let pendingQueries = 0;
  let pendingPosts = 0;
  const retried = [];
  await scenario('retry-pending-after-poll-limit', route => {
    const post = route.request().method() === 'POST';
    const id = post ? route.request().postDataJSON().id : new URL(route.request().url()).pathname.split('/').at(-1);
    if (post) { pendingPosts++; retried.push(route.request().postDataJSON()); }
    else pendingQueries++;
    return route.fulfill({ status: post || pendingQueries <= 20 ? 202 : 200,
      json: post || pendingQueries <= 20 ? { id, status: 'PendingConfirmation' } : accepted(id) });
  }, async page => {
    await submit(page);
    await page.getByText(/A consulta automática terminou/).waitFor();
    await retry(page);
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.includes('Tentar novamente com') && !button.disabled));
    assert.equal(pendingPosts, 2);
    assert.deepEqual(retried[0], retried[1]);
    assert.equal(pendingQueries, 20);
    assert.equal(await page.getByText(/A consulta automática terminou/).count(), 1);
    await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
    await final(page);
  }, page => page.addInitScript(() => {
    const original = window.setTimeout.bind(window);
    window.setTimeout = (callback, delay, ...args) => original(callback, delay === 1000 ? 5 : delay, ...args);
  }));

  const malformed = [
    ['null-result', 200, () => null],
    ['array-result', 200, () => []],
    ...['reason', 'diagnostic', 'orderId', 'execId'].flatMap(field => [
      [`object-${field}`, 200, id => ({ ...accepted(id), [field]: { unexpected: 'object' } })],
      [`array-${field}`, 200, id => ({ ...accepted(id), [field]: ['invalid'] })],
      [`number-${field}`, 200, id => ({ ...accepted(id), [field]: 123 })],
    ]),
    ['object-field-error', 400, () => ({ code: 'ValidationFailed', error: 'invalid', errors: { price: {} } })],
    ['array-errors', 400, () => ({ code: 'ValidationFailed', error: 'invalid', errors: [] })],
    ['object-error', 400, () => ({ code: 'ValidationFailed', error: {} })],
    ['null-error', 400, () => null],
  ];
  for (const [name, status, payload] of malformed) {
    const posts = [];
    await scenario(`invalid-response-${name}`, route => {
      if (route.request().method() === 'GET') return route.fulfill({ status: 404 });
      const order = route.request().postDataJSON();
      posts.push(order);
      return posts.length === 1 ? route.fulfill({ status, body: JSON.stringify(payload(order.id)), contentType: 'application/json' })
        : route.fulfill({ json: accepted(order.id) });
    }, async page => {
      await submit(page);
      await pending(page);
      assert.equal(await page.locator('#price').isEnabled(), false);
      await retry(page);
      await final(page);
      assert.deepEqual(posts[0], posts[1], 'Invalid responses retain identity and payload');
    });
  }

  for (const status of [413, 429]) {
    const posts = [];
    await scenario(`http-limit-${status}-retry`, route => {
      const order = route.request().postDataJSON();
      posts.push(order);
      return posts.length === 1
        ? route.fulfill({ status, json: { code: status === 413 ? 'RequestBodyTooLarge' : 'HttpCapacityReached', error: 'limit' } })
        : route.fulfill({ json: accepted(order.id) });
    }, async page => {
      await submit(page);
      await page.getByText(status === 413 ? /A requisição excedeu/ : /O serviço está ocupado/).waitFor();
      await page.getByRole('button', { name: 'Tentar novamente', exact: true }).click();
      await final(page);
      assert.deepEqual(posts[0], posts[1]);
    });
  }

  let malformedQueries = 0;
  await scenario('invalid-query-response-can-recover', route => {
    if (route.request().method() === 'POST') {
      const { id } = route.request().postDataJSON();
      return route.fulfill({ status: 202, json: { id, status: 'PendingConfirmation' } });
    }
    const id = new URL(route.request().url()).pathname.split('/').at(-1);
    return route.fulfill({ json: ++malformedQueries === 1 ? { ...accepted(id), diagnostic: {} } : accepted(id) });
  }, async page => {
    await submit(page);
    await pending(page);
    await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
    await page.getByText(/Falha na consulta/).waitFor();
    await page.getByRole('button', { name: 'Consultar status', exact: true }).click();
    await final(page);
  });

  const evidence = JSON.stringify({ result: 'PASS', checks: completed, financialOrdersSent: 0 }, null, 2);
  if (process.env.BROWSER_EVIDENCE_DIR) {
    await mkdir(process.env.BROWSER_EVIDENCE_DIR, { recursive: true });
    await writeFile(join(process.env.BROWSER_EVIDENCE_DIR, 'recovery.json'), evidence);
  }
  console.log(evidence);
} finally { await browser.close(); }



