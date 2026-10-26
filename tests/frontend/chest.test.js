import assert from 'node:assert/strict';
import test from 'node:test';
import { Chest } from '../../src/AgentAquarium/wwwroot/js/chest.js';

const bubbles = { spawn() {} };
const usage = reset => ({
  source: 'codex',
  asOf: new Date().toISOString(),
  fiveHour: { usedPercent: 40, resetsAt: new Date(reset).toISOString() },
});

test('fractional coin credit survives quiet periods', () => {
  const chest = new Chest('Codex');
  chest.set(usage(Date.now() + 3600000), bubbles);
  chest.spend(10000);
  chest.settle();
  assert.equal(chest.owed, 0.5);
  chest.spend(10000);
  assert.equal(chest.owed, 1);
});

test('a usage-only reset asks the caller to animate its celebration', () => {
  const chest = new Chest('Codex');
  const now = Date.now();
  assert.equal(chest.set(usage(now + 60000), bubbles), false);
  assert.equal(chest.set(usage(now + 7200000), bubbles), true);
  assert.equal(chest.burst, 1);
  assert.equal(chest.set(usage(now + 7200000), bubbles), false);
});

test('clock resets wake once, while static views consume the reset without replay', () => {
  const chest = new Chest('Codex');
  const now = Date.now();
  chest.set(usage(now + 60000), bubbles);
  assert.equal(chest.resetDue(now), false);
  assert.equal(chest.resetDue(now + 60001), true);
  // Simulate an overdue report arriving while hidden or with motion reduced.
  chest.usage = usage(now - 60000);
  chest.settle();
  assert.equal(chest.resetDue(now), false);
  assert.equal(chest.burst, 0);
});
