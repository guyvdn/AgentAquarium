// Director: drives the real tank in the iframe from a script, moves a camera over it and animates
// the captions. Every frame is produced by __frame(n) in order; nothing reads the wall clock.
const FPS = 30;
const DURATION = 21.5;
const W = 1080;

const tankEl = document.getElementById('tank');
const shadeEl = document.getElementById('shade');
let tw; // tank window

// --- scenes -------------------------------------------------------------------

const LAYERS = [
  { id: 'hook', out: 2.95 },
  { id: 'reveal', out: 6.0 },
  { id: 'schools', out: 9.8 },
  { id: 'diver', out: 14.2 },
  { id: 'chest', out: 17.3 },
  { id: 'outro', out: 99 },
];

/** How fast the tank runs relative to the video: the diver gets a fast-forward to reach its fish. */
function simSpeed(t) {
  return t >= 9.6 && t < 13.6 ? 2 : 1;
}

/** Darkening over the tank, and blur, so captions read. */
function shadeAt(t) {
  const k = [
    [0, 0.9],
    [2.9, 0.9],
    [3.3, 0.5],
    [5.9, 0.5],
    [6.3, 0.12],
    [17.4, 0.12],
    [18.0, 0.72],
  ];
  return interp(k, t);
}
function blurAt(t) {
  return interp(
    [
      [0, 7],
      [2.9, 7],
      [3.4, 0],
    ],
    t,
  );
}

// --- scripted agents ------------------------------------------------------------

// Fictional sessions, the same ones the app's demo mode uses.
const AGENTS = [
  {
    id: 's1',
    provider: 'codex',
    label: 'Checkout coupon codes',
    detail: 'T3 Code · Codex · web-shop · coupons',
    ctx: 64_000,
    busy: 0.75,
    act: 0.85,
  },
  { id: 's2', label: 'Postgres failover runbook', detail: 'T3 Code · infra · main', ctx: 182_000, busy: 0.5, act: 0.6 },
  {
    id: 's3',
    label: 'Invoice PDF rounding fix',
    detail: 'CLI · billing-api · fix/rounding',
    ctx: 48_000,
    busy: 0.6,
    act: 0.7,
    waitAt: 8.4,
    waitingFor: 'permission prompt',
  },
  {
    id: 's4',
    provider: 'codex',
    label: 'Offline sync for orders',
    detail: 'Codex · mobile-app · offline-sync',
    ctx: 116_000,
    busy: 0.45,
    act: 0.55,
  },
  {
    id: 's5',
    label: 'Search for API reference',
    detail: 'T3 Code · docs-site · search',
    ctx: 31_000,
    busy: 0.55,
    act: 0.7,
  },
  { id: 's4a', parent: 's4', label: 'Map the sync API', detail: 'Plan', ctx: 22_000, busy: 0.6, act: 0.7 },
  {
    id: 's1a',
    parent: 's1',
    label: 'Find coupon call sites',
    detail: 'Explore',
    ctx: 42_000,
    busy: 0.8,
    act: 0.9,
    born: 6.85,
  },
  {
    id: 's1b',
    parent: 's1',
    label: 'Review the diff',
    detail: 'code-reviewer',
    ctx: 36_000,
    busy: 0.8,
    act: 0.9,
    born: 7.65,
  },
];
const TOOLS = ['Read', 'Edit', 'Bash', 'Grep', 'Glob', 'Write'];

let rs = 7;
const rnd = () => (rs = (rs * 1103515245 + 12345) % 2147483648) / 2147483648;

const state = new Map(
  AGENTS.map(a => [
    a.id,
    { tokens: a.ctx, spent: 0, calls: Math.floor(a.ctx / 3000), errors: 0, lastTool: 'Read', since: null },
  ]),
);

function iso(ms) {
  return new Date(ms).toISOString();
}

/** One server tick (every 500 ms of tank time), at video time t. */
function snapshot(t) {
  const now = tw.Date.now();
  const spendBoost = t > 13.4 && t < 17.4 ? 3.5 : 1;
  const out = [];
  for (const a of AGENTS) {
    if (a.born !== undefined && t < a.born) continue;
    const s = state.get(a.id);
    const waiting = a.waitAt !== undefined && t >= a.waitAt;
    if (!waiting && rnd() < a.busy) {
      s.calls++;
      s.lastTool = TOOLS[Math.floor(rnd() * TOOLS.length)];
      s.tokens += 800 + Math.floor(rnd() * 5200);
      s.spent += (s.tokens / 10 + 2000 + rnd() * 6000) * spendBoost;
    }
    const status = waiting ? 'Waiting' : 'Working';
    if (s.status !== status) {
      s.status = status;
      s.since = now;
    }
    out.push({
      id: a.id,
      parentId: a.parent ?? null,
      sessionId: a.parent ?? a.id,
      source: a.provider ?? AGENTS.find(root => root.id === a.parent)?.provider ?? 'claude-code',
      label: a.label,
      detail: a.detail,
      depth: a.parent ? 1 : 0,
      contextTokens: s.tokens,
      spentTokens: Math.round(s.spent),
      status,
      waitingFor: waiting ? a.waitingFor : null,
      statusSince: iso(s.since),
      activity: waiting ? 0 : a.act,
      toolCalls: s.calls,
      errors: s.errors,
      compactions: 0,
      lastTool: s.lastTool,
      lastSeen: iso(now),
    });
  }
  return out;
}

function usage(used, source = 'claude-code') {
  const now = tw.Date.now();
  return {
    fiveHour: { usedPercent: used, resetsAt: iso(now + 2 * 3600_000 + 7 * 60_000) },
    sevenDay: { usedPercent: 22, resetsAt: iso(now + 3 * 86400_000) },
    asOf: iso(now - 60_000),
    source,
  };
}

// --- tank clock -----------------------------------------------------------------

let nextEmit = 0; // tank ms of the next snapshot
let usageStep = 0;

function tankTick(ms, t) {
  tw.__tick(ms);
  while (tw.__S.vt >= nextEmit) {
    tw.__emit('snapshot', snapshot(t));
    nextEmit += 500;
  }
  if (usageStep === 0 && t >= 15.2) {
    usageStep = 1;
    tw.__emit('usage', usage(37));
    tw.__emit('usage', usage(25, 'codex'));
  }
}

// --- camera ---------------------------------------------------------------------

const cam = { x: W / 2, y: W / 2, z: 1 };

function fishPos(id) {
  const f = tw.__aq.world.fish.get(id);
  return f ? [f.x, f.y] : [W / 2, W / 2];
}

/** Where the camera wants to be at t: centre (tank px) and zoom. */
function cameraTarget(t) {
  const aq = tw.__aq;
  if (t < 3.1) return [W / 2, W / 2, 1 + 0.06 * (t / 3.1)];
  if (t < 6.1) return [W / 2, W / 2, 1];
  if (t < 9.9) {
    const z = 1.9;
    const [x, y] = fishPos('s1');
    return [x, y + (540 - 400) / z, z];
  }
  if (t < 14.25) {
    const [fx, fy] = fishPos('s3');
    const { x: dx, y: dy } = aq.diver;
    const span = Math.max(Math.abs(fx - dx) + 260, (Math.abs(fy - dy) + 240) / 0.62);
    const z = clamp(W / span, 1.15, 2.0);
    // Keep both above the caption card, which covers the bottom quarter.
    return [(fx + dx) / 2, (fy + dy) / 2 + 130 / z, z];
  }
  if (t < 17.35) {
    const z = 2.15;
    const chests = [...aq.chests.values()];
    return [(chests[0].x + chests[1].x) / 2 + 75, chests[0].y - 120, z];
  }
  return [W / 2, W / 2, 1];
}

function updateCamera(t, dt, snap) {
  const [x, y, z] = cameraTarget(t);
  const k = snap ? 1 : 1 - Math.exp(-dt * 4.2);
  cam.z += (z - cam.z) * k;
  cam.x += (x - cam.x) * k;
  cam.y += (y - cam.y) * k;
  const half = W / 2 / cam.z;
  const cx = clamp(cam.x, half, W - half);
  const cy = clamp(cam.y, half, W - half);
  tankEl.style.transform = `translate(${W / 2 - cx * cam.z}px, ${W / 2 - cy * cam.z}px) scale(${cam.z})`;
  // Zoomed in, the Schools panel would only show as a sliver cut off at the frame edge.
  const panel = tw.document.querySelector('.panel');
  if (panel) panel.style.opacity = String(clamp((1.3 - cam.z) / 0.25, 0, 1));
}

// --- captions -------------------------------------------------------------------

const easeOut = x => 1 - (1 - x) ** 3;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
function interp(keys, t) {
  if (t <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++)
    if (t <= keys[i][0]) {
      const [t0, v0] = keys[i - 1];
      const [t1, v1] = keys[i];
      const u = (t - t0) / (t1 - t0);
      return v0 + (v1 - v0) * (u * u * (3 - 2 * u));
    }
  return keys[keys.length - 1][1];
}

/** Split the hook lines into words that rise in one after another. */
function splitWords(el) {
  const walk = node => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === Node.TEXT_NODE) {
        const frag = document.createDocumentFragment();
        for (const part of child.textContent.split(/(\s+)/)) {
          if (!part) continue;
          if (/^\s+$/.test(part)) frag.append(part);
          else {
            const s = document.createElement('span');
            s.className = 'w';
            s.textContent = part;
            frag.append(s);
          }
        }
        child.replaceWith(frag);
      } else walk(child);
    }
  };
  walk(el);
}

const animated = [];
function setupCaptions() {
  for (const layer of LAYERS) {
    const root = document.getElementById(layer.id);
    layer.el = root;
    const items = [...root.querySelectorAll('[data-in]')];
    layer.in = Math.min(...items.map(e => +e.dataset.in));
    layer.card = root.querySelector('.card');
    for (const el of items) {
      if (el.classList.contains('hook')) {
        splitWords(el);
        [...el.querySelectorAll('.w')].forEach((w, i) =>
          animated.push({ el: w, at: +el.dataset.in + i * 0.075, layer }),
        );
      } else animated.push({ el, at: +el.dataset.in, layer });
    }
  }
}

function updateCaptions(t) {
  for (const layer of LAYERS) {
    const out = clamp((t - layer.out) / 0.28, 0, 1);
    const visible = t >= layer.in - 0.25 && out < 1;
    layer.el.style.display = visible ? '' : 'none';
    if (!visible) continue;
    layer.el.style.opacity = String(1 - easeOut(out));
    if (layer.card) {
      const u = easeOut(clamp((t - (layer.in - 0.2)) / 0.4, 0, 1));
      layer.card.style.opacity = String(u);
      layer.card.style.transform = `translateX(-50%) translateY(${(1 - u) * 18 - out * 10}px) scale(${0.97 + 0.03 * u})`;
    }
  }
  for (const a of animated) {
    const u = easeOut(clamp((t - a.at) / 0.45, 0, 1));
    a.el.style.opacity = String(u);
    a.el.style.transform = `translateY(${(1 - u) * 26}px)`;
    a.el.style.filter = u < 1 ? `blur(${(1 - u) * 8}px)` : '';
  }
}

// --- frames ---------------------------------------------------------------------

let frameNo = -1;

window.__init = async () => {
  await new Promise(r =>
    tankEl.contentDocument?.readyState === 'complete' ? r() : tankEl.addEventListener('load', r),
  );
  tw = tankEl.contentWindow;
  await document.fonts.ready;
  await tw.document.fonts.ready;
  await Promise.all([...document.images].map(i => i.decode().catch(() => {})));
  setupCaptions();

  // Warm up 12 s of tank time so the fish have spread out and faded in.
  tw.__emit('open', {});
  tw.__emit('usage', usage(31));
  tw.__emit('usage', usage(17, 'codex'));
  nextEmit = tw.__S.vt;
  for (let i = 0; i < 12 * FPS; i++) tankTick(1000 / FPS, -12 + i / FPS);
  return Math.round(DURATION * FPS);
};

window.__frame = n => {
  if (n !== frameNo + 1) throw new Error(`frames must run in order: got ${n} after ${frameNo}`);
  const t = n / FPS;
  if (n > 0) {
    const speed = simSpeed(t);
    const ticks = Math.ceil(speed);
    for (let i = 0; i < ticks; i++) tankTick((1000 / FPS) * (speed / ticks), t);
  }
  frameNo = n;
  updateCamera(t, 1 / FPS, n === 0);
  shadeEl.style.opacity = String(shadeAt(t));
  const b = blurAt(t);
  tankEl.style.filter = b > 0.05 ? `blur(${b}px)` : '';
  updateCaptions(t);
  tw.__syncAnimations();
  return t;
};
