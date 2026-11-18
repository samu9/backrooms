import { defineConfig } from 'vite';
export default defineConfig({
  base: './',
  server: { port: 5190, host: '127.0.0.1', proxy: { '/ws': { target: `ws://127.0.0.1:${process.env.RELAY_PORT ?? 5191}`, ws: true } } },
  build: { target: 'es2022', assetsInlineLimit: 0, chunkSizeWarningLimit: 1000 },
});
