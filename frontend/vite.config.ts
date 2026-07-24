import { fileURLToPath, URL } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  root: fileURLToPath(new URL(".", import.meta.url)),
  plugins: [react()],
  clearScreen: false,
  server: { host: "127.0.0.1", port: 1420, strictPort: true },
  envPrefix: ["VITE_", "TAURI_ENV_"],
  build: { target: "es2022", minify: "esbuild", sourcemap: true, outDir: "dist", emptyOutDir: true },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
    include: ["src/**/*.test.{ts,tsx}"],
    exclude: ["tests/**", "node_modules/**", "dist/**"],
    css: true,
  },
});
