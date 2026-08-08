import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5977',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5977',
        changeOrigin: true,
        ws: true,
      },
    },
  },
  // MapLibre tiles its GeoJSON in a web worker written as an ES module. Vite bundles workers as
  // classic scripts by default, so the worker was silently dropped from the production build:
  // raster basemap tiles and DOM markers still appeared, but no route line ever rendered because
  // the geometry never got tiled. Building workers as ES modules keeps that worker intact.
  worker: {
    format: 'es',
  },
  build: {
    outDir: '../FSOps.Server/wwwroot',
    emptyOutDir: true,
  },
})
