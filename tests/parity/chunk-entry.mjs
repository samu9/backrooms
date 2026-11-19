// Bundled by dump.ts (web/src uses bundler-style imports Node can't load directly). setSeed must
// come from the same bundle as buildChunk: gen.ts keeps the seed in module state.
export { buildChunk, makeMaterials } from '../../web/src/chunk.ts';
export { setSeed } from '../../shared/gen.ts';
