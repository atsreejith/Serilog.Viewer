import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  base: '/logviewer/',
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: {
          vendor: ['react', 'react-dom'],
          router: ['react-router-dom'],
          query: ['@tanstack/react-query'],
          table: ['@tanstack/react-table', '@tanstack/react-virtual'],
          charts: ['recharts'],
          signalr: ['@microsoft/signalr'],
        },
      },
    },
  },
  server: {
    proxy: {
      '/logviewer/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/logviewer/hubs': {
        target: 'http://localhost:5000',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
