// Hue identifies the session (the whole school); saturation and lightness
// drop with each generation, so parents are vivid and descendants duller.

// Hues are handed out in order of appearance from a hand-picked sequence: neighbours
// in the list are far apart and none are water blue. A hash put two of five sessions
// on the same red; a golden-angle walk folded around the blue gap did the same.
const PALETTE = [5, 52, 135, 285, 28, 325, 90, 165, 345, 42, 265, 115];
const hues = new Map();

export function sessionHue(sessionId) {
  let hue = hues.get(sessionId);
  if (hue === undefined) {
    const n = hues.size;
    hue = (PALETTE[n % PALETTE.length] + Math.floor(n / PALETTE.length) * 9) % 360;
    hues.set(sessionId, hue);
  }
  return hue;
}

export function generationTone(depth) {
  return {
    s: Math.max(22, 95 - depth * 34),
    l: Math.max(30, 58 - depth * 11),
  };
}

export function fishColor(sessionId, depth, dl = 0, ds = 0, alpha = 1) {
  const { s, l } = generationTone(depth);
  return `hsla(${sessionHue(sessionId)} ${Math.max(0, s + ds)}% ${Math.min(95, l + dl)}% / ${alpha})`;
}
