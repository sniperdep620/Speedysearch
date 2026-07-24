import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { LogicalSize } from "@tauri-apps/api/dpi";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { backend } from "./lib/backend";
import SearchBar from "./components/SearchBar";
import ResultsGrid from "./components/ResultsGrid";
import SettingsPanel from "./components/SettingsPanel";
import { useConfig } from "./hooks/useConfig";
import { useKeyboardNav } from "./hooks/useKeyboardNav";
import { useSearch } from "./hooks/useSearch";
import type { AppState, SearchResult } from "./types/search";
import "./styles/global.css";

const PreviewPane = lazy(() => import("./components/PreviewPane"));

export default function App() {
  const [query, setQuery] = useState("");
  const [focused, setFocused] = useState(false);
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [preview, setPreview] = useState<SearchResult | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const previewTimer = useRef<number | null>(null);
  const { config, saveConfig, configError } = useConfig();
  const { results, latency_ms: latency, index_stale: indexStale, isLoading, error } = useSearch(query);

  const appState: AppState = preview ? "preview_open" : isLoading ? "loading" : results.length ? "results" : focused || query ? "searching" : "resting";
  useEffect(() => { setSelectedIndex(0); setPreview(null); }, [query, results.length]);
  useEffect(() => { if (configError) setStatus(`Settings: ${configError}`); }, [configError]);

  useEffect(() => {
    const categoryRows = ["App", "File", "Setting"]
      .map((type) => results.filter((result) => type === "Setting"
        ? result.entry_type === "Setting" || result.entry_type === "Command"
        : result.entry_type === type).length);
    const nonEmptyCategories = categoryRows.filter(Boolean).length;
    const useColumns = config.layout_mode === "three-column"
      || (config.layout_mode === "dynamic" && nonEmptyCategories > 1);
    const rows = useColumns ? Math.max(1, ...categoryRows) : Math.max(1, results.length);
    const height = settingsOpen ? 650
      : preview ? 560
      : !query ? 300
      : !results.length ? 390
      : Math.min(650, 340 + (rows - 1) * 68);
    try {
      getCurrentWindow().setSize(new LogicalSize(1040, height)).catch(() => undefined);
    } catch {
      // The browser-only test harness has no native window.
    }
  }, [config.layout_mode, preview, query, results, settingsOpen]);

  const selectResult = useCallback((result: SearchResult, closeWindow = false) => {
    const rankPosition = results.findIndex((candidate) => candidate.id === result.id) + 1;
    window.setTimeout(() => backend.logClick(query, result.id, rankPosition).catch(console.error), 50);
    backend.openResult(result.id, closeWindow).then(() => {
      setQuery("");
      setPreview(null);
      setStatus(`Opened ${result.name}`);
    }).catch((cause) => setStatus(String(cause)));
  }, [query, results]);

  const activateIndex = useCallback((index: number) => {
    const result = results[index];
    if (result) selectResult(result, true);
  }, [results, selectResult]);

  const escape = useCallback(() => {
    if (settingsOpen) return setSettingsOpen(false);
    if (preview) return setPreview(null);
    if (query) return setQuery("");
    backend.hideWindow().catch(() => undefined);
  }, [preview, query, settingsOpen]);

  useKeyboardNav({ enabled: !settingsOpen, selectedIndex, totalResults: results.length, onSelectIndex: setSelectedIndex, onActivate: activateIndex, onEscape: escape });

  const keepPreviewOpen = useCallback(() => {
    if (previewTimer.current !== null) window.clearTimeout(previewTimer.current);
  }, []);
  const updatePreview = useCallback((result: SearchResult | null) => {
    keepPreviewOpen();
    if (result) setPreview(result);
    else previewTimer.current = window.setTimeout(() => setPreview(null), 180);
  }, [keepPreviewOpen]);

  const previewAction = useCallback(async (action: "open" | "show_in_folder" | "copy_path" | "close") => {
    if (!preview) return;
    if (action === "close") return setPreview(null);
    if (action === "open") return selectResult(preview);
    try {
      if (action === "show_in_folder") await backend.showInFolder(preview.id);
      else await navigator.clipboard.writeText(preview.path);
      setStatus(action === "copy_path" ? "Path copied" : "Opened containing folder");
    } catch (cause) { setStatus(String(cause)); }
  }, [preview, selectResult]);

  const shellStyle = useMemo(() => ({
    "--surface-opacity": config.opacity,
    "--transition-duration": `${config.animation_duration_ms}ms`,
  }) as React.CSSProperties, [config.animation_duration_ms, config.opacity]);

  const startWindowDrag = useCallback((event: React.PointerEvent<HTMLElement>) => {
    if (event.button !== 0 || (event.target as HTMLElement).closest("button, input, select")) return;
    try {
      getCurrentWindow().startDragging().catch(() => undefined);
    } catch {
      // The browser-only test harness has no native window.
    }
  }, []);

  return (
    <main className={`app-shell state-${appState} preview-${config.preview_anchor}${preview ? " has-preview" : ""}`} style={shellStyle}>
      <section className="launcher-surface">
        <header className="launcher-header" onPointerDown={startWindowDrag}>
          <div className="brand"><span className="brand-mark">✦</span><span><strong>Speedysearch</strong></span></div>
          <button className="settings-button" onClick={() => setSettingsOpen(true)} aria-label="Open settings">⌘</button>
        </header>
        <SearchBar query={query} onQueryChange={setQuery} onFocus={() => setFocused(true)} isLoading={isLoading} latency={latency} />
        {appState !== "resting" && (
          <ResultsGrid query={query} results={results} selectedIndex={selectedIndex} onSelectIndex={setSelectedIndex} onSelectResult={selectResult} onPreviewHover={updatePreview} layoutMode={config.layout_mode} isLoading={isLoading} error={error} showScore={config.show_frecency} />
        )}
        <footer className="launcher-footer"><span>{status || (indexStale ? "Index refreshing" : results.length ? `${results.length} local matches` : "Private · local · instant")}</span><span><kbd>↑↓←→</kbd> navigate <kbd>↵</kbd> open <kbd>esc</kbd> close</span></footer>
      </section>
      {preview && <Suspense fallback={null}><PreviewPane result={preview} anchor={config.preview_anchor} onAction={previewAction} onKeepOpen={keepPreviewOpen} onLeave={() => updatePreview(null)} /></Suspense>}
      {settingsOpen && <SettingsPanel config={config} error={configError} onConfigChange={saveConfig} onClose={() => setSettingsOpen(false)} />}
      <div className="sr-only" aria-live="polite">{isLoading ? "Searching" : `${results.length} results`}</div>
    </main>
  );
}
