import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

const sourceDirectory = fileURLToPath(new URL("./src", import.meta.url));

export default defineConfig({
  plugins: [
    react(),
  ],
  base: '/',    
  resolve: {
    alias: {
      "@": sourceDirectory,
    },
  },
  server: {
    port: 3000,
    host: '0.0.0.0'
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    minify: false,  
    rollupOptions: {
      treeshake: false,
    }
  }
});
