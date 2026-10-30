// fortefile: encrypted, self-deleting file links.
//   files/{id}  uploaded bytes; customMetadata { expires (ms), deleteHash (hex sha256 of the delete token), encrypted, type }
// Encrypted files are AES-256-GCM output (IV || ciphertext || tag) made by the uploader. The key only ever
// travels in the link's #fragment, so this worker never sees it. Public (unencrypted) files are PNG/JPEG
// screenshots that live at most an hour, so a reverse image search can fetch them.
//
// The HOLDS bucket keeps reported files for legal reasons (18 U.S.C. § 2258A(h): a year after a report to
// NCMEC). No lifecycle rule or cron touches it, and only the /api/admin routes (tools/admin.mjs) can read it.
//   holds/{id}/file         the bytes exactly as they were stored, with the original customMetadata
//   holds/{id}/record.json  a Hold: why, when, hashes, the reporter's key if it works, the report number

interface Env {
  FILES: R2Bucket;
  HOLDS: R2Bucket;
  ASSETS: Fetcher;
  UPLOAD_LIMITER?: RateLimit;
  ADMIN_TOKEN?: string; // secret; without it the admin routes don't exist
  TEST_HOOKS?: string; // "1" only in `npm run dev`: allows X-Test-Expire-In and /__test/files/{id} on localhost
}

interface Hold {
  id: string;
  reason: string;
  heldAt: string;
  size: number;
  sha256: string; // of the stored bytes (ciphertext for encrypted files)
  uploaded: string;
  expires: string; // when the file would have been deleted
  encrypted: boolean;
  type: string; // as stored: application/octet-stream for every encrypted file
  key?: string; // from the reported link's #fragment, kept only when it decrypts the file
  contentType?: string; // sniffed from the decrypted bytes
  report?: string; // NCMEC CyberTipline report number
  reportedAt?: string;
  keepUntil: string; // a year after the report (or the hold), later if extended; release refuses before it
  notes: string[];
}

type Handler = (req: Request, env: Env, params: string[], url: URL) => Promise<Response>;

const JSON_TYPE = 'application/json; charset=utf-8';
const MAX_BYTES = 25 * 1024 * 1024;
const LIFETIMES = [3600, 43200, 86400];
const DEFAULT_LIFETIME = 86400;
const PUBLIC_MAX_LIFETIME = 3600;
const HOLD_DAYS = 365;
const MIN_ENCRYPTED_BYTES = 12 + 16; // IV + GCM tag
const ID_RE = /^[A-Za-z0-9_-]{22}$/;
const PUBLIC_TYPES: Record<string, { ext: string; magic: number[] }> = {
  'image/png': { ext: 'png', magic: [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a] },
  'image/jpeg': { ext: 'jpg', magic: [0xff, 0xd8, 0xff] },
};

// Stored files are served as inert data: never sniffed, never rendered as a document on this origin.
const FILE_HEADERS = {
  'X-Content-Type-Options': 'nosniff',
  'Content-Security-Policy': "default-src 'none'; sandbox",
  'X-Robots-Tag': 'noindex, nofollow',
  'Referrer-Policy': 'no-referrer',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Expose-Headers': 'X-Expires, Content-Length',
};

const PAGE_HEADERS = {
  'Content-Security-Policy': [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self'",
    "img-src 'self' blob: data:",
    "media-src 'self' blob:",
    "connect-src 'self'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
    "frame-ancestors 'none'",
    "require-trusted-types-for 'script'",
  ].join('; '),
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=(), usb=()',
  'Cross-Origin-Opener-Policy': 'same-origin',
};

class HttpError extends Error {
  constructor(readonly status: number, message: string, readonly headers: Record<string, string> = {}) {
    super(message);
  }
}

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': JSON_TYPE, 'Cache-Control': 'no-store', ...headers } });

function check(ok: unknown, message: string, status = 400): asserts ok {
  if (!ok) throw new HttpError(status, message);
}

const notFound = () => json({ error: 'not found' }, 404, FILE_HEADERS);
const fileKey = (id: string) => `files/${id}`;
const b64url = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const randomToken = (bytes: number) => b64url(crypto.getRandomValues(new Uint8Array(bytes)));
const sha256 = (s: string) => crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
const hex = (buf: ArrayBuffer) => [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
const unhex = (s: string) => (/^[0-9a-f]{64}$/.test(s) ? new Uint8Array(s.match(/../g)!.map((h) => parseInt(h, 16))) : null);
const isExpired = (obj: R2Object, now = Date.now()) => !(Number(obj.customMetadata?.expires) > now);
const expiresIso = (obj: R2Object) => new Date(Number(obj.customMetadata!.expires)).toISOString();
const bodyOf = (obj: R2Object) => ('body' in obj ? (obj as R2ObjectBody).body : null);
const testHooks = (env: Env, url: URL) => env.TEST_HOOKS === '1' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname);
const bearer = (req: Request) => /^Bearer\s+(\S+)\s*$/i.exec(req.headers.get('Authorization') ?? '')?.[1];
const unauthorized = () => new HttpError(401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
const holdKey = (id: string, part: 'file' | 'record.json') => `holds/${id}/${part}`;
const daysAfter = (date: Date, days: number) => new Date(date.getTime() + days * 86400_000);
const sniff = (bytes: Uint8Array) =>
  Object.keys(PUBLIC_TYPES).find((t) => PUBLIC_TYPES[t].magic.every((b, i) => bytes[i] === b)) ?? 'unknown';

// IPv6 users usually own a whole /64, so limit by prefix rather than by address.
function clientKey(ip: string) {
  if (!ip.includes(':')) return ip;
  const [head, tail] = ip.split('::');
  const h = head ? head.split(':') : [];
  const t = tail ? tail.split(':') : [];
  const groups = tail === undefined ? h : [...h, ...Array(Math.max(0, 8 - h.length - t.length)).fill('0'), ...t];
  return `${groups.slice(0, 4).map((g) => parseInt(g || '0', 16).toString(16)).join(':')}::/64`;
}

async function rateLimit(req: Request, env: Env) {
  if (!env.UPLOAD_LIMITER) return;
  let allowed = true;
  try {
    allowed = (await env.UPLOAD_LIMITER.limit({ key: clientKey(req.headers.get('CF-Connecting-IP') ?? 'unknown') })).success;
  } catch (e) {
    console.error('rate limiter unavailable', e); // fail open: uploads are still capped in size and lifetime
  }
  if (!allowed) throw new HttpError(429, 'too many uploads, try again in a minute', { 'Retry-After': '60' });
}

// Returns the live object, or null when it is missing, malformed or expired (expired objects are deleted).
async function load(env: Env, id: string, head: boolean) {
  if (!ID_RE.test(id)) return null;
  const obj = head ? await env.FILES.head(fileKey(id)) : await env.FILES.get(fileKey(id));
  if (!obj) return null;
  if (!isExpired(obj)) return obj;
  await bodyOf(obj)?.cancel();
  await env.FILES.delete(fileKey(id));
  return null;
}

const serve = (obj: R2Object, cacheControl: string) =>
  new Response(bodyOf(obj), {
    headers: {
      'Content-Type': obj.customMetadata?.type ?? 'application/octet-stream',
      'Content-Length': String(obj.size),
      'X-Expires': expiresIso(obj),
      'Cache-Control': cacheControl,
      ETag: obj.httpEtag,
      ...FILE_HEADERS,
    },
  });

async function page(req: Request, env: Env, url: URL, path?: string, extra: Record<string, string> = {}) {
  const res = await env.ASSETS.fetch(path ? new Request(new URL(path, url), req) : req);
  const out = new Response(res.body, res);
  for (const [k, v] of Object.entries({ ...PAGE_HEADERS, ...extra })) out.headers.set(k, v);
  if (url.protocol === 'https:') out.headers.set('Strict-Transport-Security', 'max-age=31536000; includeSubDomains');
  return out;
}

const upload: Handler = async (req, env, _params, url) => {
  await rateLimit(req, env);
  const length = req.headers.get('Content-Length');
  check(length !== null, 'Content-Length required', 411);
  check(/^\d+$/.test(length), 'invalid Content-Length');
  check(Number(length) <= MAX_BYTES, 'file is larger than 25 MiB', 413);
  const lifetime = LIFETIMES.find((l) => String(l) === (req.headers.get('X-Lifetime') ?? String(DEFAULT_LIFETIME)));
  check(lifetime, 'X-Lifetime must be 3600, 43200 or 86400');
  const encryptedHeader = req.headers.get('X-Encrypted') ?? '1';
  check(encryptedHeader === '1' || encryptedHeader === '0', 'X-Encrypted must be 1 or 0');
  const encrypted = encryptedHeader === '1';
  const type = encrypted ? 'application/octet-stream' : (req.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase();
  check(encrypted || PUBLIC_TYPES[type], 'unencrypted uploads must be image/png or image/jpeg');

  const body = new Uint8Array(await req.arrayBuffer());
  check(body.byteLength <= MAX_BYTES, 'file is larger than 25 MiB', 413);
  check(body.byteLength > 0, 'empty body');
  if (encrypted) check(body.byteLength >= MIN_ENCRYPTED_BYTES, 'encrypted body is shorter than IV + tag');
  else check(PUBLIC_TYPES[type].magic.every((b, i) => body[i] === b), `body is not a ${type} file`);

  let expiresIn = encrypted ? lifetime : Math.min(lifetime, PUBLIC_MAX_LIFETIME);
  const override = testHooks(env, url) ? req.headers.get('X-Test-Expire-In') : null;
  if (override !== null) {
    check(/^-?\d+$/.test(override), 'invalid X-Test-Expire-In');
    expiresIn = Number(override);
  }
  const expires = Date.now() + expiresIn * 1000;
  const id = randomToken(16);
  const deleteToken = randomToken(32);
  await env.FILES.put(fileKey(id), body, {
    httpMetadata: { contentType: type },
    customMetadata: { expires: String(expires), deleteHash: hex(await sha256(deleteToken)), encrypted: encrypted ? '1' : '0', type },
  });
  const link = encrypted ? `${url.origin}/f/${id}` : `${url.origin}/r/${id}.${PUBLIC_TYPES[type].ext}`;
  return json({ id, deleteToken, expires: new Date(expires).toISOString(), size: body.byteLength, url: link }, 201);
};

const getFile: Handler = async (req, env, [id]) => {
  const obj = await load(env, id, req.method === 'HEAD');
  return obj ? serve(obj, 'no-store') : notFound();
};

const getPublic: Handler = async (req, env, [id, ext]) => {
  const obj = await load(env, id, req.method === 'HEAD');
  const meta = obj?.customMetadata;
  if (!obj || meta?.encrypted !== '0' || PUBLIC_TYPES[meta.type ?? '']?.ext !== ext) {
    if (obj) await bodyOf(obj)?.cancel();
    return notFound();
  }
  return serve(obj, 'public, max-age=300');
};

const deleteFile: Handler = async (req, env, [id]) => {
  const given = bearer(req);
  if (!given) throw unauthorized();
  const obj = await load(env, id, true);
  if (!obj) return notFound();
  // Compare fixed-length digests in constant time; only the hash of the token is stored.
  const stored = unhex(obj.customMetadata?.deleteHash ?? '');
  if (!stored || !crypto.subtle.timingSafeEqual(await sha256(given), stored)) throw unauthorized();
  await env.FILES.delete(fileKey(id));
  return json({ ok: true });
};

// --- admin: take reported files down, and keep the ones the law says to keep ---

const admin =
  (handler: Handler): Handler =>
  async (req, env, params, url) => {
    if (!env.ADMIN_TOKEN) return json({ error: 'not found' }, 404);
    const given = bearer(req);
    if (!given || !crypto.subtle.timingSafeEqual(await sha256(given), await sha256(env.ADMIN_TOKEN))) throw unauthorized();
    return handler(req, env, params, url);
  };

async function readJson(req: Request) {
  const body: unknown = await req.json().catch(() => null);
  check(body && typeof body === 'object' && !Array.isArray(body), 'body must be a JSON object');
  return body as Record<string, unknown>;
}

async function decrypt(data: Uint8Array, keyText: string) {
  try {
    const raw = Uint8Array.from(atob(keyText.replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '')), (c) => c.charCodeAt(0));
    const key = await crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['decrypt']);
    return new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: data.subarray(0, 12) }, key, data.subarray(12)));
  } catch {
    return null;
  }
}

async function readHold(env: Env, id: string) {
  const obj = await env.HOLDS.get(holdKey(id, 'record.json'));
  if (!obj) throw new HttpError(404, 'no hold for that file');
  return obj.json<Hold>();
}

const saveHold = (env: Env, hold: Hold) =>
  env.HOLDS.put(holdKey(hold.id, 'record.json'), JSON.stringify(hold, null, 2), { httpMetadata: { contentType: JSON_TYPE } });

const extend = (hold: Hold, until: Date) => {
  hold.keepUntil = new Date(Math.max(Date.parse(hold.keepUntil), until.getTime())).toISOString();
};

// Copies the file into HOLDS, then deletes it from FILES, so the link dies and the uploader's delete token
// can't reach the copy. Expired files that cleanup hasn't removed yet are kept too: the bytes still exist.
const createHold: Handler = async (req, env) => {
  const { id, reason, key } = await readJson(req);
  check(typeof id === 'string' && ID_RE.test(id), 'id must be a fortefile file id');
  check(typeof reason === 'string' && reason.trim(), 'reason required');
  check(key === undefined || typeof key === 'string', 'key must be a string');
  if (await env.HOLDS.head(holdKey(id, 'record.json'))) throw new HttpError(409, 'that file is already held');
  const obj = await env.FILES.get(fileKey(id));
  if (!obj) throw new HttpError(404, 'not found: the file has expired or was deleted');
  const bytes = new Uint8Array(await obj.arrayBuffer());
  const { deleteHash: _, ...meta } = obj.customMetadata ?? {};
  const encrypted = meta.encrypted !== '0';
  const plain = encrypted && key ? await decrypt(bytes, key) : null;
  const heldAt = new Date();
  const hold: Hold = {
    id,
    reason: reason.trim(),
    heldAt: heldAt.toISOString(),
    size: bytes.byteLength,
    sha256: hex(await crypto.subtle.digest('SHA-256', bytes)),
    uploaded: obj.uploaded.toISOString(),
    expires: new Date(Number(meta.expires) || obj.uploaded.getTime()).toISOString(),
    encrypted,
    type: meta.type ?? 'application/octet-stream',
    ...(plain && key ? { key: key.replace(/=+$/, ''), contentType: sniff(plain) } : {}),
    keepUntil: daysAfter(heldAt, HOLD_DAYS).toISOString(),
    notes: key && !plain ? [`${heldAt.toISOString()} the key given with the report didn't decrypt the file, so it wasn't kept`] : [],
  };
  await env.HOLDS.put(holdKey(id, 'file'), bytes, { customMetadata: meta, sha256: hold.sha256 });
  await saveHold(env, hold);
  await env.FILES.delete(fileKey(id));
  return json(hold, 201);
};

const listHolds: Handler = async (_req, env) => {
  const holds: Hold[] = [];
  let cursor: string | undefined;
  do {
    const list = await env.HOLDS.list({ prefix: 'holds/', cursor });
    for (const o of list.objects.filter((o) => o.key.endsWith('/record.json'))) {
      const rec = await env.HOLDS.get(o.key);
      if (rec) holds.push(await rec.json<Hold>());
    }
    cursor = list.truncated ? list.cursor : undefined;
  } while (cursor);
  return json({ holds: holds.sort((a, b) => a.heldAt.localeCompare(b.heldAt)) });
};

const getHold: Handler = async (_req, env, [id]) => json(await readHold(env, id));

// Records the CyberTipline report (the year runs from it), extends the hold, or adds a note. Never shortens it.
const updateHold: Handler = async (req, env, [id]) => {
  const hold = await readHold(env, id);
  const { report, reportedAt, keepUntil, note } = await readJson(req);
  if (report !== undefined) {
    check(typeof report === 'string' && report.trim(), 'report must be the CyberTipline report number');
    const at = reportedAt === undefined ? new Date() : new Date(String(reportedAt));
    check(!isNaN(at.getTime()), 'reportedAt must be a date');
    hold.report = report.trim();
    hold.reportedAt = at.toISOString();
    extend(hold, daysAfter(at, HOLD_DAYS));
  }
  if (keepUntil !== undefined) {
    const until = new Date(String(keepUntil));
    check(!isNaN(until.getTime()), 'keepUntil must be a date');
    extend(hold, until);
  }
  if (note !== undefined) {
    check(typeof note === 'string' && note.trim(), 'note must be text');
    hold.notes.push(`${new Date().toISOString()} ${note.trim()}`);
  }
  await saveHold(env, hold);
  return json(hold);
};

const getHoldFile: Handler = async (_req, env, [id]) => {
  const obj = await env.HOLDS.get(holdKey(id, 'file'));
  if (!obj) throw new HttpError(404, 'no hold for that file');
  return new Response(obj.body, {
    headers: {
      'Content-Type': 'application/octet-stream',
      'Content-Length': String(obj.size),
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      'Content-Security-Policy': "default-src 'none'; sandbox",
    },
  });
};

const releaseHold: Handler = async (_req, env, [id], url) => {
  const hold = await readHold(env, id);
  const early = Date.parse(hold.keepUntil) > Date.now();
  check(!early || url.searchParams.get('force') === '1', `held until ${hold.keepUntil}; add force=1 to release it early`, 409);
  await env.HOLDS.delete([holdKey(id, 'file'), holdKey(id, 'record.json')]);
  return json({ ok: true });
};

// Takes a link down without keeping anything (copyright notices, most abuse reports).
const removeFile: Handler = async (_req, env, [id]) => {
  if (!(await env.FILES.head(fileKey(id)))) return notFound();
  await env.FILES.delete(fileKey(id));
  return json({ ok: true });
};

const viewer: Handler = async (req, env, _params, url) =>
  page(req, env, url, '/viewer', { 'X-Robots-Tag': 'noindex, nofollow' });

// Test-only (TEST_HOOKS=1 on localhost): reports whether the object is still in R2, without expiring it.
const inspect: Handler = async (_req, env, [id]) => {
  const obj = await env.FILES.head(fileKey(id));
  return json({ exists: !!obj, expires: obj ? expiresIso(obj) : null, meta: obj?.customMetadata ?? null });
};

const routes: [RegExp, Record<string, Handler>][] = [
  [/^\/api\/files$/, { POST: upload }],
  [/^\/api\/files\/([^/]+)$/, { GET: getFile, DELETE: deleteFile }],
  [/^\/r\/([^/.]+)\.(png|jpg)$/, { GET: getPublic }],
  [/^\/f\/([A-Za-z0-9_-]{22})$/, { GET: viewer }],
  [/^\/api\/admin\/files\/([A-Za-z0-9_-]{22})$/, { DELETE: admin(removeFile) }],
  [/^\/api\/admin\/holds$/, { GET: admin(listHolds), POST: admin(createHold) }],
  [/^\/api\/admin\/holds\/([A-Za-z0-9_-]{22})$/, { GET: admin(getHold), PATCH: admin(updateHold), DELETE: admin(releaseHold) }],
  [/^\/api\/admin\/holds\/([A-Za-z0-9_-]{22})\/file$/, { GET: admin(getHoldFile) }],
];
const testRoutes: [RegExp, Record<string, Handler>][] = [[/^\/__test\/files\/([^/]+)$/, { GET: inspect }]];

// Deletes every file whose expiry has passed (and, as a backstop, anything older than the longest lifetime).
async function cleanup(env: Env, now = Date.now()) {
  let cursor: string | undefined;
  let deleted = 0;
  do {
    const list = await env.FILES.list({ prefix: 'files/', cursor, include: ['customMetadata'] });
    const expired = list.objects
      .filter((o) => (o.customMetadata ? isExpired(o, now) : false) || o.uploaded.getTime() + DEFAULT_LIFETIME * 1000 < now)
      .map((o) => o.key);
    for (let i = 0; i < expired.length; i += 1000) await env.FILES.delete(expired.slice(i, i + 1000));
    deleted += expired.length;
    cursor = list.truncated ? list.cursor : undefined;
  } while (cursor);
  return deleted;
}

export default {
  async fetch(req, env): Promise<Response> {
    const url = new URL(req.url);
    try {
      if (url.hostname === 'www.fortefile.com' && !url.pathname.startsWith('/api/') && ['GET', 'HEAD'].includes(req.method)) {
        return Response.redirect(`https://fortefile.com${url.pathname}${url.search}`, 301);
      }
      for (const [pattern, methods] of testHooks(env, url) ? [...testRoutes, ...routes] : routes) {
        const match = pattern.exec(url.pathname);
        if (!match) continue;
        const handler = methods[req.method] ?? (req.method === 'HEAD' ? methods.GET : undefined);
        if (!handler) {
          const allow = Object.keys(methods).concat(methods.GET && !methods.HEAD ? ['HEAD'] : []);
          return json({ error: 'method not allowed' }, 405, { Allow: allow.join(', ') });
        }
        return await handler(req, env, match.slice(1), url);
      }
      if (url.pathname === '/api' || url.pathname.startsWith('/api/')) return json({ error: 'not found' }, 404);
      return await page(req, env, url);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.message }, e.status, e.headers);
      console.error(e);
      return json({ error: 'internal error' }, 500);
    }
  },

  async scheduled(_controller, env): Promise<void> {
    const deleted = await cleanup(env);
    console.log(`cleanup: deleted ${deleted} expired file(s)`);
  },
} satisfies ExportedHandler<Env>;
