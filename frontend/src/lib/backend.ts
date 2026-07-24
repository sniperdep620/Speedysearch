import { invoke } from "@tauri-apps/api/core";
import type { QueryResponse, UIConfig } from "../types/search";

declare global {
  interface Window {
    __SPEEDYSEARCH_MOCK__?: <T>(command: string, args?: Record<string, unknown>) => Promise<T>;
  }
}

async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  if (window.__SPEEDYSEARCH_MOCK__) return window.__SPEEDYSEARCH_MOCK__<T>(command, args);
  return invoke<T>(command, args);
}

export const backend = {
  search: (query: string) => call<QueryResponse>("search_query", { query }),
  logClick: (query: string, selectedId: string, rankPosition: number) =>
    call<void>("log_click", { query, selectedId, rankPosition }),
  openResult: (id: string, closeWindow = false) => call<void>("open_result", { id, closeWindow }),
  showInFolder: (id: string) => call<void>("show_in_folder", { id }),
  loadConfig: () => call<UIConfig>("load_ui_config"),
  saveConfig: (config: UIConfig) => call<UIConfig>("save_ui_config", { config }),
  suspendGlobalHotkey: () => call<void>("suspend_global_hotkey"),
  restoreGlobalHotkey: () => call<void>("restore_global_hotkey"),
  setGlobalHotkey: (shortcut: string) => call<string>("set_global_hotkey", { shortcut }),
  hideWindow: () => call<void>("hide_window"),
};
