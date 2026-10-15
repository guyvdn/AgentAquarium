// The diver: you, the one thing agents wait on. It swims to whoever has waited on you longest,
// blocked mid-turn before merely finished, and climbs back to the boat when nobody waits.

import { time } from './time.js';

const TAU = Math.PI * 2;
const SPEED = 75; // px/s; comfortably faster than an idle fish, so it can catch one
const ARRIVAL_RADIUS = 70;
const STEER = 1.8;
const STAND_OFF = 34; // px between the diver and the fish it attends, past the fish's half length
const REST_DEPTH = 34; // px below the surface line, holding the ladder
const BREATH_EVERY = 1.8; // s
const TURN_RATE = 3;
const LIST_MAX = 8;

export class Diver {
  constructor() {
    this.x = 0;
    this.y = 0;
    this.vx = 0;
    this.vy = 0;
    this.facing = 1; // 1 = head to the right
    this.pitch = 0; // rad, nose down positive, in the facing frame
    this.kick = 0;
    this.breathAt = 0;
    this.queue = []; // fish waiting on you, most overdue first
    this.boat = null;
  }

  place(width, top) {
    const first = !this.boat;
    this.boat = { x: Math.max(260, width * 0.42), top };
    if (first) [this.x, this.y] = this.restPoint();
  }

  /** Where it waits for work: on the ladder, just under the surface. */
  restPoint() {
    return [this.boat.x + 22, this.boat.top + REST_DEPTH];
  }

  step(dt, t, world, bubbles) {
    this.queue = waitingOnYou(world.fish);
    const fish = this.queue[0];

    let tx;
    let ty;
    if (fish) {
      // Stay on whichever side of it we are already on, so the diver does not circle it.
      const side = this.x < fish.x ? -1 : 1;
      tx = fish.x + side * (fish.len * 0.5 + STAND_OFF);
      ty = Math.max(world.top + 20, Math.min(world.floor - 20, fish.y + 6));
    } else {
      [tx, ty] = this.restPoint();
    }

    const dx = tx - this.x;
    const dy = ty - this.y;
    const dist = Math.hypot(dx, dy) || 1;
    const speed = SPEED * Math.min(1, dist / ARRIVAL_RADIUS);
    this.vx += ((dx / dist) * speed - this.vx) * Math.min(1, dt * STEER);
    this.vy += ((dy / dist) * speed - this.vy) * Math.min(1, dt * STEER);
    this.x += this.vx * dt;
    this.y += this.vy * dt;

    const v = Math.hypot(this.vx, this.vy);
    const resting = !fish && dist < 12;
    if (v > 15) this.facing = this.vx < 0 ? -1 : 1;
    else if (fish) this.facing = fish.x < this.x ? -1 : 1; // hovering: face the fish

    // Swim level when travelling, head up on the ladder.
    const want = resting
      ? -1.25
      : v > 15
        ? Math.max(-0.6, Math.min(0.6, Math.atan2(this.vy, Math.abs(this.vx))))
        : -0.15;
    this.pitch += (want - this.pitch) * Math.min(1, dt * TURN_RATE);
    this.kick += dt * TAU * (resting ? 0.15 : 0.5 + v / 60);

    if (t > this.breathAt) {
      const [hx, hy] = this.head();
      bubbles.spawn(hx, hy - 4, 3, 1.4);
      this.breathAt = t + BREATH_EVERY * (0.8 + Math.random() * 0.4);
    }
  }

  /** World position of the mask, where the bubbles come from. */
  head() {
    const c = Math.cos(this.pitch);
    const s = Math.sin(this.pitch);
    return [this.x + this.facing * (24 * c + 2 * s), this.y + 24 * s - 2 * c];
  }

  /** Behind the fish: the boat's hull at the surface and its ladder. */
  drawBoat(ctx) {
    if (!this.boat) return;
    const { x, top } = this.boat;
    ctx.save();
    // Hull, seen from below: only the keel shows under the surface.
    ctx.beginPath();
    ctx.moveTo(x - 90, 0);
    ctx.quadraticCurveTo(x - 70, 22, x - 30, 24);
    ctx.lineTo(x + 50, 24);
    ctx.quadraticCurveTo(x + 85, 22, x + 100, 0);
    ctx.closePath();
    const hull = ctx.createLinearGradient(0, 0, 0, 24);
    hull.addColorStop(0, '#0e2231');
    hull.addColorStop(1, '#1d3a4c');
    ctx.fillStyle = hull;
    ctx.fill();

    // Ladder
    const left = x + 14;
    const right = x + 30;
    const bottom = top + REST_DEPTH + 30;
    ctx.strokeStyle = 'rgba(190, 205, 215, 0.55)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(left, 18);
    ctx.lineTo(left, bottom);
    ctx.moveTo(right, 18);
    ctx.lineTo(right, bottom);
    for (let y = 30; y < bottom; y += 12) {
      ctx.moveTo(left, y);
      ctx.lineTo(right, y);
    }
    ctx.stroke();
    ctx.restore();
  }

  /** In front of the fish. Drawn head to the right, then flipped and pitched. */
  draw(ctx) {
    ctx.save();
    ctx.translate(this.x, this.y);
    ctx.scale(this.facing, 1);
    ctx.rotate(this.pitch);

    const kick = Math.sin(this.kick);
    this.drawLeg(ctx, -3, kick);
    this.drawLeg(ctx, 3, -kick);

    // Tank on the back, hose round to the mouth.
    ctx.fillStyle = '#f2c230';
    ctx.beginPath();
    ctx.roundRect(-8, -14, 24, 8, 4);
    ctx.fill();
    ctx.strokeStyle = '#2b2b2b';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(16, -10);
    ctx.quadraticCurveTo(26, -14, 27, 1);
    ctx.stroke();

    // Wetsuit, with a stripe down the side.
    ctx.fillStyle = '#1b2530';
    ctx.beginPath();
    ctx.roundRect(-8, -7, 28, 13, 6);
    ctx.fill();
    ctx.fillStyle = '#e04848';
    ctx.fillRect(-6, -1, 24, 2.5);

    // Arm, reaching forward.
    ctx.strokeStyle = '#1b2530';
    ctx.lineWidth = 4;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(14, 2);
    ctx.quadraticCurveTo(22, 10, 31, 6);
    ctx.stroke();

    // Head: hood and mask.
    ctx.fillStyle = '#1b2530';
    ctx.beginPath();
    ctx.arc(24, -2, 6.5, 0, TAU);
    ctx.fill();
    ctx.fillStyle = 'rgba(150, 225, 255, 0.85)';
    ctx.beginPath();
    ctx.roundRect(26, -6, 6, 6, 2);
    ctx.fill();
    ctx.fillStyle = 'rgba(255, 255, 255, 0.7)';
    ctx.fillRect(28, -5, 1.5, 3);
    ctx.restore();

    this.drawBadge(ctx);
  }

  drawLeg(ctx, dy, kick) {
    ctx.save();
    ctx.translate(-6, dy);
    ctx.rotate(kick * 0.3);
    ctx.fillStyle = '#1b2530';
    ctx.beginPath();
    ctx.roundRect(-20, -2.5, 22, 5, 2.5);
    ctx.fill();
    // Fin
    ctx.translate(-20, 0);
    ctx.rotate(kick * 0.35);
    ctx.fillStyle = '#16a3a0';
    ctx.beginPath();
    ctx.moveTo(0, -3);
    ctx.lineTo(-17, -6);
    ctx.lineTo(-17, 5);
    ctx.lineTo(0, 3);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  /** How many wait on you; amber when one of them is blocked mid-turn. */
  drawBadge(ctx) {
    const n = this.queue.length;
    if (!n) return;
    const [bx, by] = this.badgePoint();
    ctx.save();
    ctx.beginPath();
    ctx.arc(bx, by, 9, 0, TAU);
    ctx.fillStyle = this.queue.some(blocked) ? '#ffb347' : 'rgba(220, 240, 255, 0.85)';
    ctx.shadowColor = 'rgba(0, 0, 0, 0.6)';
    ctx.shadowBlur = 4;
    ctx.fill();
    ctx.shadowBlur = 0;
    ctx.fillStyle = '#071521';
    ctx.font = '700 11px "Segoe UI", system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(n > 9 ? '9+' : `${n}`, bx, by + 0.5);
    ctx.restore();
  }

  badgePoint() {
    return [this.x, this.y - 28];
  }

  hit(px, py) {
    const [bx, by] = this.badgePoint();
    return Math.hypot(px - this.x, py - this.y) < 30 || Math.hypot(px - bx, py - by) < 12;
  }

  /** The waiting list, most overdue first. Changes with the clock, so callers compare the markup. */
  tooltipHtml(escapeHtml, now = Date.now()) {
    if (!this.queue.length) return '<strong>You</strong><br><span class="dim">Nobody is waiting on you.</span>';
    const rows = this.queue.slice(0, LIST_MAX).map(f => {
      const a = f.agent;
      const why = blocked(f)
        ? `<span class="wait">needs you${a.waitingFor ? ` · ${escapeHtml(a.waitingFor)}` : ''}</span>`
        : 'your turn';
      return `${escapeHtml(a.label)} <span class="dim">·</span> ${why} <span class="dim">· ${ago(time(a.statusSince), now)}</span>`;
    });
    const more =
      this.queue.length > LIST_MAX ? `<br><span class="dim">and ${this.queue.length - LIST_MAX} more</span>` : '';
    return `<strong>You</strong> <span class="dim">· ${this.queue.length} waiting on you</span><br>${rows.join('<br>')}${more}`;
  }
}

/** Blocked mid-turn on you, before finished and waiting for your next move; longest wait first. */
function waitingOnYou(fishes) {
  return [...fishes.values()]
    .filter(f => !f.leaving && !f.done && (blocked(f) || (f.isRoot && f.agent.status === 'Idle')))
    .sort((a, b) => blocked(b) - blocked(a) || time(a.agent.statusSince) - time(b.agent.statusSince));
}

function blocked(fish) {
  return fish.agent.status === 'Waiting';
}

function ago(ms, now) {
  if (!Number.isFinite(ms)) return '?';
  const minutes = Math.floor((now - ms) / 60_000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m`;
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`;
}
