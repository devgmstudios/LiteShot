// fortefile viewer: fetches /api/files/{id}, decrypts it here with the key from the #fragment, shows it.
// Stored bytes are IV (12) || AES-256-GCM ciphertext || tag (16). The key never leaves this page:
// it is not sent anywhere, not stored, and not removed from the address bar (so reloading still works).

const $ = (id) => document.getElementById(id);
const ui = {
  state: $('state'), spinner: $('state-spinner'), badge: $('state-badge'), icon: $('state-icon'),
  title: $('state-title'), text: $('state-text'), progress: $('state-progress'),
  actions: $('state-actions'), stateDownload: $('state-download'), retry: $('state-retry'),
  content: $('content'), toolbar: $('toolbar'), expiry: $('expiry'), expiryText: $('expiry-text'),
  download: $('download'), copy: $('copy'), copyIcon: $('copy-icon'), copyLabel: $('copy-label'),
};

const STATES = {
  loading: { title: 'Opening encrypted file…', text: 'Your browser is downloading the file and unlocking it with the key from the link.' },
  badlink: { icon: 'i-link', tone: 'slate', title: "This link isn't complete", text: 'Part of the address is missing or mistyped. Ask the sender to copy the whole link again.' },
  nokey: { icon: 'i-key', tone: 'violet', title: 'This link is missing its key', text: 'The key is the part after the # at the end of the link. Without it the file can’t be unlocked, by you or by us. Ask the sender for the whole link.' },
  badkey: { icon: 'i-lock', tone: 'orange', title: "Couldn't unlock this file", text: "The key in this link doesn't match the file, or the file is damaged. Check that the whole link was copied, including everything after the #." },
  gone: { icon: 'i-clock', tone: 'slate', title: 'This file is gone', text: 'It expired or was deleted by the person who shared it. Files on fortefile delete themselves within 24 hours.' },
  network: { icon: 'i-cloud-off', tone: 'slate', title: "Couldn't reach fortefile", text: 'Check your internet connection and try again.', retry: true },
  error: { icon: 'i-alert', tone: 'orange', title: 'Something went wrong', text: "fortefile couldn't send this file right now. Try again in a moment.", retry: true },
  unsupported: { icon: 'i-file', tone: 'blue', title: 'Ready to download', text: "This file was unlocked, but it can't be shown in the browser.", download: true },
  nocrypto: { icon: 'i-alert', tone: 'orange', title: "This browser can't unlock files", text: 'Open the link in a current version of Edge, Chrome, Firefox or Safari.' },
};

function show(name, extra = {}) {
  const s = { ...STATES[name], ...extra };
  ui.state.dataset.state = name;
  ui.state.hidden = false;
  ui.content.hidden = true;
  ui.spinner.hidden = name !== 'loading';
  ui.badge.hidden = name === 'loading';
  ui.badge.className = `badge ${s.tone ?? 'blue'}`;
  if (s.icon) ui.icon.setAttribute('href', `#${s.icon}`);
  ui.title.textContent = s.title;
  ui.text.textContent = s.text;
  ui.progress.hidden = true;
  ui.retry.hidden = !s.retry;
  ui.stateDownload.hidden = !s.download;
  ui.actions.hidden = name === 'loading';
  document.title = `${name === 'loading' ? 'Opening…' : s.title} · fortefile`;
}

const b64urlDecode = (s) => {
  if (!/^[A-Za-z0-9_-]+$/.test(s) || s.length % 4 === 1) return null;
  const bin = atob(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
};

const formatSize = (n) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`);

// Content type from the first bytes; the server only ever sees ciphertext, so it can't tell us.
function sniff(b) {
  const at = (offset, sig) => sig.every((v, i) => b[offset + i] === v);
  const ascii = (from, to) => String.fromCharCode(...b.subarray(from, to));
  if (at(0, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return { mime: 'image/png', ext: 'png', kind: 'image' };
  if (at(0, [0xff, 0xd8, 0xff])) return { mime: 'image/jpeg', ext: 'jpg', kind: 'image' };
  if (/^GIF8[79]a$/.test(ascii(0, 6))) return { mime: 'image/gif', ext: 'gif', kind: 'image' };
  if (ascii(0, 4) === 'RIFF' && ascii(8, 12) === 'WEBP') return { mime: 'image/webp', ext: 'webp', kind: 'image' };
  if (ascii(4, 8) === 'ftyp') {
    const brand = ascii(8, 12);
    if (/^(avif|avis|heic|heix|hevc|mif1|msf1)$/.test(brand)) return null;
    return brand === 'qt  ' ? { mime: 'video/quicktime', ext: 'mov', kind: 'video' } : { mime: 'video/mp4', ext: 'mp4', kind: 'video' };
  }
  return null;
}

function countdown(expires) {
  const tick = () => {
    const left = expires - Date.now();
    ui.expiry.classList.toggle('over', left <= 0);
    if (left <= 0) {
      ui.expiryText.textContent = 'Link expired';
      return clearInterval(timer);
    }
    const mins = Math.floor(left / 60000);
    const h = Math.floor(mins / 60);
    const m = mins % 60;
    ui.expiryText.textContent =
      mins < 1 ? 'Deletes in under a minute' : h ? `Deletes in ${h} h${m ? ` ${m} min` : ''}` : `Deletes in ${m} min`;
  };
  const timer = setInterval(tick, 15000);
  tick();
}

async function fetchBytes(id) {
  const res = await fetch(`/api/files/${id}`, { cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer' });
  if (!res.ok) return { status: res.status };
  const total = Number(res.headers.get('Content-Length')) || 0;
  const expires = Date.parse(res.headers.get('X-Expires') ?? '');
  if (!res.body || total < 1024 * 1024) return { status: 200, expires, bytes: new Uint8Array(await res.arrayBuffer()) };
  // Big files: show progress while downloading.
  const bytes = new Uint8Array(total);
  const reader = res.body.getReader();
  let got = 0;
  ui.progress.hidden = false;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    if (got + value.length > total) throw new Error('more data than announced');
    bytes.set(value, got);
    got += value.length;
    ui.progress.textContent = `${formatSize(got)} of ${formatSize(total)}`;
  }
  return { status: 200, expires, bytes: bytes.subarray(0, got) };
}

function setupCopy(blob, type, img) {
  const canCopy = window.ClipboardItem && navigator.clipboard?.write && (ClipboardItem.supports?.('image/png') ?? true);
  if (!canCopy || type.kind !== 'image') return;
  // The clipboard takes PNG everywhere; other image types are redrawn as PNG first.
  const asPng = () =>
    type.mime === 'image/png'
      ? Promise.resolve(blob)
      : new Promise((resolve, reject) => {
          const canvas = document.createElement('canvas');
          canvas.width = img.naturalWidth;
          canvas.height = img.naturalHeight;
          canvas.getContext('2d').drawImage(img, 0, 0);
          canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('encode failed'))), 'image/png');
        });
  let reset;
  const status = (label, icon) => {
    ui.copyLabel.textContent = label;
    ui.copy.setAttribute('aria-label', label);
    ui.copyIcon.setAttribute('href', icon);
    clearTimeout(reset);
    if (label !== 'Copy image') reset = setTimeout(() => status('Copy image', '#i-copy'), 2000);
  };
  ui.copy.hidden = false;
  ui.copy.onclick = async () => {
    try {
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': asPng() })]);
      status('Copied', '#i-check');
    } catch {
      status("Couldn't copy", '#i-alert');
    }
  };
}

async function present(id, plain, expires) {
  const type = sniff(plain);
  const blob = new Blob([plain], { type: type?.mime ?? 'application/octet-stream' });
  const url = URL.createObjectURL(blob);
  const filename = `fortefile-${id}.${type?.ext ?? 'bin'}`;
  for (const a of [ui.download, ui.stateDownload]) {
    a.href = url;
    a.download = filename;
  }
  if (Number.isFinite(expires)) countdown(expires);
  ui.toolbar.hidden = false;

  let el = null;
  if (type?.kind === 'image') {
    el = new Image();
    el.alt = 'The shared image';
    el.src = url;
    try {
      await el.decode();
    } catch {
      el = null;
    }
    if (el) {
      // Click to switch between fit-to-window and actual pixels, when the image had to be scaled down.
      el.addEventListener('click', () => {
        if (ui.content.classList.contains('actual') || el.naturalWidth > el.clientWidth + 1 || el.naturalHeight > el.clientHeight + 1)
          ui.content.classList.toggle('actual');
      });
      setupCopy(blob, type, el);
    }
  } else if (type?.kind === 'video') {
    el = document.createElement('video');
    el.controls = true;
    el.playsInline = true;
    el.preload = 'metadata';
    el.src = url;
  }

  if (!el) return show('unsupported', { text: `This file (${formatSize(plain.length)}) was unlocked, but it can't be shown in the browser.` });
  ui.content.replaceChildren(el);
  ui.content.hidden = false;
  ui.state.hidden = true;
  document.title = `Shared ${type.kind} · fortefile`;
}

async function open() {
  ui.toolbar.hidden = true;
  const id = /^\/f\/([A-Za-z0-9_-]{22})$/.exec(location.pathname)?.[1];
  if (!id) return show('badlink');
  const keyText = location.hash.slice(1).replace(/=+$/, '');
  if (!keyText) return show('nokey');
  const rawKey = b64urlDecode(keyText);
  if (!rawKey || rawKey.length !== 32) return show('badkey');
  if (!window.crypto?.subtle) return show('nocrypto');

  show('loading');
  let res;
  try {
    res = await fetchBytes(id);
  } catch {
    return show('network');
  }
  if (res.status === 404) return show('gone');
  if (res.status !== 200) return show('error');
  const data = res.bytes;
  if (data.length < 12 + 16) return show('badkey');

  let plain;
  try {
    const key = await crypto.subtle.importKey('raw', rawKey, 'AES-GCM', false, ['decrypt']);
    plain = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: data.subarray(0, 12) }, key, data.subarray(12)));
  } catch {
    return show('badkey');
  }
  await present(id, plain, res.expires);
}

ui.retry.addEventListener('click', open);
addEventListener('hashchange', () => location.reload());
open();
