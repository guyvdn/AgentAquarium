// Serves the real wwwroot at / and the promo files at /promo/. The only change to the app is one
// line appended to main.js that exposes its module state, so the camera can follow fish and diver.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const [, , wwwroot, promoDir, port = '5203'] = process.argv;
const TYPES = {
  '.html': 'text/html',
  '.js': 'text/javascript',
  '.mjs': 'text/javascript',
  '.css': 'text/css',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
};

http
  .createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/promo/tank.html') {
      // The app's own page, with relative URLs resolved from / and the boot script ahead of main.js.
      const html = fs
        .readFileSync(path.join(wwwroot, 'index.html'), 'utf8')
        .replace(
          '<head>',
          '<head>\n    <base href="/" />\n    <script src="/promo/virtual-clock.js"></script>\n    <script src="/promo/tank-boot.js"></script>',
        );
      res.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-cache' });
      res.end(html);
      return;
    }
    let file;
    if (url.pathname === '/promo/virtual-clock.js') file = path.join(promoDir, '..', 'virtual-clock.js');
    else if (url.pathname.startsWith('/promo/')) file = path.join(promoDir, url.pathname.slice(7));
    else file = path.join(wwwroot, url.pathname === '/' ? 'index.html' : url.pathname);
    fs.readFile(file, (err, data) => {
      if (err) {
        res.writeHead(404);
        res.end();
        return;
      }
      let body = data;
      if (file.endsWith(path.join('js', 'main.js')))
        body = data
          .toString()
          .replace(/\nconnect\(\);/, '\nwindow.__aq = { world, diver, chests, bubbles };\nconnect();');
      res.writeHead(200, {
        'Content-Type': TYPES[path.extname(file)] ?? 'application/octet-stream',
        'Cache-Control': 'no-cache',
      });
      res.end(body);
    });
  })
  .listen(+port, '127.0.0.1', () => console.log(`listening ${port}`));
