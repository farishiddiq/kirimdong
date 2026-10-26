// Snaps spacing, type, radius and element sizes in XAML to the Tailwind CSS default scales
// (ui-skills baseline: "use Tailwind CSS defaults"). Window sizes are left alone.
//   node tools/snap-to-scale.mjs src/Kirimdong.App/*.xaml
import { readFileSync, writeFileSync } from 'node:fs';

const spacing = [0, 1, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 36, 40, 44, 48, 56, 64, 72, 80, 96];
// Up to max-w-2xl, so dialogs keep their Tailwind max-width steps (lg 512, xl 576, 2xl 672).
const sizes = [...spacing, 112, 128, 144, 160, 176, 192, 208, 224, 240, 256, 288, 320, 384, 448, 512, 576, 672];
const type = [12, 14, 16, 18, 20, 24, 30, 36, 48, 60];
const radius = [0, 2, 4, 6, 8, 12, 16, 24, 999];

const nearest = (scale, v) => {
  const a = Math.abs(v);
  let best = scale[0];
  for (const s of scale) if (Math.abs(s - a) < Math.abs(best - a)) best = s; // ties keep the smaller step
  return Math.sign(v) * best;
};
const snapList = (scale, value) =>
  value.split(',').map(p => {
    const n = Number(p.trim());
    return Number.isFinite(n) ? String(nearest(scale, n)) : p;
  }).join(',');

for (const file of process.argv.slice(2)) {
  let s = readFileSync(file, 'utf8');
  const before = s;
  s = s.replace(/\b(Padding|Margin)="([-\d.,\s]+)"/g, (_, k, v) => `${k}="${snapList(spacing, v)}"`);
  s = s.replace(/\bFontSize="([\d.]+)"/g, (_, v) => `FontSize="${nearest(type, Number(v))}"`);
  s = s.replace(/\bCornerRadius="([\d.]+)"/g, (_, v) => `CornerRadius="${nearest(radius, Number(v))}"`);
  s = s.replace(/\bRadius([XY])="([\d.]+)"/g, (_, a, v) => `Radius${a}="${nearest(radius, Number(v))}"`);
  // Element sizes, except on the <Window> element itself.
  s = s.replace(/<(?!Window\b)(\w[\w:.]*)\b([^>]*)>/g, (tag, name, attrs) =>
    `<${name}${attrs.replace(/\b(Width|Height)="([\d.]+)"/g, (_, k, v) => `${k}="${nearest(sizes, Number(v))}"`)}>`);
  if (s !== before) writeFileSync(file, s);
  console.log(`${file}: ${s === before ? 'unchanged' : 'snapped'}`);
}
