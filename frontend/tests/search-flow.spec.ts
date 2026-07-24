import { expect, test } from "@playwright/test";

test("searches, renders ranked results, and opens a selection", async ({ page }) => {
  await page.addInitScript(() => {
    const calls: string[] = [];
    const callArgs: Array<{ command: string; args?: Record<string, unknown> }> = [];
    Object.assign(window, {
      __SPEEDYSEARCH_CALLS__: calls,
      __SPEEDYSEARCH_CALL_ARGS__: callArgs,
      __SPEEDYSEARCH_MOCK__: async (command: string, args?: Record<string, unknown>) => {
        calls.push(command);
        callArgs.push({ command, args });
        if (command === "load_ui_config" || command === "save_ui_config") return {
          layout_mode: "three-column", preview_anchor: "right", opacity: 0.95,
          animation_duration_ms: 200, global_hotkey: "Ctrl+Space",
          show_frecency: true, highlight_active_app: true,
        };
        if (command === "search_query") return {
          results: [
            { id: "90071992547409931", entry_type: "App", name: "Document Viewer", path: "org.gnome.Evince", icon_path: null, ml_score: 0.96, frecency_score: 0.8, is_dir: false, category: "Utility" },
            { id: "90071992547409930", entry_type: "File", name: "document.pdf", path: "/tmp/document.pdf", icon_path: null, ml_score: 0.92, frecency_score: 0.7, is_dir: false, category: null },
            { id: "90071992547409932", entry_type: "Setting", name: "Default Applications", path: "settings.default-apps", icon_path: null, ml_score: 0.7, frecency_score: 0.3, is_dir: false, category: "System" },
          ],
          latency_ms: 1, index_stale: false,
        };
        if (command === "set_global_hotkey") return args?.shortcut;
      },
    });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Open settings" }).click();
  await page.getByRole("tab", { name: "Keyboard shortcut" }).click();
  await page.getByRole("button", { name: "Change shortcut" }).click();
  await page.keyboard.press("Control+Alt+k");
  await expect(page.getByText(/Shortcut changed to Ctrl \+ Alt \+ K/)).toBeVisible();
  await page.getByRole("button", { name: "Done" }).click();
  await page.evaluate(() => {
    const metrics = window as unknown as { __UI_STARTED__: number; __UI_LATENCY__?: number };
    metrics.__UI_STARTED__ = performance.now();
    const observer = new MutationObserver(() => {
      if ([...document.querySelectorAll("button")].some((button) => button.textContent?.includes("document.pdf"))) {
        metrics.__UI_LATENCY__ = performance.now() - metrics.__UI_STARTED__;
        observer.disconnect();
      }
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });
  await page.getByRole("searchbox").fill("document");
  await expect(page.getByRole("button", { name: /document.pdf/i })).toBeVisible();
  const uiLatency = await page.evaluate(() => (window as unknown as { __UI_LATENCY__: number }).__UI_LATENCY__);
  expect(uiLatency).toBeLessThan(200);
  await expect(page.getByRole("button", { name: /Document Viewer/i })).toHaveAttribute("aria-current", "true");
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("button", { name: /document.pdf/i })).toHaveAttribute("aria-current", "true");
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("button", { name: /document.pdf/i })).toHaveAttribute("aria-current", "true");
  await page.keyboard.press("ArrowUp");
  await expect(page.getByRole("button", { name: /Document Viewer/i })).toHaveAttribute("aria-current", "true");
  if (process.env.CAPTURE_UI) await page.screenshot({ path: process.env.CAPTURE_UI, fullPage: true });
  await page.keyboard.press("Enter");
  await expect.poll(() => page.evaluate(() => {
    const calls = (window as unknown as {
      __SPEEDYSEARCH_CALL_ARGS__: Array<{ command: string; args?: Record<string, unknown> }>;
    }).__SPEEDYSEARCH_CALL_ARGS__;
    return calls.findLast(({ command }) => command === "open_result")?.args;
  })).toMatchObject({ closeWindow: true });
});
