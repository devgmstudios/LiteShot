// Download page: file names, sizes, SHA-256 and VirusTotal links straight from the signed release manifests: the
// "stable" channel for Windows, "mac" for the Mac app.
const manifest = async (channel) => {
  const res = await fetch(`/v1/latest?channel=${channel}`, { cache: 'no-store' });
  if (!res.ok) throw new Error('no release');
  return JSON.parse((await res.json()).payload);
};
const size = (bytes) => (bytes < 1048576 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1048576).toFixed(1)} MB`);

function fill(info, channel) {
  const cards = [...document.querySelectorAll('[data-file]')].filter((c) => (c.dataset.channel ?? 'stable') === channel);
  for (const card of cards) {
    const f = info.files.find((x) => x.kind === card.dataset.file);
    if (!f) continue;
    card.querySelector('[data-name]').textContent = f.name;
    card.querySelector('[data-size]').textContent = size(f.size);
    card.querySelector('[data-sha]').textContent = f.sha256;
    const vt = card.querySelector('[data-vt]');
    if (vt) vt.href = `https://www.virustotal.com/gui/file/${f.sha256}`;
  }
}

(async () => {
  try {
    const info = await manifest('stable');
    const date = new Date(info.published).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
    document.querySelector('[data-release-line]').textContent = `Version ${info.version}, released ${date}. Free and open source for Windows 10 and 11, and now in beta for Mac.`;
    for (const el of document.querySelectorAll('[data-version-only]')) el.textContent = info.version;
    fill(info, 'stable');
  } catch {
    document.querySelector('[data-release-line]').textContent = 'Coming soon — free and open source for Windows 10 and 11.';
  }
})();

(async () => {
  try {
    const info = await manifest('mac');
    for (const el of document.querySelectorAll('[data-mac-version]')) el.textContent = info.version;
    fill(info, 'mac');
  } catch {
    const sha = document.querySelector('[data-channel="mac"] [data-sha]');
    if (sha) sha.textContent = 'coming soon';
  }
})();
