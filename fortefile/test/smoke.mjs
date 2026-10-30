// Endpoint smoke test against a running server (`npm run dev` in another terminal, then `npm test`).
//   BASE_URL  default http://127.0.0.1:8790
//   E2E_FILE  optional: a real file to run through the encrypt → upload → download → decrypt round trip
// Expiry and cron tests need `npm run dev` (TEST_HOOKS=1, --test-scheduled); they are skipped otherwise.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
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
const SITE_PAGES = ['/', '/security', '/terms', '/privacy', '/acceptable-use', '/copyright'];
const fetched = new Map();
async function getText(path) {
  if (!fetched.has(path)) {
    const res = await call('GET', path);
    fetched.set(path, { status: res.status, type: res.headers.get('content-type') ?? '', csp: res.headers.get('content-security-policy') ?? '', text: await res.text() });
  }
  return fetched.get(path);
}
await test('security and legal pages are served with page headers', async () => {
  for (const path of SITE_PAGES.slice(1)) {
    const page = await getText(path);
    assert(page.status === 200 && page.type.startsWith('text/html'), `${path}: ${page.status} ${page.type}`);
    assert(page.csp.includes("script-src 'self'") && page.csp.includes("frame-ancestors 'none'"), `${path}: csp ${page.csp}`);
    assert(page.text.includes('Devgm, LLC') && page.text.includes('Last changed October 28, 2026'), `${path}: content missing`);
  }
});
await test('site pages load nothing from other origins', async () => {
  for (const path of SITE_PAGES) {
    const { text } = await getText(path);
    // Anything the browser would fetch: scripts, stylesheets/icons/preloads, images, frames (canonical links are not fetched).
    const external = text.match(/<(script|img|iframe|link(?![^>]*rel="canonical"))\b[^>]*\b(src|href)="(https?:)?\/\/[^"]*"/g);
    assert(!external, `${path}: ${external}`);
  }
});
await test('every internal link and #anchor on the site pages resolves', async () => {
  const checked = new Set();
  for (const page of SITE_PAGES) {
    const { text } = await getText(page);
    for (const [, href] of text.matchAll(/href="([^"]+)"/g)) {
      if (/^(https?:|mailto:)/.test(href)) continue;
      const [path, hash] = href.startsWith('#') ? [page, href.slice(1)] : href.split('#');
      if (checked.has(`${path}#${hash}`)) continue;
      checked.add(`${path}#${hash}`);
      const target = await getText(path);
      assert(target.status === 200, `${page} links to ${href}: ${target.status}`);
      if (hash) assert(target.text.includes(`id="${hash}"`), `${page} links to ${href}, but ${path} has no id="${hash}"`);
    }
  }
});
await test('badge animations, their static icons and the self-hosted player are served', async () => {
  const sprite = await getText('/assets/badges.svg');
  assert(sprite.status === 200 && sprite.type.includes('image/svg+xml'), `badges.svg ${sprite.status} ${sprite.type}`);
  for (const name of ['lock', 'shield', 'key', 'timer', 'trash', 'eye-off']) {
    const anim = await getText(`/lottie/${name}.json`);
    assert(anim.status === 200, `/lottie/${name}.json ${anim.status}`);
    const data = JSON.parse(anim.text);
    assert(data.w === 96 && data.h === 96 && data.layers.length > 0, `/lottie/${name}.json is not a 96x96 animation`);
    assert(sprite.text.includes(`<symbol id="${name}"`), `badges.svg lacks ${name}`);
  }
  const player = await getText('/vendor/lottie_light.min.js');
  assert(player.status === 200 && player.type.includes('javascript') && player.text.includes('lottie'), `player ${player.status} ${player.type}`);
  assert((await getText('/vendor/lottie-web-LICENSE.md')).status === 200, 'player license missing');
});
await test('/.well-known/security.txt lists the security contact and a future expiry', async () => {
  const txt = await getText('/.well-known/security.txt');
  assert(txt.status === 200 && txt.type.startsWith('text/plain'), `${txt.status} ${txt.type}`);
  assert(txt.text.includes('Contact: mailto:security@fortefile.com'), 'no contact');
  const expires = Date.parse(/^Expires: (.+)$/m.exec(txt.text)?.[1] ?? '');
  assert(expires > Date.now(), 'Expires is missing or in the past');
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
// `npm run dev` passes --local-upstream (otherwise wrangler would rewrite every request to the fortefile.com
// route), so the worker sees 127.0.0.1 whatever Host is sent and these two skip there.
let keepsHost = false;
await test('test hooks are ignored for Host: fortefile.com, and urls use that host', async () => {
  const res = await rawRequest('POST', '/api/files', { Host: 'fortefile.com', 'X-Test-Expire-In': '-5' }, small);
  assert(res.status === 201, `status ${res.status} ${res.text}`);
  const d = JSON.parse(res.text);
  created.push(d);
  keepsHost = !d.url.startsWith(`${BASE}/`);
  if (!keepsHost) return 'skip';
  assert(near(d.expires, 86400), `hook honored for Host fortefile.com: ${d.expires}`);
  assert(d.url === `http://fortefile.com/f/${d.id}`, `url ${d.url}`);
  const probe = await rawRequest('GET', '/__test/files/AAAAAAAAAAAAAAAAAAAAAA', { Host: 'fortefile.com' });
  assert(!probe.headers['content-type']?.startsWith('application/json'), 'test route reachable for Host fortefile.com');
});
await test('www.fortefile.com pages redirect to fortefile.com', async () => {
  if (!keepsHost) return 'skip';
  const res = await rawRequest('GET', '/f/AAAAAAAAAAAAAAAAAAAAAA?x=1', { Host: 'www.fortefile.com' });
  assert(res.status === 301 && res.headers.location === 'https://fortefile.com/f/AAAAAAAAAAAAAAAAAAAAAA?x=1', `${res.status} ${res.headers.location}`);
  const api = await rawRequest('GET', '/api/files/AAAAAAAAAAAAAAAAAAAAAA', { Host: 'www.fortefile.com' });
  assert(api.status === 404, `API on www: ${api.status}`);
});

// --- admin: takedowns and legal holds (`npm run dev` sets ADMIN_TOKEN=local-admin-token; elsewhere pass ADMIN_TOKEN) ---
const ADMIN = process.env.ADMIN_TOKEN ?? 'local-admin-token';
const adminCall = (method, path, body, token = ADMIN) =>
  call(method, path, { body: body && JSON.stringify(body), headers: { Authorization: `Bearer ${token}`, ...(body && { 'Content-Type': 'application/json' }) } });
const adminOn = await adminCall('GET', '/api/admin/holds').then(async (r) => (await r.arrayBuffer(), r.status === 200));
const holds = [];
async function hold(id, extra = {}) {
  const h = await expectStatus(await adminCall('POST', '/api/admin/holds', { id, reason: 'smoke test', ...extra }), 201);
  holds.push(h.id);
  return h;
}
const sha256hex = (buf) => createHash('sha256').update(buf).digest('hex');
const DAY = 86400;

await test('admin: no token, a wrong token or a delete token -> 401', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(small);
  await expectStatus(await call('GET', '/api/admin/holds'), 401);
  await expectStatus(await adminCall('GET', '/api/admin/holds', undefined, 'wrong'), 401);
  await expectStatus(await adminCall('DELETE', `/api/admin/files/${d.id}`, undefined, d.deleteToken), 401);
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: d.id, reason: 'x' }, d.deleteToken), 401);
});
await test("admin hold: the link dies, the uploader can't delete the copy, a working key is kept", async () => {
  if (!adminOn) return 'skip';
  const e = await encrypt(png);
  const d = await upload(e.stored);
  const h = await hold(d.id, { key: e.key });
  assert(h.id === d.id && h.reason === 'smoke test' && h.encrypted === true && h.type === 'application/octet-stream', JSON.stringify(h));
  assert(h.size === e.stored.length && h.sha256 === sha256hex(e.stored), 'size or sha256');
  assert(h.key === e.key && h.contentType === 'image/png', `key ${h.key}, contentType ${h.contentType}`);
  assert(near(h.keepUntil, 365 * DAY) && h.expires === d.expires && !h.report && h.notes.length === 0, JSON.stringify(h));
  await expectStatus(await call('GET', `/api/files/${d.id}`), 404);
  await expectStatus(await call('DELETE', `/api/files/${d.id}`, { headers: { Authorization: `Bearer ${d.deleteToken}` } }), 404);
  const res = await adminCall('GET', `/api/admin/holds/${d.id}/file`);
  const kept = Buffer.from(await res.arrayBuffer());
  assert(res.status === 200 && kept.equals(e.stored), `kept file: ${res.status}`);
  assert(res.headers.get('cache-control') === 'no-store' && !res.headers.get('access-control-allow-origin'), 'kept file headers');
  assert((await decrypt(kept, h.key)).equals(png), "kept file doesn't decrypt to the original");
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: d.id, reason: 'again' }), 409);
  const { holds: all } = await expectStatus(await adminCall('GET', '/api/admin/holds'), 200);
  assert(all.some((x) => x.id === d.id), 'hold not listed');
  assert((await expectStatus(await adminCall('GET', `/api/admin/holds/${d.id}`), 200)).sha256 === h.sha256, 'record differs');
});
await test("admin hold: a key that doesn't decrypt is noted, not kept", async () => {
  if (!adminOn) return 'skip';
  const d = await upload((await encrypt(png)).stored);
  const h = await hold(d.id, { key: b64url(crypto.getRandomValues(new Uint8Array(32))) });
  assert(!h.key && !h.contentType && h.notes.length === 1 && h.notes[0].includes("didn't decrypt"), JSON.stringify(h));
});
await test('admin hold: public image, then /r/ is 404', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(png, { 'X-Encrypted': '0', 'Content-Type': 'image/png' });
  const h = await hold(d.id);
  assert(h.encrypted === false && h.type === 'image/png' && h.sha256 === sha256hex(png), JSON.stringify(h));
  await expectStatus(await call('GET', `/r/${d.id}.png`), 404);
});
await test('admin hold: an expired file not yet cleaned up is kept, and the cron leaves holds alone', async () => {
  if (!adminOn || !hooks) return 'skip';
  const d = await expiredUpload();
  await hold(d.id);
  await (await call('GET', '/__scheduled?cron=0+*+*+*+*')).arrayBuffer();
  const res = await adminCall('GET', `/api/admin/holds/${d.id}/file`);
  assert(res.status === 200 && Buffer.from(await res.arrayBuffer()).equals(small), `kept file: ${res.status}`);
});
await test('admin hold: bad requests change nothing', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(small);
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: 'AAAAAAAAAAAAAAAAAAAAAA', reason: 'gone' }), 404, 'expired');
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: 'short', reason: 'x' }), 400, 'id');
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: d.id }), 400, 'reason');
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: d.id, reason: '  ' }), 400, 'reason');
  await expectStatus(await adminCall('POST', '/api/admin/holds', { id: d.id, reason: 'x', key: 5 }), 400, 'key');
  await expectStatus(await call('POST', '/api/admin/holds', { body: 'not json', headers: { Authorization: `Bearer ${ADMIN}` } }), 400, 'JSON');
  await expectStatus(await adminCall('GET', '/api/admin/holds/AAAAAAAAAAAAAAAAAAAAAA'), 404);
  const res = await call('GET', `/api/files/${d.id}`);
  assert(res.status === 200 && Buffer.from(await res.arrayBuffer()).equals(small), 'a rejected hold took the file down');
});
await test('admin hold: report, extend and notes only ever move keepUntil later', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(small);
  await hold(d.id);
  const path = `/api/admin/holds/${d.id}`;
  const reportedAt = new Date(Date.now() + 10 * DAY * 1000).toISOString();
  const r = await expectStatus(await adminCall('PATCH', path, { report: ' 123456 ', reportedAt }), 200);
  assert(r.report === '123456' && r.reportedAt === reportedAt && near(r.keepUntil, 375 * DAY), JSON.stringify(r));
  const s = await expectStatus(await adminCall('PATCH', path, { keepUntil: '2020-01-01' }), 200);
  assert(s.keepUntil === r.keepUntil, `keepUntil moved back to ${s.keepUntil}`);
  const far = new Date(Date.now() + 800 * DAY * 1000).toISOString();
  const t = await expectStatus(await adminCall('PATCH', path, { keepUntil: far, note: ' preservation request 42 ' }), 200);
  assert(t.keepUntil === far && t.notes.at(-1).endsWith(' preservation request 42'), JSON.stringify(t));
  await expectStatus(await adminCall('PATCH', path, { report: '' }), 400, 'report');
  await expectStatus(await adminCall('PATCH', path, { report: '1', reportedAt: 'yesterday' }), 400, 'reportedAt');
  await expectStatus(await adminCall('PATCH', path, { keepUntil: 'soon' }), 400, 'keepUntil');
  await expectStatus(await adminCall('PATCH', '/api/admin/holds/AAAAAAAAAAAAAAAAAAAAAA', { note: 'x' }), 404);
});
await test('admin release: refused before keepUntil, done with force=1', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(small);
  await hold(d.id);
  await expectStatus(await adminCall('DELETE', `/api/admin/holds/${d.id}`), 409, 'force=1');
  await expectStatus(await adminCall('DELETE', `/api/admin/holds/${d.id}?force=1`), 200);
  await expectStatus(await adminCall('GET', `/api/admin/holds/${d.id}`), 404);
  await expectStatus(await adminCall('GET', `/api/admin/holds/${d.id}/file`), 404);
});
await test('admin remove: the link dies and nothing is kept', async () => {
  if (!adminOn) return 'skip';
  const d = await upload(small);
  await expectStatus(await adminCall('DELETE', `/api/admin/files/${d.id}`), 200);
  await expectStatus(await call('GET', `/api/files/${d.id}`), 404);
  await expectStatus(await adminCall('GET', `/api/admin/holds/${d.id}`), 404);
  await expectStatus(await adminCall('DELETE', `/api/admin/files/${d.id}`), 404);
});
await test('admin CLI: hold a whole link, report, list, export decrypted, release', async () => {
  if (!adminOn) return 'skip';
  const e = await encrypt(png);
  const d = await upload(e.stored);
  const out = mkdtempSync(join(tmpdir(), 'fortefile-admin-'));
  const env = { ...process.env, FORTEFILE_URL: BASE, FORTEFILE_ADMIN_TOKEN: ADMIN };
  const cli = (...args) => execFileSync(process.execPath, [fileURLToPath(new URL('../tools/admin.mjs', import.meta.url)), ...args], { env, encoding: 'utf8', stdio: 'pipe' });
  try {
    assert(cli('hold', `${d.url}#${e.key}`, '--reason', 'smoke test (CLI)').includes('key kept (image/png)'), 'hold output');
    holds.push(d.id);
    assert(cli('report', d.id, '987654', '--date', '2026-10-28').includes('Recorded report 987654'), 'report output');
    assert(cli('list').includes(d.id), 'not listed');
    assert(cli('export', d.id, '--out', out).includes('SHA-256 checked'), 'export output');
    assert(readFileSync(join(out, `fortefile-${d.id}.png`)).equals(png), 'exported file is not the original PNG');
    let refused = false;
    try {
      cli('release', d.id);
    } catch {
      refused = true;
    }
    assert(refused, 'released early without --force');
    assert(cli('release', `${BASE}/f/${d.id}`, '--force').includes('Released'), 'release output');
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
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
for (const id of holds) await adminCall('DELETE', `/api/admin/holds/${id}?force=1`).then((r) => r.arrayBuffer());
for (const d of created)
  await call('DELETE', `/api/files/${d.id}`, { headers: { Authorization: `Bearer ${d.deleteToken}` } }).then((r) => r.arrayBuffer());

console.log(failures ? `\n${failures} failure(s), ${skipped} skipped` : `\nall passed${skipped ? `, ${skipped} skipped` : ''}`);
process.exit(failures ? 1 : 0);
