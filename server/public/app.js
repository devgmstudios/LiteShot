// LiteShot site: release details from the signed manifest, the hero tilt, reveals, and the link countdown.

(async function release() {
  try {
    const res = await fetch('/v1/latest', { cache: 'no-store' });
    if (!res.ok) throw new Error('no release');
    const { payload } = await res.json();
    const info = JSON.parse(payload);
    const mb = (n) => `${(n / 1048576).toFixed(0)} MB`;
    const file = (kind) => info.files.find((f) => f.kind === kind);
    for (const el of document.querySelectorAll('[data-version]')) el.textContent = `Version ${info.version}`;
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
    for (const el of document.querySelectorAll('[data-version]')) el.textContent = 'Coming soon';
  }
})();

const still = matchMedia('(prefers-reduced-motion: reduce)').matches;

// Hero entrance: "Screenshots," rises letter by letter out of a blur, then the rest follows on a spring.
for (const el of document.querySelectorAll('[data-split]')) {
  const text = el.textContent;
  el.textContent = '';
  [...text].forEach((c, i) => {
    const ch = document.createElement('span');
    ch.className = 'ch';
    ch.textContent = c;
    ch.style.setProperty('--i', i);
    el.append(ch);
  });
  el.classList.add('split');
}
{
  // Start once the fonts are in, so letters don't shift mid-flight; never wait more than a second.
  let started = false;
  const go = () => { if (!started) { started = true; requestAnimationFrame(() => document.documentElement.classList.add('go')); } };
  (document.fonts?.ready ?? Promise.resolve()).then(go);
  setTimeout(go, 1000);
}

// The nav frosts over once the page scrolls under it.
const nav = document.querySelector('.nav');
if (nav) {
  const frost = () => nav.classList.toggle('scrolled', scrollY > 12);
  addEventListener('scroll', frost, { passive: true });
  frost();
}

if (!still && matchMedia('(pointer: fine)').matches) {
  // Buttons lean toward the pointer and spring back when it leaves.
  for (const b of document.querySelectorAll('.magnetic')) {
    b.addEventListener('pointermove', (e) => {
      const r = b.getBoundingClientRect();
      b.classList.add('pulling');
      b.style.setProperty('--tx', `${(((e.clientX - r.left) / r.width - 0.5) * 12).toFixed(1)}px`);
      b.style.setProperty('--ty', `${(((e.clientY - r.top) / r.height - 0.5) * 8).toFixed(1)}px`);
    });
    b.addEventListener('pointerleave', () => {
      b.classList.remove('pulling');
      b.style.setProperty('--tx', '0px');
      b.style.setProperty('--ty', '0px');
    });
  }

  // A soft light follows the pointer across the hero, eased so it trails a little.
  const hero = document.querySelector('.hero');
  const spot = hero?.querySelector('.spot');
  if (hero && spot) {
    let tx = 0, ty = 0, x = 0, y = 0, raf = 0;
    const step = () => {
      x += (tx - x) * 0.14;
      y += (ty - y) * 0.14;
      spot.style.setProperty('--mx', `${x.toFixed(1)}px`);
      spot.style.setProperty('--my', `${y.toFixed(1)}px`);
      raf = Math.abs(tx - x) + Math.abs(ty - y) > 0.5 ? requestAnimationFrame(step) : 0;
    };
    hero.addEventListener('pointermove', (e) => {
      const r = spot.getBoundingClientRect();
      tx = e.clientX - r.left;
      ty = e.clientY - r.top;
      if (!hero.classList.contains('lit')) { x = tx; y = ty; hero.classList.add('lit'); }
      if (!raf) raf = requestAnimationFrame(step);
    });
    hero.addEventListener('pointerleave', () => hero.classList.remove('lit'));
  }
}

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

// Feature animations (Lottie): the player loads with the first one that scrolls into view; the static icon stays
// until its animation is ready, and reduced motion gets a still frame.
const animated = document.querySelectorAll('[data-lottie]');
if (animated.length) {
  let player;
  const loadPlayer = () => (player ??= new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = '/vendor/lottie_light.min.js';
    script.onload = () => resolve(window.lottie);
    script.onerror = reject;
    document.head.append(script);
  }));
  const watch = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      watch.unobserve(entry.target);
      const badge = entry.target;
      loadPlayer().then((lottie) => {
        const box = document.createElement('div');
        box.className = 'lottie';
        badge.append(box);
        const anim = lottie.loadAnimation({ container: box, renderer: 'svg', loop: !still, autoplay: !still, path: `/lottie/${badge.dataset.lottie}.json` });
        anim.addEventListener('DOMLoaded', () => {
          badge.querySelector('svg.icon')?.remove();
          if (still) anim.goToAndStop(120, true);
        });
      }).catch(() => { /* keep the static icon */ });
    }
  }, { rootMargin: '0px 0px -5% 0px' });
  animated.forEach((el) => watch.observe(el));
}

// FacePrivacy.ai's blur brush, live: paint over the picture and a blurred copy shows through the brush.
for (const demo of document.querySelectorAll('[data-blur-demo]')) {
  const canvas = demo.querySelector('canvas');
  const ctx = canvas.getContext('2d');
  const size = demo.querySelector('[data-brush]');
  const ring = demo.querySelector('.brush-ring');
  const blurred = document.createElement('canvas');
  const img = new Image();
  let painting = false, last = null;
  const original = () => ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
  img.onload = () => {
    canvas.width = blurred.width = img.naturalWidth;
    canvas.height = blurred.height = img.naturalHeight;
    // Shrink, then grow back in two smooth steps: a strong blur that works in every browser.
    let source = img;
    for (const k of [14, 4, 1]) {
      const step = document.createElement('canvas');
      step.width = Math.ceil(canvas.width / k);
      step.height = Math.ceil(canvas.height / k);
      const s = step.getContext('2d');
      s.imageSmoothingQuality = 'high';
      s.drawImage(source, 0, 0, step.width, step.height);
      source = step;
    }
    blurred.getContext('2d').drawImage(source, 0, 0);
    original();
  };
  img.src = demo.dataset.blurDemo;

  const scale = () => canvas.width / canvas.getBoundingClientRect().width;
  const at = (e) => {
    const r = canvas.getBoundingClientRect();
    return { x: (e.clientX - r.left) * scale(), y: (e.clientY - r.top) * scale() };
  };
  const dab = (x, y, radius) => {
    ctx.save();
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.clip();
    ctx.drawImage(blurred, 0, 0);
    ctx.restore();
  };
  const paint = (p) => {
    const radius = (size.value * scale()) / 2;
    if (!last) dab(p.x, p.y, radius);
    else {
      // Fill the gap between pointer events so fast strokes stay continuous.
      const dx = p.x - last.x, dy = p.y - last.y, n = Math.max(1, Math.ceil(Math.hypot(dx, dy) / (radius / 3)));
      for (let i = 1; i <= n; i++) dab(last.x + (dx * i) / n, last.y + (dy * i) / n, radius);
    }
    last = p;
  };
  const moveRing = (e) => {
    const r = canvas.getBoundingClientRect();
    ring.style.setProperty('--x', `${e.clientX - r.left}px`);
    ring.style.setProperty('--y', `${e.clientY - r.top}px`);
    ring.style.setProperty('--size', `${size.value}px`);
  };
  canvas.addEventListener('pointerdown', (e) => {
    painting = true;
    last = null;
    canvas.setPointerCapture(e.pointerId);
    demo.classList.add('used');
    paint(at(e));
  });
  canvas.addEventListener('pointermove', (e) => {
    moveRing(e);
    if (painting) paint(at(e));
  });
  for (const type of ['pointerup', 'pointercancel']) canvas.addEventListener(type, () => { painting = false; last = null; });
  size.addEventListener('input', () => ring.style.setProperty('--size', `${size.value}px`));
  demo.querySelector('[data-reset]').addEventListener('click', () => { original(); demo.classList.remove('used'); });
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
