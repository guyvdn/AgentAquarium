// Fast capture against a demo server started on a spare port. Production sessions stay disabled.
// node capture.mjs http://localhost:5202 artifacts/agent-aquarium-media/docs
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';

const [, , url = 'http://localhost:5202', output = 'artifacts/agent-aquarium-media/docs'] = process.argv;
if (new URL(url).port === '5180') throw new Error('Use a spare demo port, never the live aquarium');
const directory = path.dirname(fileURLToPath(import.meta.url));
fs.mkdirSync(path.join(output, 'frames'), { recursive: true });
// An optional built DLL gives each capture a fresh, isolated demo and a predictable starting scene.
const dll = process.argv[4];
let server;
if (dll) {
  let listening = false;
  try {
    await fetch(url);
    listening = true;
  } catch {
    /* expected for an unused spare port */
  }
  if (listening) throw new Error(`A server already listens on ${url}`);
  const root = path.resolve(directory, '../../..', 'src/AgentAquarium');
  server = spawn(
    'dotnet',
    [
      path.resolve(dll),
      '--urls',
      url,
      '--contentRoot',
      root,
      '--Sources:ClaudeCode:Enabled=false',
      '--Sources:Codex:Enabled=false',
      '--Sources:Demo:Enabled=true',
      '--Sources:Demo:Sessions=4',
    ],
    { windowsHide: true, stdio: 'ignore' },
  );
  process.on('exit', () => server.kill());
  let ready = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      if ((await fetch(url)).ok) {
        ready = true;
        break;
      }
    } catch {
      /* startup */
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  if (!ready) throw new Error('The isolated demo did not start');
}
const browser = await chromium.launch({ channel: 'msedge' });
try {
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: 1 });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  // Capture-only access for a readable starting layout, as used by the promo's camera.
  await page.route('**/js/main.js', async route => {
    const response = await route.fetch();
    const body = (await response.text()).replace(
      '\nconnect();',
      '\nwindow.__captureTank = { world, diver };\nconnect();',
    );
    await route.fulfill({ response, body });
  });
  await page.goto(`${url}/styles.css`);
  await page.evaluate(
    fs.readFileSync(path.join(directory, 'virtual-clock.js'), 'utf8') +
      '\n' +
      fs.readFileSync(path.join(directory, 'tank-boot.js'), 'utf8'),
  );
  await page.waitForSelector('#schools li[data-id]');
  await page.waitForTimeout(14000);
  for (let attempt = 0; attempt < 45; attempt++) {
    const scene = await page.evaluate(async () => ({
      rows: document.querySelectorAll('#schools li').length,
      waiting: document.querySelector('#schools').innerText.includes('needs you'),
      usage: await (await fetch('/api/usages')).json(),
    }));
    console.log('scene', JSON.stringify(scene));
    if (
      scene.rows >= 7 &&
      scene.rows <= 16 &&
      scene.waiting &&
      ['claude-code', 'codex'].every(source =>
        scene.usage.some(u => u.source === source && u.fiveHour?.usedPercent < 60),
      )
    )
      break;
    if (attempt === 44) throw new Error('No readable mixed-provider scene; restart the demo and retry');
    await page.waitForTimeout(1000);
  }
  await page.evaluate(() => {
    window.__freeze();
    const { world, diver } = window.__captureTank;
    const slots = [
      [410, 300],
      [850, 280],
      [1040, 570],
      [650, 600],
    ];
    const roots = [...world.fish.values()].filter(fish => fish.isRoot).sort((a, b) => a.id.localeCompare(b.id));
    roots.forEach((fish, index) => {
      [fish.x, fish.y] = slots[index % slots.length];
      fish.vx = index % 2 ? -20 : 20;
      fish.vy = 0;
      fish.angle = index % 2 ? Math.PI : 0;
      fish.target = null;
      fish.retargetAt = 0;
      fish.opacity = 1;
      const children = [...world.fish.values()].filter(child => child.agent.parentId === fish.id);
      children.forEach((child, n) => {
        child.x = fish.x - Math.sign(fish.vx) * (90 + Math.floor(n / 2) * 55);
        child.y = fish.y + (n % 2 ? 55 : -55);
        child.vx = fish.vx;
        child.vy = 0;
        child.angle = fish.angle;
        child.opacity = 1;
      });
    });
    diver.settle(world);
    window.__step(6);
  });
  await page.screenshot({ path: path.join(output, 'screenshot.png') });
  for (let frame = 0; frame < 135; frame++) {
    await page.evaluate(() => window.__step(2));
    await page.screenshot({ path: path.join(output, 'frames', `f${String(frame).padStart(3, '0')}.png`) });
    if (frame % 30 === 0) console.log('frame', frame, '/ 135');
  }
  if (errors.length) throw new Error(errors.join('\n'));
} finally {
  await browser.close();
  server?.kill();
}
execFileSync(
  'ffmpeg',
  [
    '-v',
    'error',
    '-y',
    '-framerate',
    '15',
    '-i',
    path.join(output, 'frames', 'f%03d.png'),
    '-vf',
    'fps=12,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle',
    '-loop',
    '0',
    path.join(output, 'demo.gif'),
  ],
  { stdio: 'inherit' },
);
if (fs.statSync(path.join(output, 'demo.gif')).size > 5_000_000) throw new Error('GIF exceeds the 5 MB README budget');
console.log('Created screenshot.png and demo.gif in', path.resolve(output));
