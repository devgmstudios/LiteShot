// fortefile pages: cards rise in as they scroll into view, the sample link counts down, and the silver security
// badges play their animation once when they come into view.

const still = matchMedia('(prefers-reduced-motion: reduce)').matches;

// Only elements still below the fold are hidden, so nothing stays invisible if this script never runs.
if (!still && 'IntersectionObserver' in window) {
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); }
  }, { rootMargin: '0px 0px -8% 0px' });
  for (const el of document.querySelectorAll('[data-reveal]')) {
    if (el.getBoundingClientRect().top < innerHeight) continue;
    el.classList.add('reveal');
    io.observe(el);
  }
}

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

// Security badges. Each one holds a finished static icon; when it scrolls into view, the self-hosted Lottie player
// loads (once, for the whole page), plays the badge's animation once, and replays it when the card is hovered.
// The animation ends on the frame the static icon shows. Reduced motion and forced colors keep the static icons.
const badges = document.querySelectorAll('[data-lottie]');
const plain = still || matchMedia('(forced-colors: active)').matches;
if (badges.length && !plain && 'IntersectionObserver' in window) {
  const PLAYER = '/vendor/lottie_light.min.js';
  let player;
  // The site requires Trusted Types for script URLs, so the player's address goes through a policy that allows
  // exactly that one file.
  const loadPlayer = () => (player ??= new Promise((resolve, reject) => {
    const policy = window.trustedTypes?.createPolicy('fortefile-lottie', {
      createScriptURL: (url) => { if (url === PLAYER) return url; throw new TypeError(`blocked script ${url}`); },
    });
    const script = document.createElement('script');
    script.src = policy ? policy.createScriptURL(PLAYER) : PLAYER;
    script.onload = () => (window.lottie ? resolve(window.lottie) : reject(new Error('Lottie player missing')));
    script.onerror = reject;
    document.head.append(script);
  }));

  const start = (badge) => loadPlayer().then((lottie) => {
    const box = document.createElement('span');
    box.className = 'badge-anim';
    box.setAttribute('aria-hidden', 'true');
    badge.append(box);
    const anim = lottie.loadAnimation({
      container: box, renderer: 'svg', loop: false, autoplay: false, path: `/lottie/${badge.dataset.lottie}.json`,
      rendererSettings: { preserveAspectRatio: 'xMidYMid meet', progressiveLoad: false },
    });
    anim.addEventListener('DOMLoaded', () => {
      badge.classList.add('is-live');
      anim.play();
    });
    anim.addEventListener('data_failed', () => box.remove());
    const host = badge.closest('[data-lottie-host]') ?? badge;
    host.addEventListener('pointerenter', () => {
      if (badge.classList.contains('is-live') && anim.isPaused) anim.goToAndPlay(0, true);
    });
  }).catch(() => { /* keep the static icon */ });

  const watch = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      watch.unobserve(entry.target);
      start(entry.target);
    }
  }, { rootMargin: '0px 0px -10% 0px' });
  badges.forEach((badge) => watch.observe(badge));
}
