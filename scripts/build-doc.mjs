// Builds docs/layout-algorithm.html: the explainer template with the real shared/gen.ts inlined,
// so every figure in the page is drawn by the same code the game runs.
//   node scripts/build-doc.mjs
import { build } from 'rolldown';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';

const out = await build({ input: 'shared/gen.ts', write: false, output: { format: 'iife', name: 'Gen' } });
const gen = out.output[0].code;
const tpl = readFileSync('scripts/doc-layout.template.html', 'utf8');
mkdirSync('docs', { recursive: true });
writeFileSync('docs/layout-algorithm.html', tpl.replace('/*GEN*/', () => gen));
console.log('docs/layout-algorithm.html written');
