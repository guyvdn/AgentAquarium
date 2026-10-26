import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import vm from 'node:vm';
import { URL } from 'node:url';

const source = fs.readFileSync(new URL('../../.claude/skills/docs-media/virtual-clock.js', import.meta.url), 'utf8');
function harness(frozen = true) {
  const nativeFrames = new Map();
  const nativeTimers = new Map();
  let id = 0;
  class FakeDate extends Date {
    static now() {
      return 1700000000000;
    }
  }
  const context = {
    Date: FakeDate,
    performance: { now: () => 1000 },
    requestAnimationFrame: callback => {
      nativeFrames.set(++id, callback);
      return id;
    },
    cancelAnimationFrame: key => nativeFrames.delete(key),
    setTimeout: callback => {
      nativeTimers.set(++id, callback);
      return id;
    },
    clearTimeout: key => nativeTimers.delete(key),
  };
  context.window = context;
  vm.runInNewContext(source, context);
  const clock = context.__installCaptureClock({ frozen, time: 1000, date: 10000 });
  return { context, clock, nativeFrames, nativeTimers };
}

test('capture advances render timers and animation frames on the same clock', () => {
  const h = harness();
  const events = [];
  h.context.setTimeout(() => {
    events.push('timer');
    h.context.requestAnimationFrame(now => events.push(now));
  }, 20);
  h.clock.step(10);
  assert.deepEqual(events, []);
  h.clock.step(10);
  assert.deepEqual(events, ['timer', 1020]);
  assert.equal(h.context.performance.now(), 1020);
  assert.equal(h.context.Date.now(), 11020);
});

test('capture cancellation suppresses both timers and frames', () => {
  const h = harness();
  const events = [];
  const timer = h.context.setTimeout(() => events.push('timer'), 10);
  const frame = h.context.requestAnimationFrame(() => events.push('frame'));
  h.context.clearTimeout(timer);
  h.context.cancelAnimationFrame(frame);
  h.clock.step(100);
  assert.deepEqual(events, []);
});

test('freezing transfers pending native callbacks without dropping or duplicating them', () => {
  const h = harness(false);
  const events = [];
  h.context.setTimeout(() => events.push('timer'), 50);
  h.context.requestAnimationFrame(() => events.push('frame'));
  assert.equal(h.nativeFrames.size, 1);
  assert.equal(h.nativeTimers.size, 1);
  h.clock.freeze();
  assert.equal(h.nativeFrames.size, 0);
  assert.equal(h.nativeTimers.size, 0);
  h.clock.step(49);
  assert.deepEqual(events, ['frame']);
  h.clock.step(1);
  assert.deepEqual(events, ['frame', 'timer']);
  h.clock.step(100);
  assert.deepEqual(events, ['frame', 'timer']);
});
