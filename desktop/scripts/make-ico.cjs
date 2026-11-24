// Writes build/icon.ico (16–256 px, PNG-compressed entries) from build/icon.png with Electron's nativeImage, so no
// image library is needed. Squirrel.Windows shows it in "Installed apps" (squirrelWindows.iconUrl in package.json
// points at this file on GitHub). Run with: npm run icon (after make-icon) or `electron scripts/make-ico.cjs`.
const fs = require('node:fs');
const path = require('node:path');

const SIZES = [16, 24, 32, 48, 64, 128, 256];

function toIco(image) {
  const pngs = SIZES.map((size) => image.resize({ width: size, height: size, quality: 'best' }).toPNG());
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // reserved
  header.writeUInt16LE(1, 2); // type: icon
  header.writeUInt16LE(pngs.length, 4);
  const entries = [];
  let offset = 6 + 16 * pngs.length;
  pngs.forEach((png, i) => {
    const e = Buffer.alloc(16);
    e.writeUInt8(SIZES[i] >= 256 ? 0 : SIZES[i], 0); // 0 means 256
    e.writeUInt8(SIZES[i] >= 256 ? 0 : SIZES[i], 1);
    e.writeUInt16LE(1, 4);   // colour planes
    e.writeUInt16LE(32, 6);  // bits per pixel
    e.writeUInt32LE(png.length, 8);
    e.writeUInt32LE(offset, 12);
    offset += png.length;
    entries.push(e);
  });
  return Buffer.concat([header, ...entries, ...pngs]);
}

module.exports = { toIco };

// Run directly (`electron scripts/make-ico.cjs`); Electron doesn't set require.main to the entry script.
if (process.argv.some((a) => path.resolve(a) === __filename)) {
  const { app, nativeImage } = require('electron');
  const root = path.join(__dirname, '..');
  app.whenReady().then(() => {
    const image = nativeImage.createFromPath(path.join(root, 'build', 'icon.png'));
    if (image.isEmpty()) throw new Error('build/icon.png not found or unreadable');
    fs.writeFileSync(path.join(root, 'build', 'icon.ico'), toIco(image));
    console.log(`Wrote build/icon.ico (${SIZES.join(', ')} px)`);
    app.quit();
  }).catch((e) => { console.error(e); app.exit(1); });
}
