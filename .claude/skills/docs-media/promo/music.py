"""Soundtrack for the Agent Aquarium brag video: one piece, music and effects in F major.

Grid: 96 BPM (beat 0.625 s). 0-3.125 s is a free intro (pad + bubbles + riser), the groove's bar 1
lands on the reveal at 3.125 s, and the final Fmaj9 lands at 18.125 s. Writes a 48 kHz stereo WAV.
"""
import sys
import wave
import numpy as np

SR = 48000
DUR = 21.5
N = int(SR * DUR)
BEAT = 0.625
BAR = BEAT * 4
T0 = 3.125  # downbeat of bar 1, the reveal
FINAL = 18.125
rng = np.random.default_rng(7)


def mf(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def buf():
    return np.zeros((2, N))


def place(dst, sig, t, gain=1.0, pan=0.0):
    """Add a mono or stereo signal at time t (s), equal-power pan -1..1."""
    i = int(t * SR)
    if i >= N:
        return
    if sig.ndim == 1:
        l = np.cos((pan + 1) * np.pi / 4)
        r = np.sin((pan + 1) * np.pi / 4)
        sig = np.vstack([sig * l, sig * r])
    n = min(sig.shape[1], N - i)
    dst[:, i:i + n] += sig[:, :n] * gain


def env_adsr(n, a, d, s, r, total):
    """Linear ADSR over `total` seconds; n samples."""
    t = np.arange(n) / SR
    e = np.where(t < a, t / max(a, 1e-4), 1.0)
    e = np.where(t >= a, s + (1 - s) * np.exp(-(t - a) / max(d, 1e-4)), e)
    rel = total - r
    e = np.where(t > rel, e * np.clip(1 - (t - rel) / r, 0, 1), e)
    return e


def onepole_lp(x, fc):
    a = np.exp(-2 * np.pi * fc / SR)
    y = np.empty_like(x)
    acc = 0.0
    # Vectorised via lfilter-free recursion in chunks would be faster; numpy loop is fine at these sizes.
    from itertools import accumulate  # noqa: F401
    for i in range(len(x)):
        acc = (1 - a) * x[i] + a * acc
        y[i] = acc
    return y


def lp_fft(x, fc, slope=2):
    """Zero-phase lowpass in the frequency domain (gentle, order ~ slope)."""
    X = np.fft.rfft(x, axis=-1)
    f = np.fft.rfftfreq(x.shape[-1], 1 / SR)
    H = 1 / np.sqrt(1 + (f / fc) ** (2 * slope))
    return np.fft.irfft(X * H, n=x.shape[-1], axis=-1)


def hp_fft(x, fc, slope=2):
    X = np.fft.rfft(x, axis=-1)
    f = np.fft.rfftfreq(x.shape[-1], 1 / SR)
    H = 1 / np.sqrt(1 + (fc / np.maximum(f, 1e-3)) ** (2 * slope))
    return np.fft.irfft(X * H, n=x.shape[-1], axis=-1)


# --- voices -------------------------------------------------------------------

def pad_note(m, length, bright=1.0):
    n = int(length * SR)
    t = np.arange(n) / SR
    out = np.zeros((2, n))
    for ch, det in enumerate((-0.07, 0.07)):  # semitones, L/R
        f = mf(m + det)
        sig = np.zeros(n)
        for h in range(1, 9):
            amp = (1 / h ** 1.6) * (bright if h > 2 else 1)
            sig += amp * np.sin(2 * np.pi * f * h * t + rng.uniform(0, 6.28))
        vib = 1 + 0.004 * np.sin(2 * np.pi * 0.3 * t + ch)
        out[ch] = sig * vib
    e = env_adsr(n, 0.6, 1.5, 0.75, 0.9, length)
    return out * e * 0.12


def pad_chord(dst, notes, t, length, gain, bright=0.7):
    for m in notes:
        place(dst, pad_note(m, length, bright), t, gain)


def marimba(m, length=0.9):
    n = int(length * SR)
    t = np.arange(n) / SR
    f = mf(m)
    sig = (np.sin(2 * np.pi * f * t) * np.exp(-t / 0.35)
           + 0.35 * np.sin(2 * np.pi * f * 3.93 * t) * np.exp(-t / 0.05)
           + 0.15 * np.sin(2 * np.pi * f * 9.2 * t) * np.exp(-t / 0.015))
    sig *= np.clip(t / 0.002, 0, 1)
    return sig


def bass(m, length):
    n = int(length * SR)
    t = np.arange(n) / SR
    f = mf(m)
    sig = np.sin(2 * np.pi * f * t) + 0.25 * np.sin(2 * np.pi * 2 * f * t) + 0.08 * np.sin(2 * np.pi * 3 * f * t)
    e = np.clip(t / 0.01, 0, 1) * np.exp(-t / (length * 0.8)) * np.clip((length - t) / 0.05, 0, 1)
    return sig * e


def kick():
    n = int(0.4 * SR)
    t = np.arange(n) / SR
    f = 45 + 75 * np.exp(-t / 0.04)
    ph = 2 * np.pi * np.cumsum(f) / SR
    sig = np.sin(ph) * np.exp(-t / 0.16) * np.clip(t / 0.001, 0, 1)
    click = rng.normal(0, 1, n) * np.exp(-t / 0.003) * 0.08
    return sig + click


def snap():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    noise = rng.normal(0, 1, n)
    noise = hp_fft(lp_fft(noise, 6000), 1200)
    body = np.sin(2 * np.pi * 220 * t) * np.exp(-t / 0.03) * 0.3
    return (noise * np.exp(-t / 0.06) + body) * 0.5


def shaker():
    n = int(0.09 * SR)
    t = np.arange(n) / SR
    noise = hp_fft(rng.normal(0, 1, n), 6000)
    return noise * np.clip(t / 0.01, 0, 1) * np.exp(-t / 0.025)


def bubble(m, length=0.12, rise=1.7):
    """A pitched bloop: a sine whose pitch glides up, like a bubble leaving a fish."""
    n = int(length * SR)
    t = np.arange(n) / SR
    f = mf(m) * (1 + (rise - 1) * (1 - np.exp(-t / 0.035)))
    ph = 2 * np.pi * np.cumsum(f) / SR
    e = np.clip(t / 0.004, 0, 1) * np.exp(-t / 0.035)
    return np.sin(ph) * e


def bell(m, length=1.4):
    n = int(length * SR)
    t = np.arange(n) / SR
    f = mf(m)
    mod = np.sin(2 * np.pi * f * 3.5 * t) * 2.2 * np.exp(-t / 0.25)
    sig = np.sin(2 * np.pi * f * t + mod) * np.exp(-t / 0.45) * np.clip(t / 0.002, 0, 1)
    return sig


def riser(length):
    n = int(length * SR)
    t = np.arange(n) / SR
    noise = rng.normal(0, 1, (2, n))
    # Sweep a lowpass upward by mixing progressively brighter copies.
    dark = lp_fft(noise, 500)
    bright_ = lp_fft(noise, 5000)
    x = t / length
    sig = dark * (1 - x) + bright_ * x
    return sig * (x ** 2.2) * 0.35


def reverb_ir(seconds=2.2, decay=0.55):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = rng.normal(0, 1, (2, n)) * np.exp(-t / decay)
    ir = lp_fft(ir, 4500)
    ir[:, : int(0.012 * SR)] *= np.linspace(0, 1, int(0.012 * SR))
    return ir / np.sqrt((ir ** 2).sum(axis=1, keepdims=True))


def convolve(x, ir):
    n = x.shape[1] + ir.shape[1]
    size = 1 << (n - 1).bit_length()
    X = np.fft.rfft(x, size, axis=1)
    H = np.fft.rfft(ir, size, axis=1)
    return np.fft.irfft(X * H, size, axis=1)[:, : x.shape[1]]


# --- arrangement --------------------------------------------------------------

# (start, bass midi, pad notes) per bar from the reveal.
F7, AM7, BB7, C69, DM7 = ([53, 57, 60, 64]), ([57, 60, 64, 67]), ([58, 62, 65, 69]), ([60, 64, 67, 69, 74]), ([62, 65, 69, 72])
bars = [
    (T0 + 0 * BAR, 41, F7),
    (T0 + 1 * BAR, 45, AM7),
    (T0 + 2 * BAR, 46, BB7),
    (T0 + 3 * BAR, 48, C69),
    (T0 + 4 * BAR, 41, F7),
    (T0 + 5 * BAR, 46, BB7),  # beat 3 moves to C, then the final F
]
FMAJ9 = [53, 57, 60, 64, 67, 72]

pad = buf()
keys = buf()
low = buf()
drums = buf()
sfx = buf()
send = buf()  # reverb send

# Intro: Bbmaj7 pad swelling in, darker, under the hook.
intro = buf()
pad_chord(intro, [58, 62, 65, 69], 0.0, T0 + 0.4, 1.0, bright=0.35)
intro = lp_fft(intro, 900)
pad += intro * 0.9

for i, (t, b, notes) in enumerate(bars):
    length = BAR + 0.5
    if i == 5:
        pad_chord(pad, notes, t, BEAT * 2 + 0.4, 0.9)
        pad_chord(pad, [60, 64, 67, 70], t + BEAT * 2, BEAT * 2 + 0.4, 0.9)  # C7 turnaround
    else:
        pad_chord(pad, notes, t, length, 0.9)
    # Bass: root on 1, a pickup on the "and" of 2, root again on 3.
    roots = [b, b, b] if i != 5 else [b, b, 48]
    place(low, bass(roots[0], BEAT * 1.4), t, 0.55)
    place(low, bass(roots[1] + 12, BEAT * 0.45), t + BEAT * 1.5, 0.35)
    place(low, bass(roots[2], BEAT * 1.8), t + BEAT * 2, 0.5)
    # Marimba arpeggio in 8ths over the chord, an octave up.
    tones = (notes if i != 5 else [58, 62, 65, 69]) + [notes[0] + 12]
    pattern = [0, 2, 1, 3, 2, 4, 3, 1]
    for k, p in enumerate(pattern):
        tone = tones[p % len(tones)] + 12
        if i == 5 and k >= 4:
            tone = [60, 64, 67, 70, 72][p % 5] + 12
        vel = 0.5 if k % 2 == 0 else 0.32
        place(keys, marimba(tone), t + k * BEAT / 2, vel * 0.55, pan=-0.35 + 0.1 * (k % 4))
    # Drums: kick on 1 and 3 (and a ghost before 3), snap on 2 and 4, shaker 16ths.
    for k in range(4):
        bt = t + k * BEAT
        if k in (0, 2):
            place(drums, kick(), bt, 0.8)
        if k in (1, 3):
            place(drums, snap(), bt, 0.22, pan=0.1)
            place(send, snap(), bt, 0.12)
        for s in range(4):
            acc = 0.09 if s == 2 else 0.045
            place(drums, shaker(), bt + s * BEAT / 4, acc, pan=0.4)
    if i in (0, 2, 4):
        place(drums, kick(), t + BEAT * 1.75, 0.35)

# Final: Fmaj9 with bass, a bell on top, everything else drops out.
pad_chord(pad, FMAJ9, FINAL, DUR - FINAL, 1.0, bright=0.8)
place(low, bass(41, 2.6), FINAL, 0.6)
place(drums, kick(), FINAL, 0.9)
for k, m in enumerate([77, 81, 84, 88]):
    place(keys, marimba(m, 1.6), FINAL + k * BEAT / 2, 0.38, pan=-0.3 + 0.2 * k)
place(sfx, bell(84, 3.0), FINAL, 0.22)

# Riser into the reveal.
place(sfx, riser(1.6), T0 - 1.6, 0.6)

# Bubbles on the hook lines and title, tuned to the scale.
BUB = [84, 88, 89, 91, 93, 96]
for t0, notes in [(0.15, [81, 84, 88]), (1.1, [84, 88, 91]), (3.2, [88, 91, 93, 96])]:
    for k, m in enumerate(notes):
        place(sfx, bubble(m), t0 + k * 0.07, 0.16, pan=-0.2 + 0.2 * k)
# Subagents born: a burst of bubbles each.
for t0 in (6.9, 7.7):
    for k in range(5):
        place(sfx, bubble(BUB[(k * 2) % len(BUB)] - 12, 0.1, 1.9), t0 + k * 0.045, 0.13, pan=rng.uniform(-0.5, 0.5))
# The diver: a soft downward bubble trail as the scene opens.
for k in range(6):
    place(sfx, bubble(BUB[5 - k] - 12, 0.14, 1.4), 10.05 + k * 0.11, 0.1, pan=-0.3 + 0.12 * k)
# Coins: soft bells, in key, while the gold floats away.
for k, m in enumerate([93, 96, 100, 96, 93, 98, 101]):
    place(sfx, bell(m, 1.0), 14.65 + k * 0.42, 0.07, pan=-0.6 + 0.2 * k)

# --- mix ----------------------------------------------------------------------

send += pad * 0.5 + keys * 0.6 + sfx * 0.7 + drums * 0.05
dry = pad * 1.0 + keys * 1.0 + low * 0.9 + drums * 0.85 + sfx * 0.85
# Keep the bass out of the reverb and the sub out of everything else.
wet = convolve(hp_fft(send, 250), reverb_ir()) * 0.32
mix = hp_fft(dry + wet, 30)

# Fade the tail.
t = np.arange(N) / SR
mix *= np.clip((DUR - t) / 1.4, 0, 1)
mix *= np.clip(t / 0.05, 0, 1)

peak = np.abs(mix).max()
mix = np.tanh(mix / peak * 1.1) / np.tanh(1.1) * 0.89

out = sys.argv[1] if len(sys.argv) > 1 else 'music.wav'
data = (mix.T * 32767).astype('<i2')
with wave.open(out, 'wb') as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(data.tobytes())
print('wrote', out, f'{DUR}s')
