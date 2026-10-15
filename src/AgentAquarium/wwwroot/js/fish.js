import { fishColor } from './colors.js';

const TAU = Math.PI * 2;
const lerp = (a, b, t) => a + (b - a) * Math.min(1, t);
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

// Swimming tunables: speeds in px/s, rates per second, distances in px.
const IDLE_SPEED = 14;
const WORK_SPEED = 45; // a working fish at zero activity…
const WORK_SPEED_PER_ACTIVITY = 150; // …plus this much at full activity
const SPEED_CAP = 1.6; // hard cap, as a multiple of the cruising speed
const SCHOOL_BOOST = 1.4; // children swim faster so they can keep up
const ARRIVAL_RADIUS = 90; // start easing off this close to the target
const STEER_WORKING = 2.2;
const STEER_IDLE = 0.8;
const ALIGNMENT = 0.4;
const SEPARATION = 120;
const FADE_IN = 0.8;
const FADE_OUT = 0.45;
const TURN_RATE = 4;
const LOITER_SPEED = 25; // below this a child in its slot copies its parent's heading
const TOP_MARGIN = 20;

/** Body length in px. sqrt keeps a 1M-token session from dwarfing everything else. */
export function lengthFor(tokens) {
  return 18 + 0.11 * Math.sqrt(Math.max(0, tokens));
}

export class Fish {
  constructor(agent, x, y, { born = false } = {}) {
    this.agent = agent;
    this.x = x;
    this.y = y;
    this.vx = (Math.random() - 0.5) * 20;
    this.vy = (Math.random() - 0.5) * 10;
    this.angle = Math.atan2(this.vy, this.vx);
    this.len = born ? 4 : lengthFor(agent.contextTokens);
    this.phase = Math.random() * TAU;
    this.activity = agent.activity;
    this.opacity = born ? 1 : 0;
    this.flash = 0;
    this.squeeze = 0;
    this.leaving = false;
    this.target = null;
    this.retargetAt = 0;
    this.orbit = Math.random() * TAU;
    this.orbitDir = Math.random() < 0.5 ? -1 : 1;
    this.lane = Math.random();
    this.colors = paletteFor(agent);
  }

  get id() {
    return this.agent.id;
  }
  get isRoot() {
    return !this.agent.parentId;
  }
  get done() {
    return this.agent.status === 'Done';
  }
  get gone() {
    return (this.leaving || this.done) && this.opacity <= 0.01;
  }

  /** Apply a fresh snapshot; returns what changed so the tank can spawn effects. */
  update(agent) {
    const prev = this.agent;
    this.agent = agent;
    if (agent.sessionId !== prev.sessionId || agent.depth !== prev.depth) this.colors = paletteFor(agent);
    return {
      toolCalls: Math.max(0, agent.toolCalls - prev.toolCalls),
      errors: Math.max(0, agent.errors - prev.errors),
      compacted: agent.compactions > prev.compactions,
    };
  }

  step(dt, now, world) {
    const a = this.agent;
    this.activity = lerp(this.activity, a.activity, dt * 2);
    this.len = lerp(this.len, lengthFor(a.contextTokens), dt * 1.2);
    this.flash = Math.max(0, this.flash - dt * 1.5);
    this.squeeze = Math.max(0, this.squeeze - dt * 1.2);

    const fading = this.leaving || this.done;
    this.opacity = fading ? Math.max(0, this.opacity - dt * FADE_OUT) : Math.min(1, this.opacity + dt * FADE_IN);

    const working = a.status === 'Working';
    const maxSpeed = working ? WORK_SPEED + WORK_SPEED_PER_ACTIVITY * this.activity : IDLE_SPEED;
    // A fading child leaves its school and swims off on its own.
    const leader = !fading && a.parentId ? world.fish.get(a.parentId) : null;

    const [tx, ty] = leader ? this.schoolSlot(leader, dt) : this.wander(now, world, working, fading);
    let [ax, ay] = this.seek(tx, ty, maxSpeed * (leader ? SCHOOL_BOOST : 1));

    if (leader) {
      // Alignment: swim the way the parent swims.
      ax += (leader.vx - this.vx) * ALIGNMENT;
      ay += (leader.vy - this.vy) * ALIGNMENT;
    }
    const [sx, sy] = this.separation(world.fish);

    const steer = working ? STEER_WORKING : STEER_IDLE;
    this.vx += (ax + sx) * dt * steer;
    this.vy += (ay + sy) * dt * steer;

    const v = Math.hypot(this.vx, this.vy);
    const cap = maxSpeed * SPEED_CAP;
    if (v > cap) {
      this.vx *= cap / v;
      this.vy *= cap / v;
    }

    this.x += this.vx * dt;
    this.y += this.vy * dt;
    if (!fading) this.y = clamp(this.y, world.top + TOP_MARGIN, world.floor - this.len * 0.25);

    this.face(leader, v, dt);

    // Tail beat: lazy when idle, frantic when busy.
    this.phase += dt * TAU * (0.8 + this.activity * 5 + v / 70);
  }

  /** Seek with arrival: ease off when close so fish hover rather than overshoot. */
  seek(tx, ty, speed) {
    const dx = tx - this.x;
    const dy = ty - this.y;
    const dist = Math.hypot(dx, dy) || 1;
    const s = speed * clamp(dist / ARRIVAL_RADIUS, 0.15, 1);
    return [(dx / dist) * s - this.vx, (dy / dist) * s - this.vy];
  }

  /** Push away from every nearby fish, more gently in the vertical. */
  separation(fishes) {
    let ax = 0;
    let ay = 0;
    for (const other of fishes.values()) {
      if (other === this) continue;
      const ox = this.x - other.x;
      const oy = this.y - other.y;
      const d = Math.hypot(ox, oy);
      const room = (this.len + other.len) * 0.45;
      if (d > 0 && d < room) {
        const push = ((room - d) / room) * SEPARATION;
        ax += (ox / d) * push;
        ay += (oy / d) * push * 0.6;
      }
    }
    return [ax, ay];
  }

  /**
   * Face where we swim; a child loitering in its slot faces the way its parent does,
   * otherwise small corrections make it bob nose-up and nose-down.
   */
  face(leader, v, dt) {
    const loitering = leader && v < LOITER_SPEED;
    if (v <= 3 && !loitering) return;
    const want = loitering ? leader.angle : Math.atan2(this.vy, this.vx);
    const diff = want - this.angle;
    this.angle += Math.atan2(Math.sin(diff), Math.cos(diff)) * Math.min(1, dt * TURN_RATE);
  }

  /** A spot behind and beside the parent, drifting slowly so the school breathes. */
  schoolSlot(parent, dt) {
    this.orbit += dt * (0.25 + this.activity * 0.5) * this.orbitDir;
    const back = parent.len * 0.6 + this.len * 0.6 + 18 + this.lane * 40;
    const side = Math.sin(this.orbit) * (parent.len * 0.5 + 30 + this.lane * 25);
    const ca = Math.cos(parent.angle);
    const sa = Math.sin(parent.angle);
    return [parent.x - ca * back - sa * side, parent.y - sa * back + ca * side];
  }

  wander(now, world, working, fading) {
    if (fading) {
      // Swim off toward the nearest side wall.
      const left = this.x < world.width / 2;
      return [left ? -300 : world.width + 300, this.y - 40];
    }
    const arrived = this.target && Math.hypot(this.target[0] - this.x, this.target[1] - this.y) < 40;
    if (!this.target || arrived || now > this.retargetAt) {
      const margin = 80;
      const x = margin + Math.random() * (world.width - margin * 2);
      // Idle fish loiter near the bottom; working fish roam the whole tank.
      const [lo, hi] = working ? [0.12, 0.72] : [0.72, 0.9];
      const y = world.top + (world.floor - world.top) * (lo + Math.random() * (hi - lo));
      this.target = [x, y];
      this.retargetAt = now + (working ? 4 : 12) + Math.random() * 6;
    }
    return this.target;
  }

  hit(px, py) {
    return Math.hypot(px - this.x, py - this.y) < Math.max(12, this.len * 0.55);
  }

  draw(ctx, highlighted) {
    const L = this.len * (1 - this.squeeze * 0.12);
    const H = L * 0.36 * (1 + this.squeeze * 0.2);
    const wag = Math.sin(this.phase);
    const tail = [-L * 0.44, wag * L * 0.04];

    ctx.save();
    ctx.globalAlpha = this.opacity;
    ctx.translate(this.x, this.y);
    ctx.rotate(this.angle);
    if (Math.cos(this.angle) < 0) ctx.scale(1, -1); // keep the dorsal fin up

    this.applyGlow(ctx, highlighted);
    this.drawTail(ctx, L, H, wag, tail);
    this.drawDorsalFin(ctx, L, H);
    this.drawBody(ctx, L, H, tail);
    ctx.shadowBlur = 0;
    if (this.isRoot) this.drawBands(ctx, L, H);
    this.drawPectoralFin(ctx, L, H);
    this.drawEye(ctx, L, H);

    ctx.restore();
  }

  /** Parents glow; the glow pulses harder the busier they are. */
  applyGlow(ctx, highlighted) {
    if (this.flash > 0) {
      ctx.shadowColor = `rgba(255, 70, 90, ${this.flash})`;
      ctx.shadowBlur = 30 * this.flash;
    } else if (highlighted) {
      ctx.shadowColor = 'white';
      ctx.shadowBlur = 22;
    } else if (this.isRoot) {
      ctx.shadowColor = this.colors.glow;
      ctx.shadowBlur = 8 + this.activity * 18;
    }
  }

  drawTail(ctx, L, H, wag, [tailX, tailY]) {
    const spread = this.isRoot ? 0.8 : 0.65;
    ctx.save();
    ctx.translate(tailX, tailY);
    ctx.rotate(wag * 0.45);
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.quadraticCurveTo(-L * 0.15, -H * 0.2, -L * 0.34, -H * spread);
    ctx.quadraticCurveTo(-L * 0.22, 0, -L * 0.34, H * spread);
    ctx.quadraticCurveTo(-L * 0.15, H * 0.2, 0, 0);
    ctx.fillStyle = this.colors.tail;
    ctx.fill();
    ctx.restore();
  }

  /** Tall and sail-like on parents, a nub on descendants. */
  drawDorsalFin(ctx, L, H) {
    ctx.beginPath();
    ctx.moveTo(L * 0.18, -H * 0.42);
    if (this.isRoot) ctx.quadraticCurveTo(-L * 0.02, -H * 1.35, -L * 0.32, -H * 0.3);
    else ctx.quadraticCurveTo(-L * 0.02, -H * 0.85, -L * 0.22, -H * 0.32);
    ctx.closePath();
    ctx.fillStyle = this.colors.dorsal;
    ctx.fill();
  }

  /** Leaves the body outline as the current path, for drawBands to clip to. */
  drawBody(ctx, L, H, [tailX, tailY]) {
    const grad = ctx.createLinearGradient(0, -H * 0.5, 0, H * 0.5);
    grad.addColorStop(0, this.colors.back);
    grad.addColorStop(0.55, this.colors.body);
    grad.addColorStop(1, this.colors.belly);
    ctx.beginPath();
    ctx.moveTo(L * 0.5, 0);
    ctx.bezierCurveTo(L * 0.42, -H * 0.72, -L * 0.12, -H * 0.62, tailX, tailY);
    ctx.bezierCurveTo(-L * 0.12, H * 0.58, L * 0.42, H * 0.66, L * 0.5, 0);
    ctx.fillStyle = grad;
    ctx.fill();
  }

  /** Parents wear two pale bands, so a school's leader reads at a glance. */
  drawBands(ctx, L, H) {
    ctx.save();
    ctx.clip();
    ctx.fillStyle = 'rgba(255, 255, 255, 0.22)';
    for (const bx of [L * 0.18, -L * 0.12]) {
      ctx.beginPath();
      ctx.ellipse(bx, 0, L * 0.045, H * 0.6, 0, 0, TAU);
      ctx.fill();
    }
    ctx.restore();
  }

  /** Paddles with the tail. */
  drawPectoralFin(ctx, L, H) {
    ctx.save();
    ctx.translate(L * 0.12, H * 0.14);
    ctx.rotate(0.6 + Math.sin(this.phase * 1.3) * 0.35);
    ctx.beginPath();
    ctx.ellipse(-L * 0.06, 0, L * 0.09, H * 0.12, 0, 0, TAU);
    ctx.fillStyle = this.colors.fin;
    ctx.fill();
    ctx.restore();
  }

  drawEye(ctx, L, H) {
    const er = Math.max(1.4, L * 0.045);
    ctx.beginPath();
    ctx.arc(L * 0.3, -H * 0.12, er, 0, TAU);
    ctx.fillStyle = '#f4fbff';
    ctx.fill();
    ctx.beginPath();
    ctx.arc(L * 0.31, -H * 0.12, er * 0.55, 0, TAU);
    ctx.fillStyle = '#071521';
    ctx.fill();
  }

  /** Mouth position in world space, for bubbles. */
  mouth() {
    return [this.x + Math.cos(this.angle) * this.len * 0.5, this.y + Math.sin(this.angle) * this.len * 0.5];
  }
}

/** A fish's colours only depend on its session and generation, so build the strings once, not every frame. */
function paletteFor({ sessionId, depth }) {
  const col = (dl = 0, ds = 0, alpha = 1) => fishColor(sessionId, depth, dl, ds, alpha);
  return {
    glow: col(10),
    tail: col(-6, 0, 0.85),
    dorsal: col(-8, 0, 0.9),
    back: col(12),
    body: col(0),
    belly: col(22, -25),
    fin: col(8, 0, 0.75),
  };
}
