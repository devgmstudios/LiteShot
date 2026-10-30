// fortefile admin: take reported files down, and keep the ones the law says to keep. `npm run admin` for help.
// Talks to the worker's /api/admin routes; the kept files never leave Cloudflare unless you `export` one.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const BASE = (process.env.FORTEFILE_URL ?? 'https://fortefile.com').replace(/\/+$/, '');
const TOKEN_FILE = join(process.env.APPDATA ?? join(process.env.HOME ?? '.', '.config'), 'Devgm', 'fortefile', 'admin-token.txt');
const EXT = { 'image/png': 'png', 'image/jpeg': 'jpg' };
const USAGE = `fortefile admin

  npm run admin -- hold "<link>" --reason "<why>"
      Kill the link and keep the file in the legal-hold store for a year. Paste the whole link
      from the report: the key after the # is kept too (only if it opens the file).
  npm run admin -- remove "<link>"         Kill the link and keep nothing (copyright, most abuse).
  npm run admin -- list                    Every hold.
  npm run admin -- show <id>
  npm run admin -- report <id> <number> [--date YYYY-MM-DD]
      Record the CyberTipline report number. The year of keeping starts from the report.
  npm run admin -- extend <id> <YYYY-MM-DD>   Keep it longer, e.g. for a preservation request.
  npm run admin -- note <id> "<text>"
  npm run admin -- export <id> [--out <folder>]
      Save the kept file, decrypted when the key was kept, to file a report or answer legal process.
  npm run admin -- release <id> [--force]
      Delete a hold for good. Refused before its keep-until date unless --force.

Token: FORTEFILE_ADMIN_TOKEN, or ${TOKEN_FILE}
Server: FORTEFILE_URL (now ${BASE})`;

function die(message) {
  console.error(message);
  process.exit(1);
}

function token() {
  const t = process.env.FORTEFILE_ADMIN_TOKEN ?? (existsSync(TOKEN_FILE) ? readFileSync(TOKEN_FILE, 'utf8') : '');
  return t.trim() || die(`No admin token. Put it in ${TOKEN_FILE} or set FORTEFILE_ADMIN_TOKEN.`);
}

async function api(method, path, body) {
  const res = await fetch(BASE + path, {
    method,
    headers: { Authorization: `Bearer ${token()}`, ...(body && { 'Content-Type': 'application/json' }) },
    body: body && JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text();
    let message = text.slice(0, 300);
    try {
      message = JSON.parse(text).error;
    } catch {}
    die(`${res.status}: ${message}`);
  }
  return path.endsWith('/file') ? Buffer.from(await res.arrayBuffer()) : res.json();
}

// A whole link (…/f/{id}#{key}, …/r/{id}.png), an API address or a bare id.
function parseLink(text = '') {
  const hash = text.indexOf('#');
  const key = hash < 0 ? undefined : text.slice(hash + 1).trim() || undefined;
  const id = /(?:^|\/)([A-Za-z0-9_-]{22})(?:\.(?:png|jpg))?\/?(?:\?.*)?$/.exec((hash < 0 ? text : text.slice(0, hash)).trim())?.[1];
  return id ? { id, key } : die(`Not a fortefile link or file id: ${text || '(missing)'}\n\n${USAGE}`);
}

async function decrypt(stored, keyText) {
  const key = await crypto.subtle.importKey('raw', Buffer.from(keyText, 'base64url'), 'AES-GCM', false, ['decrypt']);
  return Buffer.from(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: stored.subarray(0, 12) }, key, stored.subarray(12)));
}

const day = (iso) => iso.slice(0, 10);
const when = (iso) => `${iso.slice(0, 16).replace('T', ' ')} UTC`;
const size = (n) => (n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`);

function print(h) {
  const what = !h.encrypted
    ? `${h.type}, not encrypted`
    : h.key
      ? `encrypted, key kept (${h.contentType === 'unknown' ? 'not an image' : h.contentType})`
      : 'encrypted, no key: we hold only unreadable ciphertext';
  console.log(`${h.id}  held ${when(h.heldAt)}, keep until ${day(h.keepUntil)}`);
  console.log(`  reason    ${h.reason}`);
  console.log(`  file      ${what}, ${size(h.size)}`);
  console.log(`  sha256    ${h.sha256}`);
  console.log(`  uploaded  ${when(h.uploaded)}, would have expired ${when(h.expires)}`);
  console.log(`  report    ${h.report ? `CyberTipline ${h.report}, filed ${day(h.reportedAt)}` : 'none recorded'}`);
  for (const n of h.notes) console.log(`  note      ${n}`);
}

const [command, ...rest] = process.argv.slice(2);
const flags = {};
const args = [];
for (let i = 0; i < rest.length; i++) {
  const flag = /^--([a-z]+)$/.exec(rest[i])?.[1];
  if (!flag) args.push(rest[i]);
  else if (flag === 'force') flags.force = true;
  else flags[flag] = rest[++i] ?? die(`--${flag} needs a value`);
}
const holdPath = (link) => `/api/admin/holds/${parseLink(link).id}`;

switch (command) {
  case 'hold': {
    const { id, key } = parseLink(args[0]);
    if (!flags.reason) die('Say why with --reason, for example: --reason "Child safety report from abuse@, 2026-10-28"');
    const h = await api('POST', '/api/admin/holds', { id, reason: flags.reason, key });
    console.log(`Held: the link is dead and the file is kept until ${day(h.keepUntil)}.\n`);
    print(h);
    if (h.encrypted && !h.key)
      console.log(`\n${key ? "The key in that link didn't open the file" : 'No key was given'}. Ask the reporter for the whole link, exactly as they received it.`);
    console.log(`\nIf it's child sexual abuse material, report it at https://report.cybertip.org, then run:\n  npm run admin -- report ${id} <report number>`);
    break;
  }
  case 'remove': {
    await api('DELETE', `/api/admin/files/${parseLink(args[0]).id}`);
    console.log('Removed: the link is dead and nothing was kept.');
    break;
  }
  case 'list': {
    const { holds } = await api('GET', '/api/admin/holds');
    if (!holds.length) console.log('No holds.');
    holds.forEach((h, i) => {
      if (i) console.log();
      print(h);
    });
    break;
  }
  case 'show':
    print(await api('GET', holdPath(args[0])));
    break;
  case 'report': {
    if (!args[1]) die('Usage: npm run admin -- report <id> <CyberTipline report number> [--date YYYY-MM-DD]');
    const h = await api('PATCH', holdPath(args[0]), { report: args[1], reportedAt: flags.date });
    console.log(`Recorded report ${h.report}. Kept until ${day(h.keepUntil)}.`);
    break;
  }
  case 'extend': {
    if (!args[1]) die('Usage: npm run admin -- extend <id> <YYYY-MM-DD>');
    const h = await api('PATCH', holdPath(args[0]), { keepUntil: args[1] });
    console.log(`Kept until ${day(h.keepUntil)}.`);
    break;
  }
  case 'note': {
    if (!args[1]) die('Usage: npm run admin -- note <id> "<text>"');
    await api('PATCH', holdPath(args[0]), { note: args.slice(1).join(' ') });
    console.log('Noted.');
    break;
  }
  case 'export': {
    const h = await api('GET', holdPath(args[0]));
    const stored = await api('GET', `${holdPath(args[0])}/file`);
    if (createHash('sha256').update(stored).digest('hex') !== h.sha256) die('The kept file no longer matches its recorded SHA-256. Nothing was saved.');
    const [bytes, name] = !h.encrypted
      ? [stored, `fortefile-${h.id}.${EXT[h.type] ?? 'bin'}`]
      : h.key
        ? [await decrypt(stored, h.key), `fortefile-${h.id}.${EXT[h.contentType] ?? 'bin'}`]
        : [stored, `fortefile-${h.id}.encrypted.bin`];
    const file = resolve(flags.out ?? '.', name);
    writeFileSync(file, bytes);
    console.log(`Saved ${file} (SHA-256 checked).`);
    console.log('This is evidence. Use it only to file the report or answer legal process, keep it out of synced or shared folders, and delete it when you are done.');
    break;
  }
  case 'release': {
    const h = await api('GET', holdPath(args[0]));
    if (!flags.force && Date.parse(h.keepUntil) > Date.now()) die(`Held until ${day(h.keepUntil)}. Add --force to release it early.`);
    await api('DELETE', `${holdPath(args[0])}${flags.force ? '?force=1' : ''}`);
    console.log('Released: the kept file and its record are deleted.');
    break;
  }
  default:
    console.log(USAGE);
    if (command && !['help', '--help', '-h'].includes(command)) process.exit(1);
}
