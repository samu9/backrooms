import { defineConfig } from 'vite';
export default defineConfig({
  base: './',
  server: { port: 5190, host: '127.0.0.1' },
  build: { target: 'es2022', assetsInlineLimit: 0, chunkSizeWarningLimit: 1000 },
});
