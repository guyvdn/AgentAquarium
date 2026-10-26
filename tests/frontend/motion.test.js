import assert from 'node:assert/strict';
import test from 'node:test';
import { MotionLoop } from '../../src/AgentAquarium/wwwroot/js/motion.js';

function harness() {
  let time = 0;
  let nextId = 0;
  const pending = new Map();
  const draws = [];
  const schedule = (callback, delay) => {
    const id = ++nextId;
    pending.set(id, { at: time + delay, callback });
    return id;
  };
  const motion = new MotionLoop((dt, animate) => draws.push({ at: time, dt, animate }), {
    now: () => time,
    requestFrame: callback => schedule(() => callback(time), 1),
    cancelFrame: id => pending.delete(id),
    setTimer: schedule,
    clearTimer: id => pending.delete(id),
  });
  const advance = milliseconds => {
    const end = time + milliseconds;
    for (;;) {
      const entry = [...pending].sort((a, b) => a[1].at - b[1].at)[0];
      if (!entry || entry[1].at > end) break;
      time = entry[1].at;
      pending.delete(entry[0]);
      entry[1].callback();
    }
    time = end;
  };
  return { motion, draws, pending, advance };
}

test('quiet visible scenes keep swimming at a lower capped cadence', () => {
  const h = harness();
  h.motion.invalidate(true);
  h.advance(4000);
  const before = h.draws.length;
  h.advance(1000);
  const idleFrames = h.draws.slice(before);
  assert.ok(idleFrames.length >= 10 && idleFrames.length <= 13);
  assert.ok(idleFrames.every(frame => frame.animate && frame.dt > 0.05));
  assert.ok(idleFrames.reduce((elapsed, frame) => elapsed + frame.dt, 0) >= 0.9);
  assert.equal(h.pending.size, 1);
});

test('continuous updates cannot exceed the animation cap', () => {
  const h = harness();
  for (let i = 0; i < 100; i++) {
    h.motion.invalidate(true);
    h.advance(10);
  }
  assert.ok(h.draws.length <= 25);
  for (let i = 1; i < h.draws.length; i++) assert.ok(h.draws[i].at - h.draws[i - 1].at >= 1000 / 24);
});

test('hiding cancels pending animation and showing does not replay hidden time', () => {
  const h = harness();
  h.motion.invalidate(true);
  h.advance(100);
  h.motion.setHidden(true);
  assert.equal(h.pending.size, 0);
  const count = h.draws.length;
  h.motion.invalidate(true);
  h.advance(60000);
  assert.equal(h.draws.length, count);
  h.motion.setHidden(false);
  h.advance(100);
  assert.equal(h.draws[count].dt, 0);
  assert.ok(h.draws.slice(count).every(frame => frame.dt < 0.25));
  assert.equal(h.draws.at(-1).animate, true);
  assert.equal(h.pending.size, 1);
});

test('reduced motion draws fresh state once without animating', () => {
  const h = harness();
  h.motion.setReduced(true);
  h.motion.invalidate(true);
  h.advance(4000);
  assert.equal(h.draws.length, 1);
  assert.equal(h.draws[0].animate, false);
  assert.equal(h.pending.size, 0);
});

test('changing motion preferences cancels an animation in progress', () => {
  const h = harness();
  h.motion.invalidate(true);
  h.advance(100);
  h.motion.setReduced(true);
  h.advance(100);
  assert.equal(h.draws.at(-1).animate, false);
  assert.equal(h.pending.size, 0);
  h.motion.setReduced(false);
  h.advance(100);
  assert.equal(h.pending.size, 1);
  h.motion.invalidate(true);
  h.advance(100);
  assert.equal(h.draws.at(-1).animate, true);
});

test('pointer invalidations coalesce into a single still frame in reduced motion', () => {
  const h = harness();
  h.motion.setReduced(true);
  for (let i = 0; i < 20; i++) h.motion.invalidate();
  h.advance(100);
  assert.equal(h.draws.length, 1);
  assert.equal(h.draws[0].animate, false);
  assert.equal(h.pending.size, 0);
});
