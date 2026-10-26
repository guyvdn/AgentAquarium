// Encode the rendered promo, optionally reusing the approved video's AAC audio verbatim.
import { execFileSync } from 'node:child_process';
import path from 'node:path';
const [, , frames, audio, output] = process.argv;
if (!frames || !audio || !output)
  throw new Error('Usage: node encode.mjs <frames-dir> <audio.wav|original.mp4> <output.mp4>');
const audioOptions =
  path.extname(audio).toLowerCase() === '.mp4'
    ? ['-c:a', 'copy']
    : ['-c:a', 'aac', '-b:a', '192k', '-af', 'loudnorm=I=-14:TP=-1.5:LRA=11'];
execFileSync(
  'ffmpeg',
  [
    '-hide_banner',
    '-loglevel',
    'error',
    '-y',
    '-framerate',
    '30',
    '-i',
    path.join(frames, 'f%04d.jpg'),
    '-i',
    audio,
    '-map',
    '0:v:0',
    '-map',
    '1:a:0',
    '-vf',
    'scale=1080:1080:flags=lanczos',
    '-c:v',
    'libx264',
    '-preset',
    'medium',
    '-crf',
    '18',
    '-pix_fmt',
    'yuv420p',
    ...audioOptions,
    '-movflags',
    '+faststart',
    '-shortest',
    output,
  ],
  { stdio: 'inherit' },
);
execFileSync(
  'ffmpeg',
  [
    '-hide_banner',
    '-loglevel',
    'error',
    '-y',
    '-i',
    path.join(frames, 'f0170.jpg'),
    '-vf',
    'scale=1080:1080:flags=lanczos',
    '-frames:v',
    '1',
    output.replace(/\.mp4$/i, '.jpg'),
  ],
  { stdio: 'inherit' },
);
