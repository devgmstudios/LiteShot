// Download page: file names, sizes, SHA-256 and VirusTotal links straight from the signed release manifest.
(async () => {
  try {
    const res = await fetch('/v1/latest', { cache: 'no-store' });
    if (!res.ok) throw new Error('no release');
    const info = JSON.parse((await res.json()).payload);
    const date = new Date(info.published).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
    document.querySelector('[data-release-line]').textContent = `Version ${info.version}, released ${date}. Free and open source for Windows 10 and 11.`;
    for (const el of document.querySelectorAll('[data-version-only]')) el.textContent = info.version;
    for (const card of document.querySelectorAll('[data-file]')) {
      const f = info.files.find((x) => x.kind === card.dataset.file);
      if (!f) continue;
      card.querySelector('[data-name]').textContent = f.name;
      card.querySelector('[data-size]').textContent = `${(f.size / 1048576).toFixed(1)} MB`;
      card.querySelector('[data-sha]').textContent = f.sha256;
      card.querySelector('[data-vt]').href = `https://www.virustotal.com/gui/file/${f.sha256}`;
    }
  } catch {
    document.querySelector('[data-release-line]').textContent = 'Coming soon — free and open source for Windows 10 and 11.';
  }
})();
