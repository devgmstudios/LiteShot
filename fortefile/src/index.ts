// fortefile: encrypted, self-deleting file links.
//   files/{id}  uploaded bytes; customMetadata { expires (ms), deleteHash (hex sha256 of the delete token), encrypted, type }
// Encrypted files are AES-256-GCM output (IV || ciphertext || tag) made by the uploader. The key only ever
// travels in the link's #fragment, so this worker never sees it. Public (unencrypted) files are PNG/JPEG
// screenshots that live at most an hour, so a reverse image search can fetch them.

interface Env {
  FILES: R2Bucket;
  ASSETS: Fetcher;
  UPLOAD_LIMITER?: RateLimit;
  TEST_HOOKS?: string; // "1" only in `npm run dev`: allows X-Test-Expire-In and /__test/files/{id} on localhost
}

type Handler = (req: Request, env: Env, params: string[], url: URL) => Promise<Response>;

const JSON_TYPE = 'application/json; charset=utf-8';
const MAX_BYTES = 25 * 1024 * 1024;
const LIFETIMES = [3600, 43200, 86400];
const DEFAULT_LIFETIME = 86400;
const PUBLIC_MAX_LIFETIME = 3600;
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
  const unauthorized = new HttpError(401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
  const given = /^Bearer\s+(\S+)\s*$/i.exec(req.headers.get('Authorization') ?? '')?.[1];
  if (!given) throw unauthorized;
  const obj = await load(env, id, true);
  if (!obj) return notFound();
  // Compare fixed-length digests in constant time; only the hash of the token is stored.
  const stored = unhex(obj.customMetadata?.deleteHash ?? '');
  if (!stored || !crypto.subtle.timingSafeEqual(await sha256(given), stored)) throw unauthorized;
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
