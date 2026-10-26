// A capture-only clock shared by the README and promo workflows. Timers, rAF and cancellation
// all follow virtual time; production modules are left unchanged.
window.__installCaptureClock = ({ frozen = false, time = 1000, date = Date.now() - time } = {}) => {
  const nativeNow = performance.now.bind(performance);
  const nativeDate = Date.now.bind(Date);
  const nativeRaf = requestAnimationFrame.bind(window);
  const nativeCancelRaf = cancelAnimationFrame.bind(window);
  const nativeTimeout = setTimeout.bind(window);
  const nativeClearTimeout = clearTimeout.bind(window);
  const clock = { frozen, vt: time, d0: date, next: 0, frames: new Map(), timers: new Map() };
  const now = () => (clock.frozen ? clock.vt : nativeNow());
  performance.now = now;
  Date.now = () => (clock.frozen ? clock.d0 + clock.vt : nativeDate());
  window.requestAnimationFrame = callback => {
    const id = ++clock.next;
    const frame = { callback, native: null };
    clock.frames.set(id, frame);
    if (!clock.frozen)
      frame.native = nativeRaf(at => {
        if (!clock.frames.delete(id)) return;
        callback(at);
      });
    return id;
  };
  window.cancelAnimationFrame = id => {
    const frame = clock.frames.get(id);
    if (frame?.native !== null) nativeCancelRaf(frame?.native);
    clock.frames.delete(id);
  };
  window.setTimeout = (callback, delay = 0, ...args) => {
    const id = ++clock.next;
    const timer = { callback: () => callback(...args), due: now() + Math.max(0, delay), native: null };
    clock.timers.set(id, timer);
    if (!clock.frozen)
      timer.native = nativeTimeout(() => {
        if (!clock.timers.delete(id)) return;
        timer.callback();
      }, delay);
    return id;
  };
  window.clearTimeout = id => {
    const timer = clock.timers.get(id);
    if (timer?.native !== null) nativeClearTimeout(timer?.native);
    clock.timers.delete(id);
  };
  clock.freeze = () => {
    if (clock.frozen) return;
    clock.vt = nativeNow();
    clock.d0 = nativeDate() - clock.vt;
    clock.frozen = true;
    for (const frame of clock.frames.values()) {
      nativeCancelRaf(frame.native);
      frame.native = null;
    }
    for (const timer of clock.timers.values()) {
      nativeClearTimeout(timer.native);
      timer.native = null;
    }
  };
  clock.step = (ms = 1000 / 30) => {
    if (!clock.frozen) throw new Error('Freeze the capture clock before stepping');
    const end = clock.vt + ms;
    let count = 0;
    for (;;) {
      const next = [...clock.timers].filter(([, timer]) => timer.due <= end).sort((a, b) => a[1].due - b[1].due)[0];
      if (!next) break;
      if (++count > 10000) throw new Error('Capture timer loop did not settle');
      const [id, timer] = next;
      clock.vt = timer.due;
      clock.timers.delete(id);
      timer.callback();
    }
    clock.vt = end;
    const frames = [...clock.frames];
    for (const [id, frame] of frames) if (clock.frames.delete(id)) frame.callback(clock.vt);
  };
  return clock;
};
