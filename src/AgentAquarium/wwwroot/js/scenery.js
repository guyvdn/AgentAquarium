// Water, light, sand, weed and drifting particles. Nothing here knows about agents.

const TAU = Math.PI * 2;
const SAND_DEPTH = 70; // px of sand below the tank floor
const LIGHT_SHAFTS = 6;
const LIGHT_DEPTH = 0.85; // shafts fade out at this fraction of the height

export class Scenery {
  constructor() {
    this.weeds = [];
    this.motes = [];
    this.vents = [];
    this.sand = null;
    this.gradients = null;
  }

  resize(width, height, dpr) {
    this.width = width;
    this.height = height;
    this.floor = height - SAND_DEPTH;

    this.weeds = Array.from({ length: Math.round(width / 70) }, () => ({
      x: Math.random() * width,
      h: 60 + Math.random() * 160,
      w: 3 + Math.random() * 4,
      hue: 120 + Math.random() * 50,
      phase: Math.random() * TAU,
    }));
    this.motes = Array.from({ length: Math.round((width * height) / 9000) }, () => ({
      x: Math.random() * width,
      y: Math.random() * height,
      r: Math.random() * 1.4 + 0.3,
      v: Math.random() * 4 + 1,
    }));
    this.vents = Array.from({ length: 3 }, () => ({ x: Math.random() * width, next: 0 }));
    this.sand = this.paintSand(width, height, dpr);
    this.gradients = null; // rebuilt for the new height on the next draw
  }

  /** Gradients depend only on the height, so build them once per resize rather than every frame. */
  buildGradients(ctx) {
    const water = ctx.createLinearGradient(0, 0, 0, this.height);
    water.addColorStop(0, '#0b4f7a');
    water.addColorStop(0.5, '#06304f');
    water.addColorStop(1, '#031524');
    // The shafts' gradient is vertical, so one serves every shaft whatever its x.
    const shaft = ctx.createLinearGradient(0, 0, 0, this.height * LIGHT_DEPTH);
    shaft.addColorStop(0, 'rgba(160, 220, 255, 0.07)');
    shaft.addColorStop(1, 'rgba(160, 220, 255, 0)');
    return { water, shaft };
  }

  /** Motes drift up and sway; they wrap round to the bottom once past the surface. */
  step(dt, t) {
    for (const m of this.motes) {
      m.y -= m.v * dt;
      m.x += Math.sin(t * 0.5 + m.y * 0.02) * 6 * dt;
      if (m.y < 0) m.y = this.height;
    }
  }

  paintSand(width, height, dpr) {
    const c = document.createElement('canvas');
    c.width = width * dpr;
    c.height = height * dpr;
    const g = c.getContext('2d');
    g.scale(dpr, dpr);

    g.beginPath();
    g.moveTo(0, height);
    const surface = x => this.floor + Math.sin(x / 90) * 8 + Math.sin(x / 31) * 3;
    for (let x = 0; x < width; x += 20) g.lineTo(x, surface(x));
    // The last step rarely lands on the edge; finish there, or the corner is cut diagonally.
    g.lineTo(width, surface(width));
    g.lineTo(width, height);
    g.closePath();
    const grad = g.createLinearGradient(0, this.floor - 10, 0, height);
    grad.addColorStop(0, '#6b5a3a');
    grad.addColorStop(1, '#2a2416');
    g.fillStyle = grad;
    g.fill();

    // Pebbles
    for (let i = 0; i < width / 6; i++) {
      const x = Math.random() * width;
      const y = this.floor + 8 + Math.random() * (height - this.floor - 8);
      g.beginPath();
      g.ellipse(x, y, Math.random() * 3 + 1, Math.random() * 2 + 0.6, 0, 0, TAU);
      g.fillStyle = `hsla(${30 + Math.random() * 20} 25% ${25 + Math.random() * 25}% / 0.8)`;
      g.fill();
    }
    return c;
  }

  /** Ambient bubbles rising from vents in the sand. */
  emitAmbient(t, bubbles) {
    for (const v of this.vents) {
      if (t < v.next) continue;
      bubbles.spawn(v.x + (Math.random() - 0.5) * 8, this.floor, 1, 2 + Math.random() * 2);
      v.next = t + 0.4 + Math.random() * 2.5;
    }
  }

  drawBack(ctx, t) {
    const { width: w, height: h } = this;
    this.gradients ??= this.buildGradients(ctx);

    ctx.fillStyle = this.gradients.water;
    ctx.fillRect(0, 0, w, h);

    // Light shafts from the surface, slowly swaying.
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.fillStyle = this.gradients.shaft;
    const bottom = h * LIGHT_DEPTH;
    for (let i = 0; i < LIGHT_SHAFTS; i++) {
      const x = (w / LIGHT_SHAFTS) * (i + 0.5) + Math.sin(t * 0.15 + i * 1.7) * 60;
      const spread = 70 + Math.sin(t * 0.2 + i) * 25;
      ctx.beginPath();
      ctx.moveTo(x - 25, 0);
      ctx.lineTo(x + 25, 0);
      ctx.lineTo(x + spread + 120, bottom);
      ctx.lineTo(x - spread + 60, bottom);
      ctx.closePath();
      ctx.fill();
    }
    ctx.restore();

    // Drifting motes
    ctx.fillStyle = 'rgba(200, 235, 255, 0.25)';
    for (const m of this.motes) {
      ctx.beginPath();
      ctx.arc(m.x, m.y, m.r, 0, TAU);
      ctx.fill();
    }

    // Weed behind the fish
    for (const weed of this.weeds) this.drawWeed(ctx, weed, t);
  }

  drawFront(ctx) {
    ctx.drawImage(this.sand, 0, 0, this.width, this.height);
  }

  drawWeed(ctx, weed, t) {
    const base = this.floor + 6;
    const sway = Math.sin(t * 0.7 + weed.phase) * 18;
    ctx.beginPath();
    ctx.moveTo(weed.x - weed.w, base);
    ctx.quadraticCurveTo(weed.x + sway * 0.5 - weed.w, base - weed.h * 0.6, weed.x + sway, base - weed.h);
    ctx.quadraticCurveTo(weed.x + sway * 0.5 + weed.w, base - weed.h * 0.6, weed.x + weed.w, base);
    ctx.fillStyle = `hsla(${weed.hue} 45% 26% / 0.85)`;
    ctx.fill();
  }
}

export class Bubbles {
  constructor() {
    this.items = [];
  }

  spawn(x, y, count = 1, size = 2.5) {
    for (let i = 0; i < count; i++) {
      this.items.push({
        x: x + (Math.random() - 0.5) * 6,
        y: y + (Math.random() - 0.5) * 6,
        r: size * (0.6 + Math.random() * 0.8),
        v: 25 + Math.random() * 30,
        phase: Math.random() * TAU,
      });
    }
  }

  step(dt) {
    for (const b of this.items) {
      b.y -= b.v * dt;
      b.phase += dt * 3;
      b.x += Math.sin(b.phase) * 0.4;
    }
    this.items = this.items.filter(b => b.y > -10); // gone above the surface
  }

  draw(ctx) {
    ctx.strokeStyle = 'rgba(210, 240, 255, 0.55)';
    ctx.fillStyle = 'rgba(210, 240, 255, 0.08)';
    ctx.lineWidth = 1;
    for (const b of this.items) {
      ctx.beginPath();
      ctx.arc(b.x, b.y, b.r, 0, TAU);
      ctx.fill();
      ctx.stroke();
    }
  }
}
