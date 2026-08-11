/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'
import { visualizer } from 'rollup-plugin-visualizer'

// https://vite.dev/config/
export default defineConfig({
  test: {
    environment: 'jsdom',
    globals: false,
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['./src/test/setup.ts'],
    restoreMocks: true,
  },
  plugins: [
    react(),
    // Bundle composition report, opt-in only (ANALYZE=1 npm run build) so a normal build doesn't
    // pay the extra analysis time. Writes a static bundle-stats.html next to this config - never
    // shipped, since Vite's outDir points at ../FSOps.Server/wwwroot, not this directory.
    process.env.ANALYZE &&
      visualizer({ filename: 'bundle-stats.html', gzipSize: true, brotliSize: false }),
  ].filter(Boolean),
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
    rollupOptions: {
      output: {
        // Groups the framework libraries - used on every route, and far more stable than app code
        // - into their own cacheable chunk, rather than leaving them duplicated or scattered
        // across the per-route chunks created by the React.lazy boundaries in App.tsx. Everything
        // else in node_modules is deliberately left to Rollup's own default grouping: an earlier
        // version of this also swept the rest of node_modules (lucide-react, sonner, signalr...)
        // into one shared "vendor" bucket, but that bucket also caught maplibre-gl - which must
        // stay reachable only through the dynamic imports in Dashboard/Routes/LiveFlightView, not
        // bundled next to statically-used libraries - and produced circular chunk warnings between
        // the react/radix buckets. A narrow allowlist avoids both.
        manualChunks(id) {
          if (/[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) {
            return 'vendor-react'
          }
          return undefined
        },
      },
    },
  },
})
