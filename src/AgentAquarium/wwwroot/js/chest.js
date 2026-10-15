// The treasure chest: the plan's 5-hour limit. The gold is what is left of it, the lid opens
// as far as there is gold to show, and spending lets coins float away.

import { time } from './time.js';

const TAU = Math.PI * 2;
const WIDTH = 100;
const BODY = 50; // body height; the bottom part sinks into the sand
const LID = 24;
const SINK = 16; // px of the body below the floor line
const MAX_LID_ANGLE = 1.9; // rad, wide open
const EASE = 2.5; // per second; how quickly the gold and lid follow a new number
const STALE_AFTER_MS = 30 * 60_000;
const LOW = 0.3; // remaining fraction below which the gold turns amber
const CRITICAL = 0.1; // ... and red
const NEW_WINDOW_MS = 30 * 60_000; // a reset time this much later is a new window; smaller shifts are jitter between fetches
const SAME_RESET_MS = 60_000;
const RESET_DROP = 15; // percentage points; without reset times, a drop this large means a new window
const COIN_LIFE = 3.5; // s

// Coin positions on the heap, as fractions of its width and height; a coin shows once the heap reaches it.
const HEAP_COINS = Array.from({ length: 18 }, (_, i) => ({ u: ((i * 0.618) % 1) * 0.8 + 0.1, v: (i + 0.5) / 18 }));

export class Chest {
  constructor() {
    this.usage = null;
    this.shown = 0; // remaining fraction as drawn, eased towards the real one
    this.celebrated = null; // reset time (ms) of the window whose refill has been shown
    this.coins = [];
    this.sparkles = [];
    this.burst = 0;
    this.angle = 0; // lid, as last drawn
    this.x = 0;
    this.y = 0;
  }

  place(width, floor) {
    this.x = Math.max(WIDTH, width * 0.12);
    this.y = floor + SINK; // bottom of the body
  }

  /** A new usage report. Spending in the same window releases coins; a new window refills with a burst. */
  set(usage, bubbles) {
    const prev = this.window;
    this.usage = usage;
    const w = this.window;
    if (!w) return;
    if (!prev) {
      // Nothing to animate on the first report, and a reset that already passed is old news.
      if (resetPassed(w)) this.celebrated = time(w.resetsAt);
      return;
    }

    const newWindow =
      prev.resetsAt && w.resetsAt
        ? time(w.resetsAt) - time(prev.resetsAt) > NEW_WINDOW_MS
        : prev.usedPercent - w.usedPercent >= RESET_DROP;
    if (newWindow) {
      if (!prev.resetsAt || !sameReset(this.celebrated, time(prev.resetsAt))) this.celebrate(bubbles);
    } else if (w.usedPercent > prev.usedPercent) {
      this.release(Math.min(8, Math.ceil(w.usedPercent - prev.usedPercent)));
    }
  }

  celebrate(bubbles) {
    this.burst = 1;
    bubbles.spawn(this.x, this.y - BODY, 16, 3);
  }

  /** The 5-hour window, or null when no source has reported one. */
  get window() {
    return this.usage?.fiveHour ?? null;
  }

  /** Fraction of the 5-hour window left, 0–1. A window whose reset time has passed is full again. */
  remaining(now = Date.now()) {
    const w = this.window;
    if (!w) return null;
    if (resetPassed(w, now)) return 1;
    return clamp01(1 - w.usedPercent / 100);
  }

  /** The numbers may no longer be true: old, or the window has reset since they were taken. */
  stale(now = Date.now()) {
    const w = this.window;
    if (!w) return false;
    return now - time(this.usage.asOf) > STALE_AFTER_MS || resetPassed(w, now);
  }

  step(dt, bubbles) {
    // The window resets on the clock, not when the next report arrives (which can be 20 minutes later).
    const w = this.window;
    if (w && resetPassed(w) && !sameReset(this.celebrated, time(w.resetsAt))) {
      this.celebrated = time(w.resetsAt);
      this.celebrate(bubbles);
    }

    const target = this.remaining() ?? 0;
    this.shown += (target - this.shown) * Math.min(1, dt * EASE);
    this.burst = Math.max(0, this.burst - dt * 0.6);

    for (const c of this.coins) {
      c.age += dt;
      c.y -= c.v * dt;
      c.x += Math.sin(c.age * 2 + c.phase) * 12 * dt;
      c.spin += dt * c.spinRate;
    }
    this.coins = this.coins.filter(c => c.age < COIN_LIFE);

    // Gold glints; a reset makes it sparkle hard for a moment.
    const rate = this.shown * 3 + this.burst * 30;
    if (this.shown > 0.02 && Math.random() < rate * dt) {
      const coin = HEAP_COINS[Math.floor(Math.random() * HEAP_COINS.length)];
      if (coin.v <= this.shown || this.burst > 0) {
        const [x, y] = this.heapPoint(coin);
        this.sparkles.push({ x, y, age: 0, size: 2 + Math.random() * 3 });
      }
    }
    for (const s of this.sparkles) s.age += dt;
    this.sparkles = this.sparkles.filter(s => s.age < 0.8);
  }

  release(count) {
    const top = this.heapTop();
    for (let i = 0; i < count; i++)
      this.coins.push({
        x: this.x + (Math.random() - 0.5) * WIDTH * 0.5,
        y: top,
        v: 30 + Math.random() * 25,
        age: -i * 0.15, // a short trickle rather than one clump
        phase: Math.random() * TAU,
        spin: Math.random() * TAU,
        spinRate: 4 + Math.random() * 4,
      });
  }

  heapHeight() {
    return this.shown * 34;
  }

  heapTop() {
    return this.y - BODY - this.heapHeight();
  }

  /** Where a heap coin sits, following the heap's rounded top. */
  heapPoint({ u, v }) {
    const half = WIDTH / 2 - 6;
    const x = this.x - half + u * half * 2;
    const dome = Math.sin(u * Math.PI); // the heap is highest in the middle
    return [x, this.y - BODY - v * this.heapHeight() * dome];
  }

  /** Behind the fish. */
  draw(ctx, t) {
    if (!this.window) return; // no plan usage, no chest
    const stale = this.stale();
    const remaining = this.shown;
    const gold = goldColor(remaining);

    ctx.save();
    if (stale) ctx.filter = 'saturate(0.3) brightness(0.8)';

    // Glow: brighter with more gold, pulsing when nearly spent.
    const pulse = remaining < CRITICAL ? 0.5 + 0.5 * Math.sin(t * 4) : 1;
    const glowR = 70 + remaining * 50;
    const glow = ctx.createRadialGradient(this.x, this.y - BODY, 0, this.x, this.y - BODY, glowR);
    glow.addColorStop(0, `hsla(${gold.h} ${gold.s}% 60% / ${(0.12 + remaining * 0.22 + this.burst * 0.4) * pulse})`);
    glow.addColorStop(1, `hsla(${gold.h} ${gold.s}% 60% / 0)`);
    ctx.fillStyle = glow;
    ctx.fillRect(this.x - glowR, this.y - BODY - glowR, glowR * 2, glowR * 2);

    const left = this.x - WIDTH / 2;
    const top = this.y - BODY;
    const angle = Math.min(MAX_LID_ANGLE, 0.15 + remaining * MAX_LID_ANGLE);
    this.angle = angle;

    // Inside of the chest, seen past the open lid.
    if (angle > 0.05) {
      ctx.fillStyle = '#1e1208';
      ctx.fillRect(left + 3, top - 6, WIDTH - 6, 8);
    }

    this.drawHeap(ctx, gold);
    this.drawBody(ctx, left, top);
    this.drawLid(ctx, left, top, angle);
    ctx.restore();
  }

  drawHeap(ctx, gold) {
    const h = this.heapHeight();
    if (h < 0.5) return;
    const half = WIDTH / 2 - 4;
    const base = this.y - BODY + 2;
    ctx.beginPath();
    ctx.moveTo(this.x - half, base);
    ctx.quadraticCurveTo(this.x, base - h * 2, this.x + half, base);
    ctx.closePath();
    ctx.fillStyle = `hsl(${gold.h} ${gold.s}% 45%)`;
    ctx.fill();

    for (const coin of HEAP_COINS) {
      if (coin.v > this.shown) continue;
      const [x, y] = this.heapPoint(coin);
      ctx.beginPath();
      ctx.ellipse(x, y, 4, 2.4, 0, 0, TAU);
      ctx.fillStyle = `hsl(${gold.h} ${gold.s}% 62%)`;
      ctx.fill();
      ctx.strokeStyle = `hsl(${gold.h} ${gold.s}% 35%)`;
      ctx.lineWidth = 0.8;
      ctx.stroke();
    }
  }

  drawBody(ctx, left, top) {
    const wood = ctx.createLinearGradient(0, top, 0, this.y);
    wood.addColorStop(0, '#7a4a22');
    wood.addColorStop(1, '#3f2410');
    ctx.fillStyle = wood;
    roundRect(ctx, left, top, WIDTH, BODY, 3);
    ctx.fill();

    // Planks
    ctx.strokeStyle = 'rgba(30, 15, 5, 0.6)';
    ctx.lineWidth = 1;
    for (let y = top + 11; y < this.y; y += 11) {
      ctx.beginPath();
      ctx.moveTo(left + 2, y);
      ctx.lineTo(left + WIDTH - 2, y);
      ctx.stroke();
    }

    // Iron bands and the lock
    ctx.fillStyle = '#5c5a52';
    for (const bx of [left + 8, left + WIDTH - 14]) ctx.fillRect(bx, top, 6, BODY);
    ctx.fillStyle = '#c9a23a';
    roundRect(ctx, this.x - 6, top + 2, 12, 14, 2);
    ctx.fill();
    ctx.fillStyle = '#2a1a0a';
    ctx.beginPath();
    ctx.arc(this.x, top + 8, 2, 0, TAU);
    ctx.fill();
  }

  /** Hinged at the back right; opens up and over to the right. */
  drawLid(ctx, left, top, angle) {
    ctx.save();
    ctx.translate(left + WIDTH, top);
    ctx.rotate(angle);
    ctx.translate(-WIDTH, 0);

    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(0, -LID * 0.45);
    ctx.quadraticCurveTo(WIDTH / 2, -LID * 1.45, WIDTH, -LID * 0.45);
    ctx.lineTo(WIDTH, 0);
    ctx.closePath();
    const wood = ctx.createLinearGradient(0, -LID, 0, 0);
    wood.addColorStop(0, '#8a5628');
    wood.addColorStop(1, '#5a3416');
    ctx.fillStyle = wood;
    ctx.fill();
    ctx.strokeStyle = 'rgba(30, 15, 5, 0.7)';
    ctx.lineWidth = 1;
    ctx.stroke();

    ctx.fillStyle = '#5c5a52';
    for (const bx of [8, WIDTH - 14]) ctx.fillRect(bx, -LID * 0.95, 6, LID * 0.95);
    ctx.restore();
  }

  /** In front of the fish: escaping coins and glints. */
  drawFront(ctx) {
    for (const c of this.coins) {
      if (c.age < 0) continue;
      const alpha = Math.min(1, (COIN_LIFE - c.age) / 1.2);
      ctx.beginPath();
      ctx.ellipse(c.x, c.y, Math.max(0.6, Math.abs(Math.cos(c.spin)) * 4.5), 4.5, 0, 0, TAU);
      ctx.fillStyle = `hsla(45 90% 60% / ${alpha})`;
      ctx.fill();
      ctx.strokeStyle = `hsla(40 80% 35% / ${alpha})`;
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    ctx.strokeStyle = 'rgba(255, 250, 220, 0.9)';
    ctx.lineWidth = 1.2;
    for (const s of this.sparkles) {
      const r = s.size * Math.sin((s.age / 0.8) * Math.PI);
      ctx.beginPath();
      ctx.moveTo(s.x - r, s.y);
      ctx.lineTo(s.x + r, s.y);
      ctx.moveTo(s.x, s.y - r);
      ctx.lineTo(s.x, s.y + r);
      ctx.stroke();
    }
  }

  /** Over the sand, like the fish labels. */
  drawLabel(ctx) {
    const w = this.window;
    if (!w) return;
    const remaining = this.remaining();
    const stale = this.stale();
    const y = this.labelTop();

    ctx.save();
    ctx.textAlign = 'center';
    ctx.shadowColor = 'rgba(0, 0, 0, 0.8)';
    ctx.shadowBlur = 4;
    ctx.font = '600 12px "Segoe UI", system-ui, sans-serif';
    const gold = goldColor(this.shown); // the colour the chest is drawn in, so the two agree
    ctx.fillStyle = stale ? 'rgba(200, 210, 220, 0.75)' : `hsl(${gold.h} ${gold.s}% 65%)`;
    ctx.fillText(`${Math.round(remaining * 100)}% left`, this.x, y - 10);
    ctx.font = '11px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = 'rgba(220, 240, 255, 0.75)';
    ctx.fillText(this.subtitle(), this.x, y + 4);
    ctx.restore();
  }

  subtitle(now = Date.now()) {
    const w = this.window;
    if (resetPassed(w, now)) return 'reset · awaiting update';
    if (this.stale(now)) return `as of ${clock(this.usage.asOf)}`;
    return w.resetsAt ? `↻ ${clock(w.resetsAt)}` : '';
  }

  /** Clear of the open lid, which stands up to a chest's width above the hinge. */
  labelTop() {
    return this.y - BODY - Math.max(LID * 1.2, WIDTH * Math.sin(this.angle)) - 14;
  }

  hit(px, py) {
    return Boolean(this.window) && Math.abs(px - this.x) < WIDTH / 2 + 10 && py > this.labelTop() - 20 && py < this.y;
  }

  tooltipHtml(escapeHtml) {
    const u = this.usage;
    const line = (name, w, day) =>
      w
        ? `${name}: ${Math.round(w.usedPercent)}% used${w.resetsAt ? ` <span class="dim">· resets ${day ? weekdayClock(w.resetsAt) : clock(w.resetsAt)}</span>` : ''}<br>`
        : '';
    return `<strong>5-hour limit</strong> <span class="dim">· gold = what is left</span><br>
      ${line('5 hours', u.fiveHour, false)}
      ${line('7 days', u.sevenDay, true)}
      <span class="dim">${escapeHtml(u.source)} · as of ${clock(u.asOf)}${this.stale() ? ' (stale)' : ''}</span>`;
  }
}

function goldColor(remaining) {
  if (remaining === null || remaining >= LOW) return { h: 45, s: 90 };
  if (remaining >= CRITICAL) return { h: 30, s: 90 };
  return { h: 8, s: 80 };
}

function clamp01(v) {
  return Math.max(0, Math.min(1, v));
}

function resetPassed(w, now = Date.now()) {
  return Boolean(w.resetsAt) && time(w.resetsAt) <= now;
}

function sameReset(a, b) {
  return a !== null && Math.abs(a - b) < SAME_RESET_MS;
}

function clock(iso) {
  return new Date(time(iso)).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function weekdayClock(iso) {
  return new Date(time(iso)).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' });
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, r);
}
