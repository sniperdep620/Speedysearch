import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  timeout: 15_000,
  use: { baseURL: "http://127.0.0.1:1420", trace: "retain-on-failure" },
  webServer: {
    command: "npm run dev",
    cwd: "..",
    url: "http://127.0.0.1:1420",
    reuseExistingServer: true,
  },
});
