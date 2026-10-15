import { Fish } from './fish.js';
import { Scenery, Bubbles } from './scenery.js';
import { fishColor } from './colors.js';

const STREAM_URL = '/api/agents/stream';
const RECONNECT_MAX_MS = 30_000;
const SURFACE = 60; // px kept clear above the fish, under the title
const MAX_DT = 0.05; // s; a backgrounded tab must not teleport the fish when it comes back
const TOOLTIP_OFFSET = 14;
const TOOLTIP_ROOM = 330; // the tooltip's max-width plus a margin, so it never runs off the right edge

const canvas = document.getElementById('tank');
const ctx = canvas.getContext('2d');
const schoolsEl = document.getElementById('schools');
const tooltipEl = document.getElementById('tooltip');
const emptyEl = document.getElementById('empty');
const connectionEl = document.getElementById('connection');

const scenery = new Scenery();
const bubbles = new Bubbles();
const world = { fish: new Map(), width: 0, height: 0, top: 0, floor: 0 };

let firstSnapshot = true;
let highlightId = null;
let hoverId = null;
let pointer = null;
let tooltipAgent = null; // the agent snapshot the tooltip was last rendered for

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
}

function setConnected(live) {
  connectionEl.classList.toggle('live', live);
  connectionEl.title = live ? 'Live' : 'Disconnected — retrying';
}

function apply(agents) {
  const seen = new Set();

  // Parents first, so a new child can be born at its parent's mouth.
  for (const agent of [...agents].sort((a, b) => a.depth - b.depth)) {
    seen.add(agent.id);
    const fish = world.fish.get(agent.id);
    if (fish) {
      fish.leaving = false;
      showChanges(fish, fish.update(agent));
    } else {
      world.fish.set(agent.id, spawn(agent));
    }
  }

  for (const fish of world.fish.values()) if (!seen.has(fish.id)) fish.leaving = true;

  firstSnapshot = false;
  renderPanel(agents);
  emptyEl.hidden = agents.length > 0;
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

let last = performance.now();

function frame(nowMs) {
  const dt = Math.min(MAX_DT, (nowMs - last) / 1000);
  last = nowMs;
  const t = nowMs / 1000;

  simulate(dt, t);
  render(t);
  updateTooltip();
  requestAnimationFrame(frame);
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

  scenery.step(dt, t);
  scenery.emitAmbient(t, bubbles);
  bubbles.step(dt);
}

function render(t) {
  scenery.drawBack(ctx, t);
  // Descendants first, so parents swim on top of their school.
  const ordered = [...world.fish.values()].sort((a, b) => b.agent.depth - a.agent.depth || a.len - b.len);
  for (const fish of ordered) fish.draw(ctx, fish.id === highlightId || fish.id === hoverId);
  bubbles.draw(ctx);
  scenery.drawFront(ctx);
  for (const fish of ordered) if (fish.isRoot) drawLabel(fish);
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
  ctx.fillText(a.label, fish.x, y - 10);
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

function row(a, depth) {
  const detail = a.detail ? `<small>${escapeHtml(a.detail)}</small>` : '';
  return `<li class="${depth ? 'child' : ''}" style="--depth:${depth}" data-id="${escapeHtml(a.id)}">
    <span class="swatch" style="background:${fishColor(a.sessionId, a.depth)}"></span>
    <span class="name">${escapeHtml(a.label)}${detail}</span>
    <span class="meta ${a.status === 'Working' ? 'working' : ''}">${tokens(a.contextTokens)}<br>${escapeHtml(statusText(a.status))}</span>
  </li>`;
}

schoolsEl.addEventListener('mouseover', e => {
  highlightId = e.target.closest('li')?.dataset.id ?? null;
});
schoolsEl.addEventListener('mouseleave', () => {
  highlightId = null;
});

canvas.addEventListener('mousemove', e => {
  pointer = [e.clientX, e.clientY];
});
canvas.addEventListener('mouseleave', () => {
  pointer = null;
});

/** The fish under the pointer, preferring the shallowest generation where they overlap. */
function fishAt([px, py]) {
  let best = null;
  for (const fish of world.fish.values())
    if (fish.hit(px, py) && (!best || fish.agent.depth < best.agent.depth)) best = fish;
  return best;
}

function updateTooltip() {
  const fish = pointer && fishAt(pointer);
  hoverId = fish ? fish.id : null;
  tooltipEl.hidden = !fish;
  canvas.style.cursor = fish ? 'pointer' : 'default';
  if (!fish) return;

  // Rebuild the markup only when a new snapshot arrives or the pointer moves to another fish.
  if (fish.agent !== tooltipAgent) {
    tooltipAgent = fish.agent;
    tooltipEl.innerHTML = tooltipHtml(fish.agent);
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
    Status: ${escapeHtml(statusText(a.status))}${lastTool}<br>
    Tool calls: ${a.toolCalls}${errors}${compactions}<br>
    <span class="dim">${escapeHtml(a.source)}</span>`;
}

// --- formatting -------------------------------------------------------------

// A Map, not an object literal, so a status such as 'constructor' cannot hit Object.prototype.
const STATUS_TEXT = new Map([
  ['Working', 'working'],
  ['Idle', 'waiting'],
  ['Done', 'done'],
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
requestAnimationFrame(frame);
