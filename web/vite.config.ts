import { defineConfig } from 'vite';
// The web client lives in web/, but builds to the repo-root dist/ that server/server.ts serves.
export default defineConfig({
  root: import.meta.dirname,
  base: './',
  server: { port: 5190, host: '127.0.0.1', proxy: { '/ws': { target: `ws://127.0.0.1:${process.env.RELAY_PORT ?? 5191}`, ws: true } } },
  build: { outDir: '../dist', emptyOutDir: true, target: 'es2022', assetsInlineLimit: 0, chunkSizeWarningLimit: 1000 },
});
