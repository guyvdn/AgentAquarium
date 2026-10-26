// Runs in the tank iframe before main.js. Makes the tank a deterministic function of the frames
// the director feeds it: seeded Math.random, a virtual clock that only moves on __tick(), and an
// EventSource whose events come from the director's script instead of the server.
(() => {
  let seed = 20261022;
  Math.random = () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };

  const clock = window.__installCaptureClock({ frozen: true, date: Date.parse('2026-10-26T11:12:00+01:00') - 1000 });
  const S = (window.__S = {
    get vt() {
      return clock.vt;
    },
    es: null,
  });

  class ScriptedEventSource extends EventTarget {
    constructor() {
      super();
      this.readyState = 1;
      S.es = this;
    }
    close() {}
  }
  Object.assign(ScriptedEventSource, { CONNECTING: 0, OPEN: 1, CLOSED: 2 });
  window.EventSource = ScriptedEventSource;

  window.__emit = (type, data) => S.es?.dispatchEvent(new MessageEvent(type, { data: JSON.stringify(data) }));
  window.__tick = (ms = 1000 / 30) => {
    const ticks = Math.ceil(ms / (1000 / 120));
    for (let i = 0; i < ticks; i++) clock.step(ms / ticks);
  };
  // CSS animations (the Schools swatch pulse) follow the virtual clock too.
  const born = new WeakMap();
  window.__syncAnimations = () => {
    for (const a of document.getAnimations()) {
      if (!born.has(a)) born.set(a, S.vt);
      a.pause();
      a.currentTime = S.vt - born.get(a);
    }
  };
})();
