// fortefile landing page: cards rise in as they scroll into view, and the sample link counts down.

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
