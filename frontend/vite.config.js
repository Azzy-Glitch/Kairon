import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // Local by default. A developer may opt into LAN exposure explicitly, but the standard
    // command must not expose the operator-authenticated proxy on every interface.
    host: process.env.KAIRON_VITE_HOST || '127.0.0.1',
    port: 5173,
    proxy: {
      // Mirrors the installed same-origin layout while preserving the Vite development server.
      '/api': {
        target: process.env.KAIRON_BACKEND_URL || 'http://localhost:8000',
        changeOrigin: true,
        configure: (proxy) => {
          // Development equivalent of the desktop WebView header injection. The secret remains
          // in the Node proxy process and is never exposed through a VITE_* browser variable.
          proxy.on('proxyReq', (proxyReq) => {
            const operatorKey = process.env.KAIRON_OPERATOR_KEY;
            if (operatorKey) proxyReq.setHeader('X-Kairon-Operator-Key', operatorKey);
          });
        }
      }
    }
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: './src/test/setup.js',
    // Only the co-located suite; dist/ and node_modules are never test sources.
    include: ['src/**/*.{test,spec}.{js,jsx}']
  }
});
