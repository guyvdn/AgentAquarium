import { Fish } from './fish.js';
import { MotionLoop } from './motion.js';
import { Scenery, Bubbles } from './scenery.js';
import { Chest } from './chest.js';
import { Diver, waitsOnYou } from './diver.js';
import { fishColor } from './colors.js';

const STREAM_URL = '/api/agents/stream';
const RECONNECT_MAX_MS = 30_000;
const SURFACE = 60; // px kept clear above the fish, under the title
const TOOLTIP_OFFSET = 14;
const LABEL_MAX_WIDTH = 220; // px; session titles can be long
const PULSE_MS = 2400; // one breath of the "needs you" glow on a Schools swatch
const TOOLTIP_ROOM = 330; // the tooltip's max-width plus a margin, so it never runs off the right edge

const canvas = document.getElementById('tank');
const ctx = canvas.getContext('2d');
const schoolsEl = document.getElementById('schools');
const tooltipEl = document.getElementById('tooltip');
const emptyEl = document.getElementById('empty');
const connectionEl = document.getElementById('connection');

const scenery = new Scenery();
const bubbles = new Bubbles();
const chests = new Map([
  ['claude-code', new Chest('Claude')],
  ['codex', new Chest('Codex')],
]);

function chestFor(source) {
  return chests.get(source === 'demo' ? 'claude-code' : source);
}

function placeChests() {
  let index = 0;
  for (const chest of chests.values()) if (chest.window) chest.place(world.width, world.floor, index++);
}
const diver = new Diver();
const world = { fish: new Map(), width: 0, height: 0, top: 0, floor: 0 };

let firstSnapshot = true;
let highlightId = null;
let hoverId = null;
let pointer = null;
let tooltipFor = null; // the agent snapshot the tooltip was last rendered for
let markupTooltip = ''; // the chest's or diver's last tooltip markup

let sceneTime = 0;
const motion = new MotionLoop((dt, animate) => {
  document.documentElement.classList.toggle('motion-paused', !animate);
  if (animate) {
    sceneTime += dt;
    simulate(dt, sceneTime);
  } else settle();
  render(sceneTime);
  updateTooltip();
});
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
motion.setReduced(reducedMotion.matches);
motion.setHidden(document.hidden);
reducedMotion.addEventListener('change', event => motion.setReduced(event.matches));
document.addEventListener('visibilitychange', () => {
  motion.setHidden(document.hidden);
  scheduleClock();
});

// --- layout -----------------------------------------------------------------

function resize() {
  const dpr = window.devicePixelRatio || 1;
  world.width = window.innerWidth;
  world.height = window.innerHeight;
  canvas.width = world.width * dpr;
  canvas.height = world.height * dpr;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  scenery.resize(world.width, world.height, dpr);
  world.top = SURFACE;
  world.floor = scenery.floor;
  placeChests();
  diver.place(world.width, world.top);
  motion.invalidate();
}
window.addEventListener('resize', resize);
resize();

// --- data -------------------------------------------------------------------

let retryMs = 1000;

function connect() {
  const source = new EventSource(STREAM_URL);
  source.addEventListener('open', () => {
    retryMs = 1000;
    setConnected(true);
  });
  source.addEventListener('error', () => {
    setConnected(false);
    // EventSource retries a dropped stream by itself, but gives up for good on an HTTP error
    // or a wrong content type; then back off and open a fresh one.
    if (source.readyState === EventSource.CLOSED) {
      setTimeout(connect, retryMs);
      retryMs = Math.min(RECONNECT_MAX_MS, retryMs * 2);
    }
  });
  source.addEventListener('snapshot', e => {
    let agents;
    try {
      agents = JSON.parse(e.data);
    } catch (err) {
      console.warn('Ignoring malformed snapshot', err);
      return;
    }
    apply(agents);
  });
  source.addEventListener('usage', e => {
    try {
      const usage = JSON.parse(e.data);
      const reset = chestFor(usage.source)?.set(usage, bubbles);
      placeChests();
      motion.invalidate(reset);
      scheduleClock();
    } catch (err) {
      console.warn('Ignoring malformed usage', err);
    }
  });
}

function setConnected(live) {
  connectionEl.classList.toggle('live', live);
  connectionEl.title = live ? 'Live' : 'Disconnected — retrying';
}

let agentSignature = '';

function apply(agents) {
  // Heartbeat timestamps and the rolling activity score do not change the visible session state.
  const signature = JSON.stringify(
    agents
      .map(({ lastSeen: _lastSeen, activity: _activity, ...agent }) => agent)
      .sort((a, b) => a.id.localeCompare(b.id)),
  );
  if (signature === agentSignature) return;
  agentSignature = signature;
  const seen = new Set();
  const spent = new Map();

  // Parents first, so a new child can be born at its parent's mouth.
  for (const agent of [...agents].sort((a, b) => a.depth - b.depth)) {
    seen.add(agent.id);
    const fish = world.fish.get(agent.id);
    if (fish) {
      fish.leaving = false;
      const delta = fish.update(agent);
      showChanges(fish, delta);
      spent.set(agent.source, (spent.get(agent.source) ?? 0) + delta.spent);
    } else {
      // A new fish's spending so far is history (a transcript read from the start), not news.
      world.fish.set(agent.id, spawn(agent));
    }
  }

  for (const fish of world.fish.values()) if (!seen.has(fish.id)) fish.leaving = true;

  for (const [source, amount] of spent) chestFor(source)?.spend(amount);
  firstSnapshot = false;
  renderPanel(agents);
  emptyEl.hidden = agents.length > 0;
  motion.invalidate(true);
}

/** Bubbles for tool calls, a red flash for errors, a squeeze and a burst for a compaction. */
function showChanges(fish, delta) {
  if (delta.toolCalls) {
    const [mx, my] = fish.mouth();
    bubbles.spawn(mx, my, Math.min(4, delta.toolCalls), 1.5 + fish.len * 0.03);
  }
  if (delta.errors) fish.flash = 1;
  if (delta.compacted) {
    fish.squeeze = 1;
    bubbles.spawn(fish.x, fish.y, 14, 3);
  }
}

function spawn(agent) {
  const parent = agent.parentId ? world.fish.get(agent.parentId) : null;
  if (parent && !firstSnapshot) {
    const [mx, my] = parent.mouth();
    bubbles.spawn(mx, my, 8, 2.5);
    return new Fish(agent, mx, my, { born: true });
  }
  if (parent) return new Fish(agent, parent.x + (Math.random() - 0.5) * 80, parent.y + (Math.random() - 0.5) * 60);

  const y = world.top + Math.random() * (world.floor - world.top) * 0.7;
  if (firstSnapshot) return new Fish(agent, 80 + Math.random() * (world.width - 160), y);
  const fromLeft = Math.random() < 0.5;
  return new Fish(agent, fromLeft ? -60 : world.width + 60, y);
}

// --- loop -------------------------------------------------------------------

// Clock-only changes need one still frame, not a permanent animation loop.
let clockTimer;
function scheduleClock() {
  clearTimeout(clockTimer);
  if (document.hidden) return;
  const now = Date.now();
  let next = (Math.floor(now / 60_000) + 1) * 60_000;
  for (const chest of chests.values()) {
    for (const deadline of [Date.parse(chest.window?.resetsAt), Date.parse(chest.usage?.asOf) + 30 * 60_000])
      if (deadline > now) next = Math.min(next, deadline);
  }
  clockTimer = setTimeout(
    () => {
      motion.invalidate([...chests.values()].some(chest => chest.resetDue()));
      scheduleClock();
    },
    Math.max(1, next - now),
  );
}

function settle() {
  for (const [id, fish] of world.fish) {
    if (fish.leaving || fish.done) world.fish.delete(id);
    else fish.settle(world);
  }
  for (const chest of chests.values()) chest.settle();
  bubbles.items = [];
  diver.settle(world);
}

function simulate(dt, t) {
  for (const fish of world.fish.values()) fish.step(dt, t, world);
  for (const [id, fish] of world.fish) if (fish.gone) world.fish.delete(id);

  // Busy fish trail the odd bubble even between tool calls.
  for (const fish of world.fish.values())
    if (fish.activity > 0.3 && Math.random() < fish.activity * dt * 0.5) {
      const [mx, my] = fish.mouth();
      bubbles.spawn(mx, my, 1, 1.2);
    }

  for (const chest of chests.values()) chest.step(dt, bubbles);
  diver.step(dt, t, world, bubbles);
  scenery.step(dt, t);
  scenery.emitAmbient(t, bubbles);
  bubbles.step(dt);
}

function render(t) {
  scenery.drawBack(ctx, t);
  for (const chest of chests.values()) chest.draw(ctx, t);
  diver.drawBoat(ctx);
  // Descendants first, so parents swim on top of their school.
  const ordered = [...world.fish.values()].sort((a, b) => b.agent.depth - a.agent.depth || a.len - b.len);
  for (const fish of ordered) fish.draw(ctx, fish.id === highlightId || fish.id === hoverId);
  diver.draw(ctx);
  bubbles.draw(ctx);
  for (const chest of chests.values()) chest.drawFront(ctx);
  scenery.drawFront(ctx);
  for (const chest of chests.values()) chest.drawLabel(ctx);
  for (const fish of ordered) if (fish.isRoot) drawLabel(fish);
}

/** Label → what fits, so a long title is measured once rather than every frame. */
const fittedLabels = new Map();

/**
 * Cuts text to fit with an ellipsis, measured in the current ctx.font. The Schools list truncates
 * with CSS; the canvas has to do it itself. Cut by code point, so an emoji is never split.
 */
function fitText(text, maxWidth) {
  const cached = fittedLabels.get(text);
  if (cached !== undefined) return cached;
  let fitted = text;
  if (ctx.measureText(text).width > maxWidth) {
    const chars = Array.from(text);
    let end = chars.length;
    const cut = n => `${chars.slice(0, n).join('').trimEnd()}…`;
    while (end > 1 && ctx.measureText(cut(end)).width > maxWidth) end--;
    fitted = cut(end);
  }
  if (fittedLabels.size > 200) fittedLabels.clear(); // renamed sessions leave old titles behind
  fittedLabels.set(text, fitted);
  return fitted;
}

function drawLabel(fish) {
  const a = fish.agent;
  const y = fish.y - fish.len * 0.55;
  ctx.save();
  ctx.globalAlpha = fish.opacity * 0.9;
  ctx.font = '600 12px "Segoe UI", system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.shadowColor = 'rgba(0, 0, 0, 0.8)';
  ctx.shadowBlur = 4;
  ctx.fillStyle = fishColor(a.sessionId, 0, 18);
  ctx.fillText(fitText(a.label, LABEL_MAX_WIDTH), fish.x, y - 10);
  ctx.font = '11px "Segoe UI", system-ui, sans-serif';
  ctx.fillStyle = 'rgba(220, 240, 255, 0.75)';
  ctx.fillText(`${tokens(a.contextTokens)} · ${statusText(a.status)}`, fish.x, y + 4);
  ctx.restore();
}

// --- HUD --------------------------------------------------------------------

function renderPanel(agents) {
  const byParent = new Map();
  for (const a of agents) {
    const key = a.parentId ?? '';
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key).push(a);
  }

  const rows = [];
  const walk = (parentId, depth) => {
    for (const a of (byParent.get(parentId) ?? []).sort((x, y) => x.label.localeCompare(y.label))) {
      rows.push(row(a, depth));
      walk(a.id, depth + 1);
    }
  };
  walk('', 0);
  schoolsEl.innerHTML = rows.join('') || '<li class="meta">—</li>';
}

/** Mirrors a hovered fish onto its row, the reverse of hovering a row. */
function markHoveredRow(id) {
  for (const li of schoolsEl.children) li.classList.toggle('hovered', li.dataset.id === id);
}

function row(a, depth) {
  const detail = a.detail ? `<small>${escapeHtml(a.detail)}</small>` : '';
  const classes = [depth && 'child', a.id === hoverId && 'hovered'].filter(Boolean).join(' ');
  // Pulse the sessions the diver comes for. The panel is rebuilt on every snapshot, which restarts
  // CSS animations; a delay taken from the clock keeps every pulsing swatch on one phase across
  // rebuilds instead of jumping back to 0.
  const fish = world.fish.get(a.id);
  const pulse = Boolean(fish && waitsOnYou(fish));
  const swatchStyle = `--swatch:${fishColor(a.sessionId, a.depth)}${
    pulse ? `;animation-duration:${PULSE_MS}ms;animation-delay:-${Math.round(performance.now() % PULSE_MS)}ms` : ''
  }`;
  return `<li class="${classes}" style="--depth:${depth}" data-id="${escapeHtml(a.id)}">
    <span class="swatch${pulse ? ' pulse' : ''}" style="${swatchStyle}"></span>
    <span class="name">${escapeHtml(a.label)}${detail}</span>
    <span class="meta ${STATUS_CLASS.get(a.status) ?? ''}">${tokens(a.contextTokens)}<br>${escapeHtml(statusText(a.status))}</span>
  </li>`;
}

schoolsEl.addEventListener('mouseover', e => {
  highlightId = e.target.closest('li')?.dataset.id ?? null;
  motion.invalidate();
});
schoolsEl.addEventListener('mouseleave', () => {
  highlightId = null;
  motion.invalidate();
});

canvas.addEventListener('mousemove', e => {
  pointer = [e.clientX, e.clientY];
  motion.invalidate();
});
canvas.addEventListener('mouseleave', () => {
  pointer = null;
  motion.invalidate();
});

/** The fish under the pointer, preferring the shallowest generation where they overlap. */
function fishAt([px, py]) {
  let best = null;
  for (const fish of world.fish.values())
    if (fish.hit(px, py) && (!best || fish.agent.depth < best.agent.depth)) best = fish;
  return best;
}

function updateTooltip() {
  // Front first, as drawn: the diver swims over the fish, the chest sits behind them.
  const onDiver = pointer && diver.hit(...pointer);
  const fish = pointer && !onDiver && fishAt(pointer);
  const other = onDiver ? diver : !fish && pointer ? [...chests.values()].find(chest => chest.hit(...pointer)) : null;
  const id = fish ? fish.id : null;
  if (id !== hoverId) markHoveredRow((hoverId = id));
  tooltipEl.hidden = !fish && !other;
  canvas.style.cursor = tooltipEl.hidden ? 'default' : 'pointer';
  if (tooltipEl.hidden) return;

  // Touch the DOM only when the content changes. A fish's changes with each snapshot; the
  // chest's and the diver's also with the clock, so compare their markup instead.
  if (fish && fish.agent !== tooltipFor) {
    tooltipFor = fish.agent;
    markupTooltip = '';
    tooltipEl.innerHTML = tooltipHtml(fish.agent);
  } else if (other) {
    const html = other.tooltipHtml(escapeHtml);
    if (html !== markupTooltip) {
      tooltipFor = null;
      markupTooltip = html;
      tooltipEl.innerHTML = html;
    }
  }
  tooltipEl.style.left = `${Math.min(pointer[0] + TOOLTIP_OFFSET, window.innerWidth - TOOLTIP_ROOM)}px`;
  tooltipEl.style.top = `${pointer[1] + TOOLTIP_OFFSET}px`;
}

function tooltipHtml(a) {
  const generation = a.depth ? ` <span class="dim">· generation ${a.depth}</span>` : '';
  const detail = a.detail ? `<span class="dim">${escapeHtml(a.detail)}</span><br>` : '';
  const lastTool = a.lastTool ? ` <span class="dim">· last tool ${escapeHtml(a.lastTool)}</span>` : '';
  const errors = a.errors ? ` · errors: ${a.errors}` : '';
  const compactions = a.compactions ? ` · compacted ×${a.compactions}` : '';
  return `<strong>${escapeHtml(a.label)}</strong>${generation}<br>
    ${detail}
    Context: ${tokens(a.contextTokens)} tokens<br>
    Status: ${escapeHtml(statusText(a.status))}${a.waitingFor ? ` · ${escapeHtml(a.waitingFor)}` : ''}${lastTool}<br>
    Tool calls: ${a.toolCalls}${errors}${compactions}<br>
    <span class="dim">${escapeHtml(a.source)}</span>`;
}

// --- formatting -------------------------------------------------------------

// A Map, not an object literal, so a status such as 'constructor' cannot hit Object.prototype.
const STATUS_TEXT = new Map([
  ['Working', 'working'],
  ['Idle', 'your turn'],
  ['Waiting', 'needs you'],
  ['Done', 'done'],
  ['Background', 'background work'],
]);
const STATUS_CLASS = new Map([
  ['Working', 'working'],
  ['Waiting', 'wait'],
]);
const HTML_ENTITIES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

function tokens(n) {
  if (n >= 1e6) return `${(n / 1e6).toFixed(2)}M`;
  if (n >= 1e3) return `${(n / 1e3).toFixed(n >= 1e5 ? 0 : 1)}k`;
  return `${n}`;
}

function statusText(s) {
  return STATUS_TEXT.get(s) ?? s;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => HTML_ENTITIES[c]);
}

connect();
motion.invalidate();
scheduleClock();
