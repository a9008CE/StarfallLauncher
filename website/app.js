(() => {
  'use strict';

  /* ── STARFIELD ──────────────────────────────────── */
  const canvas = document.getElementById('starfield');
  const ctx = canvas.getContext('2d');
  let stars = [];
  let meteors = [];
  let w, h;

  function resize() {
    w = canvas.width = window.innerWidth;
    h = canvas.height = window.innerHeight;
  }

  function initStars() {
    stars = [];
    const count = Math.floor((w * h) / 3000);
    for (let i = 0; i < count; i++) {
      stars.push({
        x: Math.random() * w,
        y: Math.random() * h,
        r: Math.random() * 1.2 + 0.3,
        a: Math.random() * 0.6 + 0.2,
        speed: Math.random() * 0.15 + 0.02,
        pulse: Math.random() * Math.PI * 2,
        pulseSpeed: Math.random() * 0.008 + 0.002
      });
    }
  }

  function spawnMeteor() {
    if (meteors.length > 4) return;
    if (Math.random() > 0.012) return;
    const side = Math.random();
    meteors.push({
      x: side < 0.5 ? Math.random() * w * 0.6 : -20,
      y: side < 0.5 ? -20 : Math.random() * h * 0.4,
      len: Math.random() * 120 + 60,
      speed: Math.random() * 6 + 5,
      angle: Math.PI / 4 + (Math.random() - 0.5) * 0.15,
      life: 1,
      hue: Math.random() < 0.3 ? 170 : 180
    });
  }

  function draw() {
    ctx.clearRect(0, 0, w, h);

    // ambient gradient orbs
    const t = Date.now() * 0.0001;
    const ox1 = w * 0.2 + Math.sin(t) * 60;
    const oy1 = h * 0.3 + Math.cos(t * 0.7) * 40;
    const g1 = ctx.createRadialGradient(ox1, oy1, 0, ox1, oy1, 300);
    g1.addColorStop(0, 'rgba(113,229,220,0.03)');
    g1.addColorStop(1, 'transparent');
    ctx.fillStyle = g1;
    ctx.fillRect(0, 0, w, h);

    const ox2 = w * 0.8 + Math.cos(t * 1.3) * 50;
    const oy2 = h * 0.6 + Math.sin(t * 0.9) * 50;
    const g2 = ctx.createRadialGradient(ox2, oy2, 0, ox2, oy2, 250);
    g2.addColorStop(0, 'rgba(113,229,220,0.02)');
    g2.addColorStop(1, 'transparent');
    ctx.fillStyle = g2;
    ctx.fillRect(0, 0, w, h);

    // stars
    for (const s of stars) {
      s.pulse += s.pulseSpeed;
      const alpha = s.a + Math.sin(s.pulse) * 0.2;
      const glow = s.r > 1 ? 1 : 0;
      if (glow) {
        ctx.beginPath();
        ctx.arc(s.x, s.y, s.r + 2, 0, Math.PI * 2);
        ctx.fillStyle = `rgba(113,229,220,${alpha * 0.15})`;
        ctx.fill();
      }
      ctx.beginPath();
      ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
      ctx.fillStyle = `rgba(200,225,240,${alpha})`;
      ctx.fill();
      s.y += s.speed;
      if (s.y > h + 5) { s.y = -5; s.x = Math.random() * w; }
    }

    // meteors
    for (let i = meteors.length - 1; i >= 0; i--) {
      const m = meteors[i];
      const dx = Math.cos(m.angle) * m.len;
      const dy = Math.sin(m.angle) * m.len;
      // glow trail
      ctx.save();
      ctx.globalAlpha = m.life * 0.3;
      ctx.shadowColor = `hsla(${m.hue},80%,70%,1)`;
      ctx.shadowBlur = 20;
      const grad = ctx.createLinearGradient(m.x, m.y, m.x - dx, m.y - dy);
      grad.addColorStop(0, `hsla(${m.hue},80%,85%,${m.life})`);
      grad.addColorStop(0.3, `hsla(${m.hue},70%,70%,${m.life * 0.6})`);
      grad.addColorStop(1, 'transparent');
      ctx.strokeStyle = grad;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(m.x, m.y);
      ctx.lineTo(m.x - dx, m.y - dy);
      ctx.stroke();
      ctx.restore();
      // bright head
      ctx.beginPath();
      ctx.arc(m.x, m.y, 2.5 * m.life, 0, Math.PI * 2);
      ctx.fillStyle = `hsla(${m.hue},60%,95%,${m.life})`;
      ctx.fill();
      m.x += Math.cos(m.angle) * m.speed;
      m.y += Math.sin(m.angle) * m.speed;
      m.life -= 0.006;
      if (m.life <= 0 || m.y > h + 80 || m.x > w + 80) meteors.splice(i, 1);
    }

    spawnMeteor();
    requestAnimationFrame(draw);
  }

  resize();
  initStars();
  draw();
  window.addEventListener('resize', () => { resize(); initStars(); });

  /* ── THEME TOGGLE ──────────────────────────────── */
  const html = document.documentElement;
  const toggle = document.getElementById('themeToggle');
  const techBg = document.getElementById('techBg');
  const savedTheme = localStorage.getItem('theme-v5');

  function applyTheme(isLight) {
    html.classList.toggle('light', isLight);
    if (techBg) techBg.style.display = isLight ? 'block' : 'none';
  }

  if (savedTheme) applyTheme(savedTheme === 'light');
  else applyTheme(false);

  toggle.addEventListener('click', () => {
    const isLight = !html.classList.contains('light');
    applyTheme(isLight);
    localStorage.setItem('theme-v5', isLight ? 'light' : 'dark');
    toggle.style.transform = 'scale(0.85)';
    setTimeout(() => { toggle.style.transform = ''; }, 150);
  });

  /* ── SCROLL REVEAL ─────────────────────────────── */
  const revealObserver = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add('revealed');
        revealObserver.unobserve(entry.target);
      }
    });
  }, { threshold: 0.08, rootMargin: '0px 0px -40px 0px' });

  document.querySelectorAll('[data-reveal]').forEach((el) => {
    revealObserver.observe(el);
  });

  /* ── SCREENSHOT CAROUSEL ────────────────────────── */
  const cards = document.querySelectorAll('.screenshot-card');
  const btns = document.querySelectorAll('.ss-btn');
  let current = 0;
  let autoTimer;

  function showSlide(index) {
    cards.forEach((c) => c.classList.remove('active'));
    btns.forEach((b) => b.classList.remove('active'));
    cards[index].classList.add('active');
    btns[index].classList.add('active');
    current = index;
  }

  btns.forEach((btn) => {
    btn.addEventListener('click', () => {
      showSlide(Number(btn.dataset.target));
      resetAuto();
    });
  });

  function nextSlide() {
    showSlide((current + 1) % cards.length);
  }

  function resetAuto() {
    clearInterval(autoTimer);
    autoTimer = setInterval(nextSlide, 4000);
  }

  resetAuto();

  /* ── COPY BUTTONS ───────────────────────────────── */
  document.querySelectorAll('.copy-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      const text = btn.dataset.copy;
      navigator.clipboard.writeText(text).then(() => {
        btn.classList.add('copied');
        setTimeout(() => btn.classList.remove('copied'), 1500);
      });
    });
  });

  /* ── SMOOTH NAV SCROLL ──────────────────────────── */
  function smoothScrollTo(target, duration) {
    const start = window.scrollY;
    const dist = target - start;
    const startTime = performance.now();
    function ease(t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; }
    function step(now) {
      const elapsed = now - startTime;
      const progress = Math.min(elapsed / duration, 1);
      window.scrollTo(0, start + dist * ease(progress));
      if (progress < 1) requestAnimationFrame(step);
    }
    requestAnimationFrame(step);
  }

  document.querySelectorAll('.nav a, .brand, .hero-actions a[href^="#"], .cta-inner a[href^="#"]').forEach((a) => {
    a.addEventListener('click', (e) => {
      const href = a.getAttribute('href');
      if (href && href.startsWith('#')) {
        e.preventDefault();
        const el = document.querySelector(href);
        if (el) smoothScrollTo(el.getBoundingClientRect().top + window.scrollY - 80, 700);
      }
    });
  });

  /* ── FETCH STATUS ───────────────────────────────── */
  const statusUrl = 'https://starfall-updater.tail309cd1.ts.net/update.json';
  fetch(statusUrl, { cache: 'no-store' })
    .then((r) => { if (!r.ok) throw new Error(); return r.json(); })
    .then((manifest) => {
      document.querySelectorAll('[data-release-version]').forEach((n) => {
        n.textContent = manifest.Version;
      });
    })
    .catch(() => {
      document.documentElement.dataset.status = 'offline';
    });

  /* ── STATUS OVERLAY ─────────────────────────────── */
  const statusChip = document.getElementById('statusChip');
  const overlay = document.getElementById('statusOverlay');
  const statusClose = document.getElementById('statusClose');
  const pCanvas = document.getElementById('statusParticles');
  const pCtx = pCanvas.getContext('2d');
  const typeEl = document.getElementById('typeLine');
  const pfTime = document.getElementById('pfTime');
  let pW, pH, pAnim, pParticles = [];

  const logs = [
    '> establishing connection to update node...',
    '> tailscale funnel [OK]',
    '> curseforge / modrinth api [OK]',
    '> integrity check: SHA-256 verified',
    '> all systems operational — STARFALL GO'
  ];

  function pResize() { pW = pCanvas.width = window.innerWidth; pH = pCanvas.height = window.innerHeight; }

  function pInit() {
    pParticles = [];
    for (let i = 0; i < 100; i++) {
      pParticles.push({
        x: Math.random() * pW, y: Math.random() * pH,
        r: Math.random() * 1.3 + 0.2,
        vx: (Math.random() - 0.5) * 0.3, vy: (Math.random() - 0.5) * 0.3,
        a: Math.random() * 0.35 + 0.05,
        pulse: Math.random() * Math.PI * 2,
        c: Math.random() < 0.12 ? 'cyan' : 'normal'
      });
    }
  }

  function pDraw() {
    pCtx.clearRect(0, 0, pW, pH);
    for (let i = 0; i < pParticles.length; i++) {
      for (let j = i + 1; j < pParticles.length; j++) {
        const dx = pParticles[i].x - pParticles[j].x, dy = pParticles[i].y - pParticles[j].y;
        const d2 = dx * dx + dy * dy;
        if (d2 < 10000) {
          pCtx.beginPath();
          pCtx.moveTo(pParticles[i].x, pParticles[i].y);
          pCtx.lineTo(pParticles[j].x, pParticles[j].y);
          pCtx.strokeStyle = `rgba(113,229,220,${(1 - d2 / 10000) * 0.08})`;
          pCtx.lineWidth = 0.3;
          pCtx.stroke();
        }
      }
    }
    for (const p of pParticles) {
      p.pulse += 0.012;
      const a = p.a + Math.sin(p.pulse) * 0.1;
      const col = p.c === 'cyan' ? `rgba(77,208,199,${a})` : `rgba(180,200,215,${a})`;
      pCtx.beginPath(); pCtx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      pCtx.fillStyle = col; pCtx.fill();
      p.x += p.vx; p.y += p.vy;
      if (p.x < 0) p.x = pW; if (p.x > pW) p.x = 0;
      if (p.y < 0) p.y = pH; if (p.y > pH) p.y = 0;
    }
    pAnim = requestAnimationFrame(pDraw);
  }

  let typeTimer = null;
  function startTyping() {
    let li = 0, ci = 0;
    typeEl.innerHTML = '<span class="cursor">_</span>';
    clearInterval(typeTimer);
    typeTimer = setInterval(() => {
      if (li >= logs.length) { clearInterval(typeTimer); return; }
      if (ci <= logs[li].length) {
        typeEl.innerHTML = logs[li].slice(0, ci) + '<span class="cursor">_</span>';
        ci++;
      } else {
        setTimeout(() => { li++; ci = 0; }, 350);
      }
    }, 35);
  }

  function tickClock() {
    const n = new Date();
    pfTime.textContent = n.toISOString().replace('T', ' ').slice(0, 19) + ' UTC';
  }
  let clockTimer = null;

  function openStatus() {
    pResize(); pInit(); pDraw();
    tickClock();
    clockTimer = setInterval(tickClock, 1000);
    overlay.classList.add('open');
    document.body.style.overflow = 'hidden';
    setTimeout(startTyping, 700);
  }

  function closeStatus() {
    overlay.classList.remove('open');
    document.body.style.overflow = '';
    cancelAnimationFrame(pAnim);
    clearInterval(typeTimer);
    clearInterval(clockTimer);
    typeEl.innerHTML = '';
  }

  statusChip.addEventListener('click', openStatus);
  statusClose.addEventListener('click', closeStatus);
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeStatus(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeStatus(); });
})();
