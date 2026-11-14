import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { relative } from 'node:path';
import { chromium } from 'playwright';

// Financial integration test: run once against two freshly started hosts.
// VIIA4 is reserved for this scenario and must start at zero exposure.
// This script sends exactly two orders and never retries a submission.
const baseURL = process.env.BROWSER_BASE_URL ?? 'http://127.0.0.1:5000';
const channel = process.env.BROWSER_CHANNEL ?? 'msedge';
const symbol = process.env.BROWSER_SYMBOL ?? 'VIIA4';
const evidence = process.env.BROWSER_EVIDENCE_DIR ?? fileURLToPath(new URL('../../docs/evidence/', import.meta.url));
await mkdir(evidence, { recursive: true });
const browser = await chromium.launch({ channel: channel === 'chromium' ? undefined : channel, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1365, height: 1000 }, deviceScaleFactor: 1 });
  const errors = [];
  const requests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(`${message.text()} (${message.location().url})`); });
  page.on('request', request => { if (request.method() === 'POST' && new URL(request.url()).pathname === '/orders') requests.push(request.postDataJSON()); });
  await page.goto(baseURL, { waitUntil: 'networkidle' });
  await page.getByText('Sessão FIX conectada', { exact: true }).waitFor({ timeout: 15000 });
  if (process.argv.includes('--page-only')) {
    assert.deepEqual(errors, [], 'No browser console or runtime errors on page load');
    assert.equal(requests.length, 0);
    console.log(JSON.stringify({ result: 'PASS', check: 'page-load-only', browser: channel, consoleErrors: errors.length, submittedOrders: requests.length }));
  } else {
  await page.locator('#symbol').selectOption(symbol);
  await page.locator('#quantity').fill('0');
  await page.locator('#price').fill('10.001');
  await page.getByRole('button', { name: 'Enviar ordem' }).click();
  await page.getByText('Informe um inteiro entre 1 e 99.999.', { exact: true }).waitFor();
  await page.getByText('Use um preço em centavos exatos (ex.: 10,25).', { exact: true }).waitFor();
  assert.equal(requests.length, 0, 'Invalid input must never submit an order');

  await page.locator('#quantity').fill('99999');
  await page.locator('#price').fill('999.99');
  const acceptedResponse = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/orders');
  await page.getByRole('button', { name: 'Enviar ordem' }).click();
  const accepted = await (await acceptedResponse).json();
  assert.equal(accepted.status, 'Accepted', 'Requires zero initial exposure on the reserved symbol');
  await page.getByRole('heading', { name: 'Ordem aceita', exact: true }).waitFor();
  assert.ok(accepted.id && accepted.orderId && accepted.execId, 'Accepted report identifiers must be available');
  const exposureCard = page.locator('.exposure-asset').filter({ hasText: symbol });
  await page.waitForFunction(({ symbol }) => Array.from(document.querySelectorAll('.exposure-asset'))
    .some(card => card.textContent.includes(symbol) && card.textContent.includes('99.998.000,01')), { symbol });
  assert.match(await exposureCard.textContent(), /100% do limite utilizado/);
  await page.screenshot({ path: `${evidence}/browser-accepted.png`, fullPage: true });

  await page.getByRole('button', { name: 'Nova ordem →', exact: true }).click();
  await page.locator('#quantity').fill('3');
  await page.locator('#price').fill('999.99');
  const rejectedResponse = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/orders');
  await page.getByRole('button', { name: 'Enviar ordem' }).click();
  const rejected = await (await rejectedResponse).json();
  assert.equal(rejected.status, 'Rejected');
  assert.ok(rejected.reason, 'Rejected order must show a reason');
  await page.getByRole('heading', { name: 'Ordem rejeitada', exact: true }).waitFor();
  await page.getByText(rejected.reason, { exact: true }).waitFor();
  await page.screenshot({ path: `${evidence}/browser-rejected.png`, fullPage: true });
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForFunction(({ symbol }) => Array.from(document.querySelectorAll('.exposure-asset'))
    .some(card => card.textContent.includes(symbol) && card.textContent.includes('99.998.000,01')), { symbol });
  await page.route('**/exposures', route => route.fulfill({ contentType: 'application/json', body: '{"items":[]}' }));
  await page.getByRole('button', { name: 'Atualizar exposição', exact: true }).click();
  await page.getByText(/Exibindo a última consulta/).waitFor();
  assert.match(await exposureCard.textContent(), /99\.998\.000,01/);
  await page.unroute('**/exposures');
  await page.getByRole('button', { name: 'Atualizar exposição', exact: true }).click();
  await page.getByText(/Última consulta às/).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.screenshot({ path: `${evidence}/browser-exposure-mobile.png`, fullPage: true });
  assert.equal(requests.length, 2, 'Exactly two valid orders must have been submitted');
  assert.notEqual(requests[0].id, requests[1].id, 'A new intention requires a new identity');
  assert.equal(requests[0].symbol, symbol);
  assert.equal(requests[0].price, '999.99', 'Price stays textual in HTTP');
  assert.deepEqual(errors, [], 'No browser console or runtime errors');
  console.log(JSON.stringify({ result: 'PASS', browser: channel, symbol, validation: 'PASS', accepted, rejected, submittedOrders: requests.length, consoleErrors: errors.length, screenshots: ['browser-accepted.png', 'browser-rejected.png'].map(name => relative(process.cwd(), `${evidence}/${name}`)) }, null, 2));
  }
} finally { await browser.close(); }
