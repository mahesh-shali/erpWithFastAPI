// Renders build/icon.svg to build/icon.png (1024x1024) with Electron's own Chromium, so no image library is
// needed. electron-builder makes the .ico / .icns / Linux sizes from that PNG; build/icon.ico is also written for
// Squirrel.Windows (see make-ico.cjs).
// Run with: npm run icon
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const { toIco } = require('./make-ico.cjs');

const SIZE = 1024;
const root = path.join(__dirname, '..');
const svg = fs.readFileSync(path.join(root, 'build', 'icon.svg'), 'utf8');
const html = '<!doctype html><html><head><style>html,body{margin:0;overflow:hidden;background:transparent}'
  + `svg{display:block}</style></head><body>${svg}</body></html>`;

app.disableHardwareAcceleration();
app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: SIZE, height: SIZE, show: false, frame: false, transparent: true, backgroundColor: '#00000000', useContentSize: true,
    webPreferences: { offscreen: true, javascript: false },
  });
  win.webContents.setZoomFactor(1);
  await win.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(html)}`);
  await new Promise((r) => setTimeout(r, 300));
  const image = (await win.webContents.capturePage({ x: 0, y: 0, width: SIZE, height: SIZE })).resize({ width: SIZE, height: SIZE });
  fs.writeFileSync(path.join(root, 'build', 'icon.png'), image.toPNG());
  console.log(`Wrote build/icon.png (${image.getSize().width}x${image.getSize().height})`);
  fs.writeFileSync(path.join(root, 'build', 'icon.ico'), toIco(image)); // Squirrel's "Installed apps" icon
  console.log('Wrote build/icon.ico');
  app.quit();
}).catch((e) => { console.error(e); app.exit(1); });
