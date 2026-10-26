// Boots the tank inside an already-open same-origin page (e.g. /styles.css) with four patches,
// then document.write()s index.html so main.js starts under them. The global object survives
// document.open(), so the patches stay in force.
//
// 1. A pausable clock: after __freeze(), performance.now, Date.now and requestAnimationFrame are
//    virtual, and __step(n, ms) advances them. One screenshot per step gives smooth frames however
//    slow the screenshots are.
// 2. Paced server updates: SSE events received while frozen are queued with their real arrival time
//    and delivered when virtual time catches up. Without this the demo's real-time tool calls pile
//    up against the slowed clock and the tank fills with bubbles.
// 3. CSS animations on the same clock: while frozen, each animation's currentTime is set from virtual
//    time, so the Schools swatch pulse keeps its real speed instead of playing ~28x too fast.
// 4. Pointer on a moving fish (only with HOVER=1): fillText is wrapped to record where each root's
//    "tokens · status" label was drawn, and every frame a mousemove is dispatched just below the
//    label of window.__follow, so the tooltip and the highlighted Schools row stay up.
(async () => {
  const html = await (await fetch('/')).text();
  const nativeNow = performance.now.bind(performance);
  const clock = window.__installCaptureClock();
  const S = (window.__S = { frozen: false, vt0: 0, rt0: 0, pending: [] });
  const NativeES = window.EventSource;
  class PacedEventSource extends EventTarget {
    constructor(url) {
      super();
      this.inner = new NativeES(url);
      for (const type of ['open', 'error', 'snapshot', 'usage', 'message'])
        this.inner.addEventListener(type, e => {
          const ev = new MessageEvent(type, { data: e.data });
          if (S.frozen) S.pending.push({ t: nativeNow(), target: this, ev });
          else this.dispatchEvent(ev);
        });
    }
    get readyState() {
      return this.inner.readyState;
    }
    close() {
      this.inner.close();
    }
  }
  Object.assign(PacedEventSource, { CONNECTING: 0, OPEN: 1, CLOSED: 2 });
  window.EventSource = PacedEventSource;

  window.__freeze = () => {
    clock.freeze();
    S.frozen = true;
    S.vt0 = S.rt0 = clock.vt;
    // Animations running already carry on from where they are, rather than restarting at 0.
    for (const a of document.getAnimations()) born.set(a, clock.vt - (a.currentTime ?? 0));
    syncAnimations();
    return 'frozen';
  };
  // Virtual time each CSS animation was first seen at. main.js recreates the swatch pulses on every
  // panel rebuild with a delay taken from performance.now, so time since creation keeps the phase.
  const born = new WeakMap();
  const syncAnimations = () => {
    for (const a of document.getAnimations()) {
      if (!born.has(a)) born.set(a, clock.vt);
      a.pause();
      a.currentTime = clock.vt - born.get(a);
    }
  };
  // ms defaults to 1/30 s: main.js caps a frame at 50 ms, so bigger steps would slow the motion.
  window.__step = (n = 1, ms = 1000 / 30) => {
    for (let i = 0; i < n; i++) {
      const due = S.rt0 + (clock.vt + ms - S.vt0);
      while (S.pending.length && S.pending[0].t <= due) {
        const p = S.pending.shift();
        p.target.dispatchEvent(p.ev);
      }
      clock.step(ms);
    }
    syncAnimations();
    return S.pending.length;
  };

  window.__labels = {};
  window.__follow = null;
  const fillText = CanvasRenderingContext2D.prototype.fillText;
  let previous = null; // drawLabel() draws the name, then "tokens · status" under it
  CanvasRenderingContext2D.prototype.fillText = function (text, x, y, ...rest) {
    if (typeof text === 'string' && text.includes(' · ') && previous) window.__labels[previous] = { x, y };
    previous = text;
    return fillText.call(this, text, x, y, ...rest);
  };
  const follow = () => {
    const canvas = document.getElementById('tank');
    const at = window.__follow && window.__labels[window.__follow];
    // The status line sits 0.55 x length above the fish's centre; 18 px below it lands on the body.
    if (canvas && at)
      canvas.dispatchEvent(new MouseEvent('mousemove', { clientX: at.x, clientY: at.y + 18, bubbles: true }));
    window.requestAnimationFrame(follow);
  };
  window.requestAnimationFrame(follow);

  document.open();
  document.write(html);
  document.close();
  return 'booted';
})();
