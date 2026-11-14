import { defineConfig } from 'vite';

export default defineConfig({
  build: { outDir: '../src/OrderGenerator.Host/wwwroot', emptyOutDir: true },
  server: { proxy: {
    '/orders': {
      target: 'http://localhost:5000', changeOrigin: true,
      configure: proxy => proxy.on('proxyReq', (request, original) => {
        if (original.headers.origin) request.setHeader('origin', 'http://localhost:5000');
      }),
    },
    '/exposures': { target: 'http://localhost:5000', changeOrigin: true },
    '/status': { target: 'http://localhost:5000', changeOrigin: true },
  } }
});
