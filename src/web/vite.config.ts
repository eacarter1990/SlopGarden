import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const server = process.env.SLOPGARDEN_SERVER ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../SlopGarden.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2000,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': server,
      '/mcp': server,
      '/openapi': server,
      '/hubs': { target: server, ws: true },
    },
  },
})
