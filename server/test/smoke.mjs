// Endpoint smoke test against a running server (`npm run dev` in another terminal, then `npm test`).
//   BASE_URL       default http://127.0.0.1:8788
//   PUBLISH_TOKEN  default: read from .dev.vars
// Uses versions 0.0.1/0.0.2 and channel "smoke-test"; leaves 0.0.2 published on that channel.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const BASE = (process.env.BASE_URL ?? 'http://127.0.0.1:8788').replace(/\/+$/, '');
const TOKEN =
  process.env.PUBLISH_TOKEN ?? /^PUBLISH_TOKEN=(.*)$/m.exec(readFileSync(new URL('../.dev.vars', import.meta.url), 'utf8'))?.[1]?.trim();
const CH = 'smoke-test';
const JSON_TYPE = 'application/json; charset=utf-8';

let failures = 0;
async function test(name, fn) {
  try {
    await fn();
    console.log(`PASS  ${name}`);
  } catch (e) {
    failures++;
    console.log(`FAIL  ${name}: ${e.message}`);
  }
}
function assert(cond, message) {
  if (!cond) throw new Error(message);
}

function call(method, path, { token = TOKEN, body, headers = {}, redirect = 'follow' } = {}) {
  const auth = token ? { Authorization: `Bearer ${token}` } : {};
  return fetch(BASE + path, { method, body, headers: { ...auth, ...headers }, duplex: 'half', redirect });
}

async function expectStatus(res, status, errorIncludes) {
  const text = await res.text();
  assert(res.status === status, `expected ${status}, got ${res.status} ${text}`);
  if (status !== 200 || res.headers.get('content-type')?.startsWith('application/json')) {
    assert(res.headers.get('content-type') === JSON_TYPE, `content-type was ${res.headers.get('content-type')}`);
  }
  const data = text ? JSON.parse(text) : null;
  if (errorIncludes) assert(data?.error?.includes(errorIncludes), `error "${data?.error}" lacks "${errorIncludes}"`);
  return data;
}

const sha = (buf) => createHash('sha256').update(buf).digest('hex');
const fileA = Buffer.from('portable build bytes '.repeat(100));
const fileB = Buffer.from('installer bytes '.repeat(50));

function envelope({ version = '0.0.1', channel = CH, product = 'LiteShot', files } = {}) {
  files ??= [
    { kind: 'portable', name: 'LiteShot.exe', size: fileA.length, sha256: sha(fileA) },
    { kind: 'msi', name: `LiteShot-Setup-${version}.msi`, size: fileB.length, sha256: sha(fileB) },
  ];
  const payload = JSON.stringify({ product, version, channel, published: '2026-10-28T12:00:00Z', notes: 'smoke', files });
  return JSON.stringify({ payload, signature: Buffer.alloc(64, 7).toString('base64') });
}

async function upload(version) {
  await expectStatus(await call('PUT', `/v1/releases/${version}/LiteShot.exe`, { body: fileA }), 200);
  await expectStatus(await call('PUT', `/v1/releases/${version}/LiteShot-Setup-${version}.msi`, { body: fileB }), 200);
}

// --- public endpoints ---
await test('GET /v1', async () => {
  const d = await expectStatus(await call('GET', '/v1'), 200);
  assert(d.name === 'LiteShot update server' && d.ok === true, JSON.stringify(d));
});
await test('GET / is the website', async () => {
  const res = await call('GET', '/');
  assert(res.status === 200 && (res.headers.get('content-type') ?? '').includes('text/html'), `${res.status} ${res.headers.get('content-type')}`);
});
await test('unknown page -> 404 page', async () => {
  const res = await call('GET', '/no-such-page');
  assert(res.status === 404 && (res.headers.get('content-type') ?? '').includes('text/html'), `${res.status}`);
});
await test('/download is the download page', async () => {
  const res = await call('GET', '/download');
  assert(res.status === 200 && (res.headers.get('content-type') ?? '').includes('text/html'), `${res.status}`);
});
await test('/download/installer redirects to the newest installer', async () => {
  const res = await call('GET', '/download/installer', { redirect: 'manual' });
  assert(res.status === 302 && /\/v1\/download\/\d+\.\d+\.\d+\/LiteShot-Setup-/.test(res.headers.get('location') ?? ''), `${res.status} ${res.headers.get('location')}`);
});
await test('/download/portable redirects to the newest exe', async () => {
  const res = await call('GET', '/download/portable', { redirect: 'manual' });
  assert(res.status === 302 && /\/LiteShot\.exe$/.test(res.headers.get('location') ?? ''), `${res.status} ${res.headers.get('location')}`);
});
await test('POST /v1 -> 405 with Allow', async () => {
  const res = await call('POST', '/v1');
  assert(res.headers.get('allow')?.includes('GET'), `Allow: ${res.headers.get('allow')}`);
  await expectStatus(res, 405);
});
await test('unknown API path -> 404', async () => expectStatus(await call('GET', '/v1/whatever'), 404));
await test('trailing slash is not a route -> 404', async () => expectStatus(await call('GET', '/v1/latest/'), 404));
await test('latest: bad channel -> 400', async () => expectStatus(await call('GET', '/v1/latest?channel=Bad_Channel'), 400));
await test('latest: empty channel -> 400', async () => expectStatus(await call('GET', '/v1/latest?channel='), 400));
await test('latest: unknown channel -> 404 no release', async () =>
  expectStatus(await call('GET', '/v1/latest?channel=nothing-here'), 404, 'no release'));
await test('latest: POST -> 405', async () => expectStatus(await call('POST', '/v1/latest'), 405));
await test('download: bad version -> 400', async () => expectStatus(await call('GET', '/v1/download/1.0/LiteShot.exe'), 400));
await test('download: dot file -> 400', async () => expectStatus(await call('GET', '/v1/download/1.0.0/.hidden'), 400));
await test('download: encoded chars -> 400', async () => expectStatus(await call('GET', '/v1/download/1.0.0/a%20b.exe'), 400));
await test('download: missing -> 404', async () => expectStatus(await call('GET', '/v1/download/0.0.999/LiteShot.exe'), 404));
await test('download: HEAD missing -> 404', async () => assert((await call('HEAD', '/v1/download/0.0.999/x.exe')).status === 404, 'not 404'));
await test('download: DELETE -> 405', async () => expectStatus(await call('DELETE', '/v1/download/0.0.1/LiteShot.exe'), 405));

// --- auth ---
await test('upload: no token -> 401', async () =>
  expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { token: null, body: fileA }), 401));
await test('upload: wrong token -> 401', async () =>
  expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { token: TOKEN + 'x', body: fileA }), 401));
await test('upload: Basic scheme -> 401', async () =>
  expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { token: null, body: fileA, headers: { Authorization: `Basic ${TOKEN}` } }), 401));
await test('list: no token -> 401', async () => expectStatus(await call('GET', '/v1/releases', { token: null }), 401));
await test('channel: no token -> 401', async () => expectStatus(await call('PUT', `/v1/channels/${CH}`, { token: null, body: envelope() }), 401));
await test('delete: wrong token -> 401', async () => expectStatus(await call('DELETE', '/v1/releases/0.0.1', { token: 'nope' }), 401));

// --- uploads ---
await test('upload: bad version -> 400', async () => expectStatus(await call('PUT', '/v1/releases/v1.0.0/LiteShot.exe', { body: fileA }), 400));
await test('upload: bad file name -> 400', async () => expectStatus(await call('PUT', '/v1/releases/0.0.1/.env', { body: fileA }), 400));
await test('upload: 101-char name -> 400', async () => expectStatus(await call('PUT', `/v1/releases/0.0.1/${'a'.repeat(101)}`, { body: fileA }), 400));
await test('upload: no Content-Length -> 411', async () => {
  const stream = new ReadableStream({ start(c) { c.enqueue(new Uint8Array([1, 2, 3])); c.close(); } });
  await expectStatus(await call('PUT', '/v1/releases/0.0.1/chunked.bin', { body: stream }), 411);
});
await test('upload: wrong X-Content-SHA256 -> 400', async () =>
  expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { body: fileA, headers: { 'X-Content-SHA256': sha(fileB) } }), 400));
await test('upload: malformed X-Content-SHA256 -> 400', async () =>
  expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { body: fileA, headers: { 'X-Content-SHA256': 'abc' } }), 400));
await test('upload: ok, with X-Content-SHA256', async () => {
  const d = await expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot.exe', { body: fileA, headers: { 'X-Content-SHA256': sha(fileA) } }), 200);
  assert(d.key === 'releases/0.0.1/LiteShot.exe' && d.size === fileA.length, JSON.stringify(d));
});
await test('upload: ok, without checksum', async () => {
  const d = await expectStatus(await call('PUT', '/v1/releases/0.0.1/LiteShot-Setup-0.0.1.msi', { body: fileB }), 200);
  assert(d.size === fileB.length, JSON.stringify(d));
});
await test('upload: GET -> 405', async () => expectStatus(await call('GET', '/v1/releases/0.0.1/LiteShot.exe'), 405));

// --- downloads ---
await test('download: GET streams the file with headers', async () => {
  const res = await call('GET', '/v1/download/0.0.1/LiteShot.exe', { token: null });
  const body = Buffer.from(await res.arrayBuffer());
  assert(res.status === 200, `status ${res.status}`);
  assert(body.equals(fileA), 'body differs');
  const h = (n) => res.headers.get(n);
  assert(h('content-length') === String(fileA.length), `content-length ${h('content-length')}`);
  assert(h('content-type') === 'application/octet-stream', `content-type ${h('content-type')}`);
  assert(h('content-disposition') === 'attachment; filename="LiteShot.exe"', `disposition ${h('content-disposition')}`);
  assert(h('etag'), 'no etag');
  assert(h('cache-control') === 'public, max-age=31536000, immutable', `cache-control ${h('cache-control')}`);
});
await test('download: HEAD has headers, no body', async () => {
  const res = await call('HEAD', '/v1/download/0.0.1/LiteShot.exe', { token: null });
  assert(res.status === 200, `status ${res.status}`);
  assert(res.headers.get('content-length') === String(fileA.length), `content-length ${res.headers.get('content-length')}`);
  assert(res.headers.get('etag') && res.headers.get('content-disposition'), 'missing headers');
  assert((await res.arrayBuffer()).byteLength === 0, 'HEAD had a body');
});

// --- download counts ---
const BROWSER = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36';
const getStats = async () => expectStatus(await call('GET', '/v1/stats'), 200);
await test('stats: no token -> 401', async () => expectStatus(await call('GET', '/v1/stats', { token: null }), 401));
await test('stats: POST -> 405', async () => expectStatus(await call('POST', '/v1/stats'), 405));
await test('downloads are counted by kind and source; HEAD and misses are not', async () => {
  const before = await getStats();
  const fetchAs = async (method, path, agent) => (await call(method, path, { token: null, headers: { 'User-Agent': agent } })).arrayBuffer();
  await fetchAs('GET', '/v1/download/0.0.1/LiteShot.exe', BROWSER);
  await fetchAs('GET', '/v1/download/0.0.1/LiteShot-Setup-0.0.1.msi', BROWSER);
  await fetchAs('GET', '/v1/download/0.0.1/LiteShot-Setup-0.0.1.msi', BROWSER);
  await fetchAs('GET', '/v1/download/0.0.1/LiteShot.exe', 'LiteShot/1.1.2');
  await fetchAs('GET', '/v1/download/0.0.1/LiteShot.exe', 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)');
  await fetchAs('HEAD', '/v1/download/0.0.1/LiteShot.exe', BROWSER);
  await fetchAs('GET', '/v1/download/0.0.999/LiteShot.exe', BROWSER);
  // Counts are written after each response; give them a moment.
  let after, diff;
  for (let i = 0; i < 20; i++) {
    after = await getStats();
    diff = {
      installer: after.downloads.installer - before.downloads.installer,
      portable: after.downloads.portable - before.downloads.portable,
      total: after.downloads.total - before.downloads.total,
      updates: after.updates.portable - before.updates.portable,
      bots: after.bots - before.bots,
    };
    if (diff.installer >= 2 && diff.updates >= 1 && diff.bots >= 1) break;
    await new Promise((r) => setTimeout(r, 150));
  }
  const ok = diff.installer === 2 && diff.portable === 1 && diff.total === 3 && diff.updates === 1 && diff.bots === 1;
  assert(ok, `changes ${JSON.stringify(diff)}`);
  const today = new Date().toISOString().slice(0, 10);
  const day = after.last30Days.find((d) => d.day === today);
  assert(day && day.installer + day.portable >= 3, `today ${JSON.stringify(day)}`);
  const v = after.versions.find((x) => x.version === '0.0.1');
  assert(v && v.downloads.total >= 3 && v.updates >= 1, `0.0.1 ${JSON.stringify(v)}`);
  assert(!after.versions.some((x) => x.version === '0.0.999'), 'a missing file was counted');
});

// --- copies in use ---
await test('copies are counted once a day by install id; bad ids, browsers and repeats are not', async () => {
  const before = (await getStats()).installs;
  const id = () => createHash('sha256').update(String(Math.random())).digest('hex').slice(0, 32);
  const [a, b] = [id(), id()];
  const check = async (headers) => (await call('GET', `/v1/latest?channel=${CH}`, { token: null, headers })).arrayBuffer();
  await check({ 'User-Agent': 'LiteShot/0.0.1', 'X-LiteShot-Install': a });
  await check({ 'User-Agent': 'LiteShot/0.0.1', 'X-LiteShot-Install': a });
  await check({ 'User-Agent': 'LiteShot/0.0.1', 'X-LiteShot-Install': b });
  await check({ 'User-Agent': 'LiteShot/0.0.1', 'X-LiteShot-Install': 'not-an-id' });
  await check({ 'User-Agent': BROWSER, 'X-LiteShot-Install': id() });
  await check({ 'User-Agent': 'LiteShot/0.0.1' });
  let after;
  for (let i = 0; i < 20; i++) {
    after = (await getStats()).installs;
    if (after.today - before.today >= 2) break;
    await new Promise((r) => setTimeout(r, 150));
  }
  const diff = { today: after.today - before.today, week: after.week - before.week, total: after.total - before.total };
  assert(diff.today === 2 && diff.week === 2 && diff.total === 2, `changes ${JSON.stringify(diff)}`);
  const today = new Date().toISOString().slice(0, 10);
  const day = after.last30Days.find((d) => d.day === today);
  assert(day && day.active >= 2 && day.new >= 2, `today ${JSON.stringify(day)}`);
  assert(after.versions.find((v) => v.version === '0.0.1')?.n >= 2, `versions ${JSON.stringify(after.versions)}`);
});

// --- channel manifests ---
const bad =(name, body, includes) =>
  test(`channel: ${name} -> 400`, async () => expectStatus(await call('PUT', `/v1/channels/${CH}`, { body }), 400, includes));
await bad('not JSON', 'not json');
await bad('missing signature', JSON.stringify({ payload: '{}' }));
await bad('payload not JSON', JSON.stringify({ payload: '{', signature: 'AA==' }));
await bad('wrong product', envelope({ product: 'Other' }), 'product');
await bad('channel mismatch', envelope({ channel: 'beta' }), 'channel');
await bad('bad version', envelope({ version: '1.0' }), 'version');
await bad('empty files', envelope({ files: [] }), 'files');
await bad('file not uploaded', envelope({ files: [{ kind: 'portable', name: 'Nope.exe', size: 1, sha256: sha(fileA) }] }), 'not been uploaded');
await bad('size mismatch', envelope({ files: [{ kind: 'portable', name: 'LiteShot.exe', size: fileA.length + 1, sha256: sha(fileA) }] }), 'bytes');
await bad('sha256 mismatch vs stored checksum', envelope({ files: [{ kind: 'portable', name: 'LiteShot.exe', size: fileA.length, sha256: sha(fileB) }] }), 'sha256');
await test('channel: bad channel name -> 400', async () => expectStatus(await call('PUT', '/v1/channels/UPPER', { body: envelope() }), 400));
await test('channel: GET -> 405', async () => expectStatus(await call('GET', `/v1/channels/${CH}`), 405));

const env1 = envelope();
await test('channel: valid envelope stored', async () => {
  const d = await expectStatus(await call('PUT', `/v1/channels/${CH}`, { body: env1, headers: { 'Content-Type': 'application/json' } }), 200);
  assert(d.ok === true && d.version === '0.0.1', JSON.stringify(d));
});
await test('latest: returns stored envelope verbatim', async () => {
  const res = await call('GET', `/v1/latest?channel=${CH}`, { token: null });
  const text = await res.text();
  assert(res.status === 200, `status ${res.status}`);
  assert(text === env1, 'body differs from what was stored');
  assert(res.headers.get('cache-control') === 'public, max-age=60', `cache-control ${res.headers.get('cache-control')}`);
  assert(res.headers.get('content-type') === JSON_TYPE, `content-type ${res.headers.get('content-type')}`);
});

// --- listing and deletion ---
await test('list: includes 0.0.1', async () => {
  const d = await expectStatus(await call('GET', '/v1/releases'), 200);
  assert(d.versions.includes('0.0.1'), JSON.stringify(d));
});
await test('list: POST -> 405', async () => expectStatus(await call('POST', '/v1/releases'), 405));
await test('delete: bad version -> 400', async () => expectStatus(await call('DELETE', '/v1/releases/latest'), 400));
await test('delete: live version -> 409', async () => expectStatus(await call('DELETE', '/v1/releases/0.0.1'), 409, CH));
await test('delete: after repointing channel -> 200', async () => {
  await upload('0.0.2');
  await expectStatus(await call('PUT', `/v1/channels/${CH}`, { body: envelope({ version: '0.0.2' }) }), 200);
  const d = await expectStatus(await call('DELETE', '/v1/releases/0.0.1'), 200);
  assert(d.deleted === 2, JSON.stringify(d));
  assert((await call('HEAD', '/v1/download/0.0.1/LiteShot.exe')).status === 404, 'file still there');
});
await test('delete: missing version -> 404', async () => expectStatus(await call('DELETE', '/v1/releases/0.0.1'), 404));
await test('list: 0.0.1 gone, 0.0.2 present', async () => {
  const d = await expectStatus(await call('GET', '/v1/releases'), 200);
  assert(!d.versions.includes('0.0.1') && d.versions.includes('0.0.2'), JSON.stringify(d));
});
await test('delete: PUT -> 405', async () => expectStatus(await call('PUT', '/v1/releases/0.0.2', { body: 'x' }), 405));

console.log(failures ? `\n${failures} failure(s)` : '\nall passed');
process.exit(failures ? 1 : 0);
