// Demand-driven animation: short, capped bursts after data changes, then no scheduled frames.
const FRAME_MS = 1000 / 24;
const ANIMATE_FOR_MS = 3000;
const MAX_DT = 0.05;

export class MotionLoop {
  constructor(draw, platform = {}) {
    this.draw = draw;
    this.now = platform.now ?? (() => performance.now());
    this.requestFrame = platform.requestFrame ?? (callback => requestAnimationFrame(callback));
    this.cancelFrame = platform.cancelFrame ?? (id => cancelAnimationFrame(id));
    this.setTimer = platform.setTimer ?? ((callback, delay) => setTimeout(callback, delay));
    this.clearTimer = platform.clearTimer ?? (id => clearTimeout(id));
    this.hidden = false;
    this.reduced = false;
    this.dirty = false;
    this.wasAnimating = false;
    this.until = 0;
    this.last = this.now();
    this.frame = null;
    this.timer = null;
  }

  invalidate(animate = false) {
    this.dirty = true;
    if (animate && !this.hidden && !this.reduced) this.until = this.now() + ANIMATE_FOR_MS;
    this.schedule();
  }

  setHidden(hidden) {
    this.hidden = hidden;
    this.reset();
  }

  setReduced(reduced) {
    this.reduced = reduced;
    this.reset();
  }

  reset() {
    if (this.frame !== null) this.cancelFrame(this.frame);
    if (this.timer !== null) this.clearTimer(this.timer);
    this.frame = this.timer = null;
    this.until = 0;
    this.wasAnimating = false;
    this.last = this.now();
    this.invalidate();
  }

  schedule() {
    if (this.hidden || this.frame !== null || this.timer !== null) return;
    if (!this.dirty && !this.wasAnimating && this.now() >= this.until) return;
    const delay = this.wasAnimating ? Math.max(0, FRAME_MS - (this.now() - this.last)) : 0;
    if (delay > 0) {
      this.timer = this.setTimer(() => {
        this.timer = null;
        this.request();
      }, delay);
    } else this.request();
  }

  request() {
    this.frame = this.requestFrame(now => {
      this.frame = null;
      if (this.hidden) return;
      const animate = !this.reduced && now < this.until;
      const dt = this.wasAnimating ? Math.min(MAX_DT, (now - this.last) / 1000) : 0;
      this.last = now;
      this.dirty = false;
      this.wasAnimating = animate;
      this.draw(animate ? dt : 0, animate);
      this.schedule();
    });
  }
}
