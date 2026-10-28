// Endpoint smoke test against a running server (`npm run dev` in another terminal, then `npm test`).
//   BASE_URL  default http://127.0.0.1:8790
//   E2E_FILE  optional: a real file to run through the encrypt → upload → download → decrypt round trip
// Expiry and cron tests need `npm run dev` (TEST_HOOKS=1, --test-scheduled); they are skipped otherwise.
import { readFileSync } from 'node:fs';
import http from 'node:http';
import { deflateSync } from 'node:zlib';

const BASE = (process.env.BASE_URL ?? 'http://127.0.0.1:8790').replace(/\/+$/, '');
const JSON_TYPE = 'application/json; charset=utf-8';
const MiB = 1024 * 1024;
const subtle = globalThis.crypto.subtle;

let failures = 0;
let skipped = 0;
async function test(name, fn) {
  try {
    const result = await fn();
    if (result === 'skip') {
      skipped++;
      console.log(`SKIP  ${name}`);
    } else console.log(`PASS  ${name}`);
  } catch (e) {
    failures++;
    console.log(`FAIL  ${name}: ${e.message}`);
  }
}
function assert(cond, message) {
  if (!cond) throw new Error(message);
}

// Every request gets its own IPv6 /64 so ordinary tests never share a rate-limit bucket.
const rand16 = () => Math.floor(Math.random() * 0x10000).toString(16);
const freshIp = () => `2001:db8:${rand16()}:${rand16()}::1`;

function call(method, path, { body, headers = {}, ip = freshIp() } = {}) {
  return fetch(BASE + path, { method, body, headers: { 'CF-Connecting-IP': ip, ...headers }, duplex: 'half', redirect: 'manual' });
}

async function expectStatus(res, status, errorIncludes) {
  const text = await res.text();
  assert(res.status === status, `expected ${status}, got ${res.status} ${text.slice(0, 200)}`);
  if (status >= 400 || res.headers.get('content-type')?.startsWith('application/json')) {
    assert(res.headers.get('content-type') === JSON_TYPE, `content-type was ${res.headers.get('content-type')}`);
  }
  const data = text ? JSON.parse(text) : null;
  if (status >= 400) assert(typeof data?.error === 'string', `no error field: ${text}`);
  if (errorIncludes) assert(data?.error?.includes(errorIncludes), `error "${data?.error}" lacks "${errorIncludes}"`);
  return data;
}

const b64url = (buf) => Buffer.from(buf).toString('base64url');
const near = (iso, seconds, slack = 120) => Math.abs(Date.parse(iso) - (Date.now() + seconds * 1000)) < slack * 1000;

// --- a real PNG, built by hand so the test needs no files ---
const CRC = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = CRC[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
function pngChunk(type, data) {
  const head = Buffer.alloc(4);
  head.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([head, body, crc]);
}
function makePng(w, h) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // truecolor RGB
  const stride = w * 3 + 1;
  const raw = Buffer.alloc(stride * h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) raw.set([(x * 255) / w, (y * 255) / h, 220 - ((x + y) % 40)], y * stride + 1 + x * 3);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw)),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}
const png = makePng(320, 200);
const jpeg = Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.alloc(400, 7), Buffer.from([0xff, 0xd9])]);

// --- the encryption contract: IV (12) || AES-256-GCM ciphertext || tag (16), key = base64url(32 bytes) ---
async function encrypt(plain) {
  const raw = crypto.getRandomValues(new Uint8Array(32));
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const key = await subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt']);
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv }, key, plain));
  return { stored: Buffer.concat([iv, ct]), key: b64url(raw) };
}
async function decrypt(stored, keyText) {
  const key = await subtle.importKey('raw', Buffer.from(keyText, 'base64url'), 'AES-GCM', false, ['decrypt']);
  return Buffer.from(await subtle.decrypt({ name: 'AES-GCM', iv: stored.subarray(0, 12) }, key, stored.subarray(12)));
}

const created = [];
async function upload(body, headers = {}) {
  const d = await expectStatus(await call('POST', '/api/files', { body, headers }), 201);
  created.push(d);
  return d;
}
const inspect = async (id) => (await call('GET', `/__test/files/${id}`)).json();

// --- pages and routing ---
await test('GET / serves the landing page with security headers', async () => {
  const res = await call('GET', '/');
  const html = await res.text();
  assert(res.status === 200, `status ${res.status}`);
  assert(res.headers.get('content-type')?.startsWith('text/html'), `content-type ${res.headers.get('content-type')}`);
  assert(html.includes('fortefile') && html.includes('© 2026 Devgm, LLC'), 'landing content missing');
  const csp = res.headers.get('content-security-policy') ?? '';
  assert(csp.includes("default-src 'self'") && csp.includes("frame-ancestors 'none'"), `csp ${csp}`);
  assert(res.headers.get('x-content-type-options') === 'nosniff', 'no nosniff');
  assert(res.headers.get('referrer-policy') === 'no-referrer', 'no referrer-policy');
});
await test('static assets are served', async () => {
  for (const [path, type] of [['/favicon.svg', 'image/svg+xml'], ['/assets/site.css', 'text/css'], ['/assets/viewer.js', 'javascript']]) {
    const res = await call('GET', path);
    await res.arrayBuffer();
    assert(res.status === 200 && res.headers.get('content-type')?.includes(type), `${path}: ${res.status} ${res.headers.get('content-type')}`);
  }
});
await test('unknown page -> 404 page', async () => {
  const res = await call('GET', '/no-such-page');
  assert(res.status === 404 && (await res.text()).includes('Nothing'), `status ${res.status}`);
});
await test('unknown API path -> 404 JSON', async () => {
  await expectStatus(await call('GET', '/api/nope'), 404);
  await expectStatus(await call('GET', '/api'), 404);
  await expectStatus(await call('GET', '/api/files/a/b'), 404);
});
await test('PUT /api/files -> 405 Allow: POST', async () => {
  const res = await call('PUT', '/api/files', { body: 'x' });
  assert(res.headers.get('allow') === 'POST', `Allow: ${res.headers.get('allow')}`);
  await expectStatus(res, 405);
});
await test('POST /api/files/{id} -> 405 Allow: GET, DELETE, HEAD', async () => {
  const res = await call('POST', '/api/files/AAAAAAAAAAAAAAAAAAAAAA', { body: 'x' });
  assert(res.headers.get('allow') === 'GET, DELETE, HEAD', `Allow: ${res.headers.get('allow')}`);
  await expectStatus(res, 405);
});
await test('DELETE /r/{id}.png -> 405', async () => expectStatus(await call('DELETE', '/r/AAAAAAAAAAAAAAAAAAAAAA.png'), 405));
await test('GET /api/files/{missing} -> 404', async () => expectStatus(await call('GET', '/api/files/AAAAAAAAAAAAAAAAAAAAAA'), 404));
await test('GET /api/files/{malformed id} -> 404', async () => expectStatus(await call('GET', '/api/files/not-an-id'), 404));
await test('HEAD /api/files/{missing} -> 404', async () => assert((await call('HEAD', '/api/files/AAAAAAAAAAAAAAAAAAAAAA')).status === 404, 'not 404'));

// --- upload validation ---
const small = (await encrypt(Buffer.from('hello'))).stored;
await test('upload: no Content-Length -> 411', async () => {
  const stream = new ReadableStream({ start(c) { c.enqueue(new Uint8Array(small)); c.close(); } });
  await expectStatus(await call('POST', '/api/files', { body: stream }), 411);
});
for (const bad of ['7200', 'abc', '86400.0', '-3600', '0'])
  await test(`upload: X-Lifetime ${bad} -> 400`, async () =>
    expectStatus(await call('POST', '/api/files', { body: small, headers: { 'X-Lifetime': bad } }), 400, 'X-Lifetime'));
await test('upload: X-Encrypted 2 -> 400', async () =>
  expectStatus(await call('POST', '/api/files', { body: small, headers: { 'X-Encrypted': '2' } }), 400, 'X-Encrypted'));
await test('upload: empty body -> 400', async () => expectStatus(await call('POST', '/api/files', { body: Buffer.alloc(0) }), 400));
await test('upload: encrypted body shorter than IV + tag -> 400', async () =>
  expectStatus(await call('POST', '/api/files', { body: Buffer.alloc(27, 1) }), 400));
await test('upload: public with text/html -> 400', async () =>
  expectStatus(await call('POST', '/api/files', { body: png, headers: { 'X-Encrypted': '0', 'Content-Type': 'text/html' } }), 400));
await test('upload: public without Content-Type -> 400', async () =>
  expectStatus(await call('POST', '/api/files', { body: png, headers: { 'X-Encrypted': '0' } }), 400));
await test('upload: public image/png that is not a PNG -> 400', async () =>
  expectStatus(await call('POST', '/api/files', { body: jpeg, headers: { 'X-Encrypted': '0', 'Content-Type': 'image/png' } }), 400));
await test('upload: 25 MiB + 1 byte -> 413', async () =>
  expectStatus(await call('POST', '/api/files', { body: Buffer.alloc(25 * MiB + 1) }), 413));
await test('upload: exactly 25 MiB -> 201', async () => {
  const d = await upload(Buffer.alloc(25 * MiB, 3));
  assert(d.size === 25 * MiB, `size ${d.size}`);
});

// --- encrypted upload, download, viewer ---
const enc = await encrypt(png);
let file;
await test('upload: encrypted (defaults) -> 201 with the contract fields', async () => {
  file = await upload(enc.stored);
  assert(/^[A-Za-z0-9_-]{22}$/.test(file.id), `id ${file.id}`);
  assert(/^[A-Za-z0-9_-]{43}$/.test(file.deleteToken), `deleteToken ${file.deleteToken}`);
  assert(file.size === enc.stored.length, `size ${file.size}`);
  assert(file.url === `${BASE}/f/${file.id}`, `url ${file.url}`);
  assert(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$/.test(file.expires) && near(file.expires, 86400), `expires ${file.expires}`);
});
for (const lifetime of [3600, 43200, 86400])
  await test(`upload: X-Lifetime ${lifetime} sets expires`, async () => {
    const d = await upload(small, { 'X-Lifetime': String(lifetime), 'X-Encrypted': '1', 'Content-Type': 'application/octet-stream' });
    assert(near(d.expires, lifetime), `expires ${d.expires}`);
  });
await test('upload: two uploads get different ids and tokens', async () => {
  const [a, b] = [await upload(small), await upload(small)];
  assert(a.id !== b.id && a.deleteToken !== b.deleteToken, 'repeated id or token');
});
await test('GET /api/files/{id}: bytes and headers', async () => {
  const res = await call('GET', `/api/files/${file.id}`);
  const body = Buffer.from(await res.arrayBuffer());
  const h = (n) => res.headers.get(n);
  assert(res.status === 200, `status ${res.status}`);
  assert(body.equals(enc.stored), 'body differs');
  assert(h('content-type') === 'application/octet-stream', `content-type ${h('content-type')}`);
  assert(h('content-length') === String(enc.stored.length), `content-length ${h('content-length')}`);
  assert(h('x-expires') === file.expires, `x-expires ${h('x-expires')} vs ${file.expires}`);
  assert(h('cache-control') === 'no-store', `cache-control ${h('cache-control')}`);
  assert(h('access-control-allow-origin') === '*', 'no CORS');
  assert(h('x-content-type-options') === 'nosniff', 'no nosniff');
});
await test('HEAD /api/files/{id}: headers, no body', async () => {
  const res = await call('HEAD', `/api/files/${file.id}`);
  assert(res.status === 200, `status ${res.status}`);
  assert(res.headers.get('content-length') === String(enc.stored.length), `content-length ${res.headers.get('content-length')}`);
  assert(res.headers.get('x-expires') === file.expires && res.headers.get('cache-control') === 'no-store', 'missing headers');
  assert((await res.arrayBuffer()).byteLength === 0, 'HEAD had a body');
});
await test('E2E: download + decrypt with the link key gives the original PNG', async () => {
  const res = await call('GET', `/api/files/${file.id}`);
  const plain = await decrypt(Buffer.from(await res.arrayBuffer()), enc.key);
  assert(plain.equals(png), 'decrypted bytes differ from the original');
});
await test('E2E: a wrong key fails to decrypt', async () => {
  const res = await call('GET', `/api/files/${file.id}`);
  const wrong = b64url(crypto.getRandomValues(new Uint8Array(32)));
  let failed = false;
  try { await decrypt(Buffer.from(await res.arrayBuffer()), wrong); } catch { failed = true; }
  assert(failed, 'decrypted with a random key');
});
if (process.env.E2E_FILE)
  await test(`E2E: round trip of ${process.env.E2E_FILE}`, async () => {
    const original = readFileSync(process.env.E2E_FILE);
    const e = await encrypt(original);
    const d = await upload(e.stored);
    const res = await call('GET', `/api/files/${d.id}`);
    assert((await decrypt(Buffer.from(await res.arrayBuffer()), e.key)).equals(original), 'round trip differs');
    console.log(`      viewer link: ${d.url}#${e.key}`);
  });
await test('GET /f/{id}: viewer page, noindex, CSP', async () => {
  const res = await call('GET', `/f/${file.id}`);
  const html = await res.text();
  assert(res.status === 200 && html.includes('/assets/viewer.js'), `status ${res.status}`);
  assert(res.headers.get('x-robots-tag')?.includes('noindex'), 'no x-robots-tag');
  const csp = res.headers.get('content-security-policy') ?? '';
  for (const part of ["img-src 'self' blob: data:", "media-src 'self' blob:", "connect-src 'self'", "script-src 'self'"])
    assert(csp.includes(part), `csp lacks ${part}`);
});
await test('GET /f/{malformed} -> 404 page', async () => assert((await call('GET', '/f/short')).status === 404, 'not 404'));
await test('POST /f/{id} -> 405', async () => expectStatus(await call('POST', `/f/${file.id}`, { body: 'x' }), 405));
await test('GET /r/{encrypted id}.png -> 404', async () => expectStatus(await call('GET', `/r/${file.id}.png`), 404));

// --- public (unencrypted) uploads for reverse image search ---
let pub;
await test('upload: public PNG -> 201, /r/ url, capped at 1 hour', async () => {
  pub = await upload(png, { 'X-Encrypted': '0', 'Content-Type': 'image/png', 'X-Lifetime': '86400' });
  assert(pub.url === `${BASE}/r/${pub.id}.png`, `url ${pub.url}`);
  assert(near(pub.expires, 3600), `expires ${pub.expires} is not ~1 h away`);
});
await test('GET /r/{id}.png: image, public cache', async () => {
  const res = await call('GET', `/r/${pub.id}.png`);
  const body = Buffer.from(await res.arrayBuffer());
  assert(res.status === 200 && body.equals(png), `status ${res.status}`);
  assert(res.headers.get('content-type') === 'image/png', `content-type ${res.headers.get('content-type')}`);
  assert(res.headers.get('cache-control') === 'public, max-age=300', `cache-control ${res.headers.get('cache-control')}`);
  assert(res.headers.get('x-expires') === pub.expires, 'x-expires');
});
await test('HEAD /r/{id}.png', async () => {
  const res = await call('HEAD', `/r/${pub.id}.png`);
  assert(res.status === 200 && res.headers.get('content-length') === String(png.length), `status ${res.status}`);
});
await test('GET /r/{id}.jpg for a PNG -> 404', async () => expectStatus(await call('GET', `/r/${pub.id}.jpg`), 404));
await test('GET /api/files/{public id}: image/png, no-store', async () => {
  const res = await call('GET', `/api/files/${pub.id}`);
  await res.arrayBuffer();
  assert(res.headers.get('content-type') === 'image/png' && res.headers.get('cache-control') === 'no-store', 'headers');
});
await test('upload: public JPEG (Content-Type with parameters) -> /r/{id}.jpg', async () => {
  const d = await upload(jpeg, { 'X-Encrypted': '0', 'Content-Type': 'image/jpeg; charset=binary', 'X-Lifetime': '3600' });
  assert(d.url === `${BASE}/r/${d.id}.jpg` && near(d.expires, 3600), JSON.stringify(d));
  const res = await call('GET', `/r/${d.id}.jpg`);
  assert(res.status === 200 && res.headers.get('content-type') === 'image/jpeg', `status ${res.status}`);
  assert(Buffer.from(await res.arrayBuffer()).equals(jpeg), 'body differs');
});

// --- delete ---
await test('DELETE: no token -> 401', async () => {
  const res = await call('DELETE', `/api/files/${file.id}`);
  assert(res.headers.get('www-authenticate') === 'Bearer', 'no WWW-Authenticate');
  await expectStatus(res, 401);
});
await test('DELETE: wrong token -> 401', async () =>
  expectStatus(await call('DELETE', `/api/files/${file.id}`, { headers: { Authorization: `Bearer ${pub.deleteToken}` } }), 401));
await test('DELETE: Basic scheme -> 401', async () =>
  expectStatus(await call('DELETE', `/api/files/${file.id}`, { headers: { Authorization: `Basic ${file.deleteToken}` } }), 401));
await test('DELETE: missing file -> 404', async () =>
  expectStatus(await call('DELETE', '/api/files/AAAAAAAAAAAAAAAAAAAAAA', { headers: { Authorization: `Bearer ${file.deleteToken}` } }), 404));
await test('DELETE: right token -> 200, then 404 everywhere', async () => {
  const d = await expectStatus(await call('DELETE', `/api/files/${file.id}`, { headers: { Authorization: `Bearer ${file.deleteToken}` } }), 200);
  assert(d.ok === true, JSON.stringify(d));
  await expectStatus(await call('GET', `/api/files/${file.id}`), 404);
  assert((await call('HEAD', `/api/files/${file.id}`)).status === 404, 'HEAD not 404');
  await expectStatus(await call('DELETE', `/api/files/${file.id}`, { headers: { Authorization: `Bearer ${file.deleteToken}` } }), 404);
});
await test('DELETE: public file, then /r/ is 404', async () => {
  await expectStatus(await call('DELETE', `/api/files/${pub.id}`, { headers: { Authorization: `bearer ${pub.deleteToken}` } }), 200);
  await expectStatus(await call('GET', `/r/${pub.id}.png`), 404);
});

// --- expiry (needs TEST_HOOKS) and the cron cleanup (needs --test-scheduled) ---
const hooks = (await call('GET', '/__test/files/AAAAAAAAAAAAAAAAAAAAAA')).headers.get('content-type') === JSON_TYPE;
const expiredUpload = (body = small, headers = {}) => upload(body, { 'X-Lifetime': '3600', 'X-Test-Expire-In': '-5', ...headers });

await test('expiry: expired file -> GET 404 and the object is deleted', async () => {
  if (!hooks) return 'skip';
  const d = await expiredUpload();
  assert(Date.parse(d.expires) < Date.now(), `expires ${d.expires} is not in the past`);
  assert((await inspect(d.id)).exists, 'object missing before GET');
  await expectStatus(await call('GET', `/api/files/${d.id}`), 404);
  assert(!(await inspect(d.id)).exists, 'expired object still stored after GET');
});
await test('expiry: expired file -> HEAD 404, DELETE 404', async () => {
  if (!hooks) return 'skip';
  const a = await expiredUpload();
  assert((await call('HEAD', `/api/files/${a.id}`)).status === 404, 'HEAD not 404');
  const b = await expiredUpload();
  await expectStatus(await call('DELETE', `/api/files/${b.id}`, { headers: { Authorization: `Bearer ${b.deleteToken}` } }), 404);
  assert(!(await inspect(a.id)).exists && !(await inspect(b.id)).exists, 'expired objects still stored');
});
await test('expiry: expired public file -> /r/ 404', async () => {
  if (!hooks) return 'skip';
  const d = await expiredUpload(png, { 'X-Encrypted': '0', 'Content-Type': 'image/png' });
  await expectStatus(await call('GET', `/r/${d.id}.png`), 404);
});
await test('expiry: file with 1 h left is still served', async () => {
  if (!hooks) return 'skip';
  const d = await upload(small, { 'X-Lifetime': '3600', 'X-Test-Expire-In': '3590' });
  const res = await call('GET', `/api/files/${d.id}`);
  assert(res.status === 200 && Buffer.from(await res.arrayBuffer()).equals(small), `status ${res.status}`);
});
await test('cron: /__scheduled deletes expired files and keeps live ones', async () => {
  if (!hooks) return 'skip';
  const expired = [await expiredUpload(), await expiredUpload(png, { 'X-Encrypted': '0', 'Content-Type': 'image/png' })];
  const live = await upload(small, { 'X-Lifetime': '3600' });
  for (const d of [...expired, live]) assert((await inspect(d.id)).exists, `${d.id} missing before cron`);
  const res = await call('GET', '/__scheduled?cron=0+*+*+*+*');
  const text = await res.text();
  assert(res.status === 200, `/__scheduled ${res.status} ${text} (is wrangler dev running with --test-scheduled?)`);
  for (const d of expired) assert(!(await inspect(d.id)).exists, `${d.id} survived the cron`);
  assert((await inspect(live.id)).exists, 'live file was deleted by the cron');
  const res2 = await call('GET', `/api/files/${live.id}`);
  assert(res2.status === 200 && Buffer.from(await res2.arrayBuffer()).equals(small), 'live file not served after cron');
});
// fetch() can't set Host, so these go through node:http to look like requests for fortefile.com.
function rawRequest(method, path, headers, body) {
  return new Promise((resolve, reject) => {
    const u = new URL(BASE);
    const req = http.request({ host: u.hostname, port: u.port, method, path, headers: { 'CF-Connecting-IP': freshIp(), 'Content-Length': body?.length ?? 0, ...headers } }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, text: Buffer.concat(chunks).toString() }));
    });
    req.on('error', reject);
    req.end(body);
  });
}
await test('test hooks are ignored for Host: fortefile.com, and urls use that host', async () => {
  const res = await rawRequest('POST', '/api/files', { Host: 'fortefile.com', 'X-Test-Expire-In': '-5' }, small);
  assert(res.status === 201, `status ${res.status} ${res.text}`);
  const d = JSON.parse(res.text);
  created.push(d);
  assert(near(d.expires, 86400), `hook honored for Host fortefile.com: ${d.expires}`);
  assert(d.url === `http://fortefile.com/f/${d.id}`, `url ${d.url}`);
  const probe = await rawRequest('GET', '/__test/files/AAAAAAAAAAAAAAAAAAAAAA', { Host: 'fortefile.com' });
  assert(!probe.headers['content-type']?.startsWith('application/json'), 'test route reachable for Host fortefile.com');
});
await test('www.fortefile.com pages redirect to fortefile.com', async () => {
  const res = await rawRequest('GET', '/f/AAAAAAAAAAAAAAAAAAAAAA?x=1', { Host: 'www.fortefile.com' });
  assert(res.status === 301 && res.headers.location === 'https://fortefile.com/f/AAAAAAAAAAAAAAAAAAAAAA?x=1', `${res.status} ${res.headers.location}`);
  const api = await rawRequest('GET', '/api/files/AAAAAAAAAAAAAAAAAAAAAA', { Host: 'www.fortefile.com' });
  assert(api.status === 404, `API on www: ${api.status}`);
});

// --- rate limit: 30 uploads per minute per IP (IPv6 grouped by /64) ---
await test('rate limit: 429 after 30 uploads a minute from one /64', async () => {
  const net = `2001:db8:${rand16()}:${rand16()}`;
  let n = 0;
  let limited;
  // Rejected uploads count too, so this stores nothing. Fixed windows can split a burst; allow two windows.
  while (n < 61) {
    n++;
    const res = await call('POST', '/api/files', { body: small, headers: { 'X-Lifetime': 'bad' }, ip: `${net}::${n.toString(16)}` });
    if (res.status === 429) {
      limited = res;
      break;
    }
    await expectStatus(res, 400);
  }
  assert(limited, 'never limited after 61 attempts (is the UPLOAD_LIMITER binding active?)');
  assert(n >= 31, `limited after only ${n - 1} attempts`);
  assert(limited.headers.get('retry-after') === '60', `retry-after ${limited.headers.get('retry-after')}`);
  await expectStatus(limited, 429);
  await expectStatus(await call('POST', '/api/files', { body: small, headers: { 'X-Lifetime': 'bad' }, ip: `${net}::ffff` }), 429);
  await expectStatus(await call('POST', '/api/files', { body: small, headers: { 'X-Lifetime': 'bad' }, ip: freshIp() }), 400);
});

// Leave nothing behind but what the expiry tests already removed.
for (const d of created)
  await call('DELETE', `/api/files/${d.id}`, { headers: { Authorization: `Bearer ${d.deleteToken}` } }).then((r) => r.arrayBuffer());

console.log(failures ? `\n${failures} failure(s), ${skipped} skipped` : `\nall passed${skipped ? `, ${skipped} skipped` : ''}`);
process.exit(failures ? 1 : 0);
