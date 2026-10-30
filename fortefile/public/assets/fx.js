// fortefile button effects, after React Bits (reactbits.dev): edges that catch the light where the pointer is
// (Specular Button) and sparks where a button is clicked (Click Spark). Both are extra: without this script the CSS
// buttons stand on their own. Reduced motion drops the sparks; forced colors turn everything off.

// Its own scope: site.js shares the page's global one.
(() => {
  const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const forced = matchMedia('(forced-colors: active)').matches;
  let px = -1e5, py = -1e5;

  // ───────── specular edges ─────────
  // Each button's light sits on the part of its edge facing the pointer (--a, degrees clockwise from the top) and
  // brightens from IDLE to full as the pointer comes within REACH px (--i). The CSS transitions do the gliding.

  const IDLE = 0.35, REACH = 250;
  const buttons = forced ? [] : [...document.querySelectorAll('.btn')];
  const angles = new WeakMap();
  let queued = false;

  function light() {
    queued = false;
    for (const b of buttons) {
      const r = b.getBoundingClientRect();
      if (!r.width || r.bottom < -REACH || r.top > innerHeight + REACH) continue;
      const gap = Math.hypot(Math.max(r.left - px, 0, px - r.right), Math.max(r.top - py, 0, py - r.bottom));
      const near = Math.max(0, 1 - gap / REACH);
      if (near > 0) {
        let a = (Math.atan2(px - (r.left + r.width / 2), r.top + r.height / 2 - py) * 180) / Math.PI;
        // keep the angle continuous, so the light never swings the long way round
        const before = angles.get(b) ?? -50;
        a += Math.round((before - a) / 360) * 360;
        angles.set(b, a);
        b.style.setProperty('--a', `${a.toFixed(1)}deg`);
      }
      b.style.setProperty('--i', (IDLE + (1 - IDLE) * near * (2 - near)).toFixed(3));
    }
  }
  const queue = () => {
    if (queued || !buttons.length) return;
    queued = true;
    requestAnimationFrame(light);
  };
  const away = () => { px = py = -1e5; queue(); };

  addEventListener('pointermove', (e) => { px = e.clientX; py = e.clientY; queue(); }, { passive: true });
  addEventListener('pointerdown', (e) => { px = e.clientX; py = e.clientY; queue(); }, { passive: true });
  addEventListener('pointerup', (e) => { if (e.pointerType !== 'mouse') setTimeout(away, 600); }, { passive: true });
  document.documentElement.addEventListener('pointerleave', away);
  addEventListener('scroll', queue, { passive: true });

  // ───────── click sparks ─────────

  if (!still && !forced) {
    document.addEventListener('click', (e) => {
      // detail is 0 for keyboard presses, which have no pointer position to spark from
      if (!e.detail || !e.target.closest?.('.btn')) return;
      const spark = document.createElement('span');
      spark.className = 'spark';
      spark.setAttribute('aria-hidden', 'true');
      spark.style.translate = `${e.clientX}px ${e.clientY}px`;
      for (let k = 0; k < 8; k++) {
        const line = document.createElement('i');
        line.style.setProperty('--r', `${k * 45}deg`);
        spark.append(line);
      }
      document.body.append(spark);
      setTimeout(() => spark.remove(), 500);
    });
  }
})();
