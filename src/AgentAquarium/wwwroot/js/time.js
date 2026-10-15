/**
 * Milliseconds since the epoch. The server sends up to 7 fractional digits; ECMAScript only
 * promises to parse 3, so trim the rest rather than risk NaN.
 */
export function time(iso) {
  return Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'));
}
