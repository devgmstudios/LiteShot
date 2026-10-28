// LiteShot update server and website: serves signed channel manifests and release files from R2, and the site
// in public/ as static assets.
//   releases/{version}/{file}   release binaries
//   channels/{channel}.json     signed envelope { payload, signature }, stored and served verbatim
// The worker never verifies signatures; the app does.

interface Env {
  RELEASES: R2Bucket;
  ASSETS: Fetcher;
  PUBLISH_TOKEN?: string;
}

type Handler = (req: Request, env: Env, params: string[], url: URL) => Promise<Response>;

const VERSION_RE = /^\d+\.\d+\.\d+$/;
const FILE_RE = /^[A-Za-z0-9._-]{1,100}$/;
const CHANNEL_RE = /^[a-z0-9-]{1,20}$/;
const SHA256_RE = /^[0-9a-f]{64}$/;
const JSON_TYPE = 'application/json; charset=utf-8';
const MAX_MANIFEST_BYTES = 64 * 1024;

class HttpError extends Error {
  constructor(readonly status: number, message: string, readonly headers: Record<string, string> = {}) {
    super(message);
  }
}

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': JSON_TYPE, ...headers } });

function check(ok: unknown, message: string, status = 400): asserts ok {
  if (!ok) throw new HttpError(status, message);
}

const isVersion = (v: unknown): v is string => typeof v === 'string' && VERSION_RE.test(v);
const isFile = (f: unknown): f is string => typeof f === 'string' && FILE_RE.test(f) && !f.startsWith('.');
const isObject = (o: unknown): o is Record<string, unknown> => typeof o === 'object' && o !== null && !Array.isArray(o);
const hex = (buf: ArrayBuffer) => [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
const compareVersions = (a: string, b: string) => {
  const [x, y] = [a, b].map((v) => v.split('.').map(Number));
  return x[0] - y[0] || x[1] - y[1] || x[2] - y[2];
};

async function listAll(env: Env, prefix: string, delimiter?: string) {
  const keys: string[] = [];
  const prefixes: string[] = [];
  let cursor: string | undefined;
  do {
    const page = await env.RELEASES.list({ prefix, delimiter, cursor });
    keys.push(...page.objects.map((o) => o.key));
    prefixes.push(...page.delimitedPrefixes);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return { keys, prefixes };
}

async function authorize(req: Request, env: Env) {
  if (!env.PUBLISH_TOKEN) throw new HttpError(503, 'publishing is not configured');
  const given = /^Bearer\s+(\S+)\s*$/i.exec(req.headers.get('Authorization') ?? '')?.[1] ?? '';
  // Compare fixed-length digests so neither content nor length leaks through timing.
  const enc = new TextEncoder();
  const [a, b] = await Promise.all([given, env.PUBLISH_TOKEN].map((s) => crypto.subtle.digest('SHA-256', enc.encode(s))));
  if (!given || !crypto.subtle.timingSafeEqual(a, b)) throw new HttpError(401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
}

const authed = (handler: Handler): Handler => async (req, env, params, url) => {
  await authorize(req, env);
  return handler(req, env, params, url);
};

const root: Handler = async () => json({ name: 'LiteShot update server', ok: true });

/** The site's download buttons: the newest stable file of a kind, or back to the download section if none yet. */
const downloadLatest = (kind: string): Handler => async (_req, env, _params, url) => {
  const obj = await env.RELEASES.get('channels/stable.json');
  try {
    const info = JSON.parse(((await obj?.json()) as { payload: string }).payload);
    const file = (info.files as { kind: string; name: string }[]).find((f) => f.kind === kind);
    if (file && isVersion(info.version) && isFile(file.name))
      return Response.redirect(new URL(`/v1/download/${info.version}/${file.name}`, url).toString(), 302);
  } catch {
    // No release yet, or an unreadable manifest: fall through.
  }
  return Response.redirect(new URL('/#download', url).toString(), 302);
};

const latest: Handler = async (_req, env, _params, url) => {
  const channel = url.searchParams.get('channel') ?? 'stable';
  check(CHANNEL_RE.test(channel), 'invalid channel');
  const obj = await env.RELEASES.get(`channels/${channel}.json`);
  if (!obj) return json({ error: 'no release' }, 404);
  return new Response(obj.body, { headers: { 'Content-Type': JSON_TYPE, 'Cache-Control': 'public, max-age=60' } });
};

const download: Handler = async (req, env, [version, file]) => {
  check(isVersion(version), 'invalid version');
  check(isFile(file), 'invalid file name');
  const key = `releases/${version}/${file}`;
  const head = req.method === 'HEAD';
  const obj = head ? await env.RELEASES.head(key) : await env.RELEASES.get(key);
  if (!obj) return json({ error: 'not found' }, 404);
  return new Response(head ? null : (obj as R2ObjectBody).body, {
    headers: {
      'Content-Length': String(obj.size),
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${file}"`,
      ETag: obj.httpEtag,
      'Cache-Control': 'public, max-age=31536000, immutable',
    },
  });
};

const uploadFile: Handler = async (req, env, [version, file]) => {
  check(isVersion(version), 'invalid version');
  check(isFile(file), 'invalid file name');
  const length = req.headers.get('Content-Length');
  check(length !== null, 'Content-Length required', 411);
  check(/^\d+$/.test(length), 'invalid Content-Length');
  // Optional: lets R2 verify the upload; the checksum is later matched against the manifest.
  const sha256 = req.headers.get('X-Content-SHA256')?.toLowerCase();
  check(sha256 === undefined || SHA256_RE.test(sha256), 'invalid X-Content-SHA256');
  const key = `releases/${version}/${file}`;
  let obj: R2Object;
  try {
    obj = await env.RELEASES.put(key, req.body ?? new Uint8Array(0), {
      httpMetadata: { contentType: 'application/octet-stream' },
      sha256,
    });
  } catch (e) {
    if (sha256 && /sha-?256|checksum|digest/i.test(String(e))) throw new HttpError(400, 'upload does not match X-Content-SHA256');
    throw e;
  }
  return json({ key, size: obj.size });
};

const putChannel: Handler = async (req, env, [channel]) => {
  check(CHANNEL_RE.test(channel), 'invalid channel');
  const body = await req.arrayBuffer();
  check(body.byteLength <= MAX_MANIFEST_BYTES, 'manifest too large', 413);
  let text: string, envelope: unknown, payload: unknown;
  try {
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(body);
    envelope = JSON.parse(text);
  } catch {
    throw new HttpError(400, 'body is not valid JSON');
  }
  check(
    isObject(envelope) && typeof envelope.payload === 'string' && typeof envelope.signature === 'string' && envelope.signature,
    'body must be { "payload": string, "signature": string }',
  );
  try {
    payload = JSON.parse(envelope.payload);
  } catch {
    throw new HttpError(400, 'payload is not valid JSON');
  }
  check(isObject(payload), 'payload must be a JSON object');
  check(payload.product === 'LiteShot', 'payload.product must be "LiteShot"');
  check(isVersion(payload.version), 'invalid payload.version');
  check(payload.channel === channel, `payload.channel must be "${channel}"`);
  check(Array.isArray(payload.files) && payload.files.length > 0, 'payload.files must be a non-empty array');
  for (const f of payload.files as unknown[]) {
    check(isObject(f) && isFile(f.name), 'invalid file name in payload.files');
    check(typeof f.kind === 'string' && f.kind, `missing kind for ${f.name}`);
    check(Number.isSafeInteger(f.size) && (f.size as number) >= 0, `invalid size for ${f.name}`);
    check(typeof f.sha256 === 'string' && SHA256_RE.test(f.sha256), `invalid sha256 for ${f.name}`);
    const obj = await env.RELEASES.head(`releases/${payload.version}/${f.name}`);
    check(obj, `${f.name} has not been uploaded for ${payload.version}`);
    check(obj.size === f.size, `${f.name} is ${obj.size} bytes on the server but the manifest says ${f.size}`);
    const stored = obj.checksums.sha256;
    check(!stored || hex(stored) === f.sha256, `${f.name} on the server does not match the manifest sha256`);
  }
  await env.RELEASES.put(`channels/${channel}.json`, text, { httpMetadata: { contentType: JSON_TYPE } });
  return json({ ok: true, version: payload.version });
};

const listReleases: Handler = async (_req, env) => {
  const { prefixes } = await listAll(env, 'releases/', '/');
  const versions = prefixes.map((p) => p.slice('releases/'.length, -1)).filter(isVersion).sort(compareVersions);
  return json({ versions });
};

const deleteRelease: Handler = async (_req, env, [version]) => {
  check(isVersion(version), 'invalid version');
  const live: string[] = [];
  for (const key of (await listAll(env, 'channels/')).keys) {
    const obj = await env.RELEASES.get(key);
    try {
      const { payload } = (await obj?.json()) as { payload: string };
      if (JSON.parse(payload).version === version) live.push(key.slice('channels/'.length, -'.json'.length));
    } catch {
      // Manifests are validated on upload; skip anything unreadable.
    }
  }
  check(live.length === 0, `version ${version} is live on channel(s): ${live.join(', ')}`, 409);
  const { keys } = await listAll(env, `releases/${version}/`);
  if (keys.length === 0) return json({ error: 'not found' }, 404);
  for (let i = 0; i < keys.length; i += 1000) await env.RELEASES.delete(keys.slice(i, i + 1000));
  return json({ ok: true, version, deleted: keys.length });
};

const routes: [RegExp, Record<string, Handler>][] = [
  [/^\/v1$/, { GET: root }],
  [/^\/download$/, { GET: downloadLatest('msi') }],
  [/^\/download\/portable$/, { GET: downloadLatest('portable') }],
  [/^\/v1\/latest$/, { GET: latest }],
  [/^\/v1\/download\/([^/]+)\/([^/]+)$/, { GET: download, HEAD: download }],
  [/^\/v1\/releases$/, { GET: authed(listReleases) }],
  [/^\/v1\/releases\/([^/]+)$/, { DELETE: authed(deleteRelease) }],
  [/^\/v1\/releases\/([^/]+)\/([^/]+)$/, { PUT: authed(uploadFile) }],
  [/^\/v1\/channels\/([^/]+)$/, { PUT: authed(putChannel) }],
];

export default {
  async fetch(req, env): Promise<Response> {
    const url = new URL(req.url);
    try {
      for (const [pattern, methods] of routes) {
        const match = pattern.exec(url.pathname);
        if (!match) continue;
        const handler = methods[req.method] ?? (req.method === 'HEAD' ? methods.GET : undefined);
        if (!handler) {
          const allow = Object.keys(methods).concat(methods.GET && !methods.HEAD ? ['HEAD'] : []);
          return json({ error: 'method not allowed' }, 405, { Allow: allow.join(', ') });
        }
        return await handler(req, env, match.slice(1), url);
      }
      if (url.pathname.startsWith('/v1/')) return json({ error: 'not found' }, 404);
      const page = await env.ASSETS.fetch(new Request(new URL('/404.html', url)));
      return new Response(page.body, { status: 404, headers: page.headers });
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.message }, e.status, e.headers);
      console.error(e);
      return json({ error: 'internal error' }, 500);
    }
  },
} satisfies ExportedHandler<Env>;
