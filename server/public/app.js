// LiteShot site: release details from the signed manifest, the hero tilt, reveals, and the link countdown.

(async function release() {
  try {
    const res = await fetch('/v1/latest', { cache: 'no-store' });
    if (!res.ok) throw new Error('no release');
    const { payload } = await res.json();
    const info = JSON.parse(payload);
    const mb = (n) => `${(n / 1048576).toFixed(0)} MB`;
    const file = (kind) => info.files.find((f) => f.kind === kind);
    for (const el of document.querySelectorAll('[data-release]'))
      el.textContent = `Version ${info.version} · Free · Windows 10 and 11 · No account`;
    for (const kind of ['msi', 'portable']) {
      const f = file(kind);
      const label = document.querySelector(`[data-label="${kind}"]`);
      if (f && label) label.textContent = `${kind === 'msi' ? 'Installer' : 'Portable'} · ${mb(f.size)}`;
    }
    const msi = file('msi');
    const hash = document.querySelector('[data-hash]');
    if (msi && hash) {
      const note = hash.textContent;
      hash.textContent = '';
      hash.append(`Version ${info.version}. Installer SHA-256: `);
      const code = document.createElement('code');
      code.textContent = msi.sha256;
      hash.append(code, document.createElement('br'), note);
    }
  } catch {
    for (const el of document.querySelectorAll('[data-download]')) el.setAttribute('aria-disabled', 'true');
    for (const el of document.querySelectorAll('[data-release]')) el.textContent = 'Coming soon · Free · Windows 10 and 11';
  }
})();

const still = matchMedia('(prefers-reduced-motion: reduce)').matches;

// The hero shot starts tilted back and settles flat as you scroll toward it.
const shot = document.getElementById('shot');
if (shot && !still) {
  let queued = false;
  const update = () => {
    queued = false;
    const top = shot.getBoundingClientRect().top;
    const t = Math.min(1, Math.max(0, (top - innerHeight * 0.25) / (innerHeight * 0.55)));
    shot.style.setProperty('--tilt', t.toFixed(3));
  };
  addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(update); } }, { passive: true });
  update();
}

// Cards rise in as they come into view.
const io = new IntersectionObserver((entries) => {
  for (const e of entries) if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
}, { rootMargin: '0px 0px -8% 0px' });
document.querySelectorAll('.reveal').forEach((el) => (still ? el.classList.add('in') : io.observe(el)));

// "See it work": tabs that advance on their own until someone takes over.
const steps = document.querySelector('[data-steps]');
if (steps) {
  const tabs = [...steps.querySelectorAll('[role="tab"]')];
  let current = 0, timer = 0, manual = still;
  const show = (i, focus) => {
    current = (i + tabs.length) % tabs.length;
    tabs.forEach((t, n) => {
      const on = n === current;
      t.setAttribute('aria-selected', on);
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
    });
    if (focus) tabs[current].focus();
    steps.classList.toggle('manual', manual);
    clearTimeout(timer);
    if (!manual) timer = setTimeout(() => show(current + 1), 6000);
  };
  const takeOver = () => { manual = true; };
  tabs.forEach((t, n) => t.addEventListener('click', () => { takeOver(); show(n); }));
  steps.addEventListener('keydown', (e) => {
    const move = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key];
    if (move) { e.preventDefault(); takeOver(); show(current + move, true); }
  });
  // Start the tour only once it's on screen.
  new IntersectionObserver((entries, obs) => {
    if (entries.some((e) => e.isIntersecting)) { obs.disconnect(); show(0); }
  }, { threshold: 0.35 }).observe(steps);
}

// Before / after: the range input sets where the "after" picture starts.
for (const box of document.querySelectorAll('[data-compare]')) {
  const range = box.querySelector('input[type="range"]');
  const apply = () => box.style.setProperty('--pos', `${range.value}%`);
  range.addEventListener('input', apply);
  apply();
}

// A shared link's life, counting down from 24 hours.
const clock = document.querySelector('[data-countdown]');
if (clock) {
  const end = Date.now() + 24 * 3600e3;
  const pad = (n) => String(n).padStart(2, '0');
  const tick = () => {
    const s = Math.max(0, Math.round((end - Date.now()) / 1000));
    clock.textContent = `${pad(Math.floor(s / 3600))}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`;
  };
  tick();
  setInterval(tick, 1000);
}
