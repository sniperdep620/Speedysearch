export type EntryType = "App" | "File" | "Setting" | "Command";

export interface SearchResult {
  id: string;
  entry_type: EntryType;
  name: string;
  path: string;
  icon_path?: string | null;
  ml_score: number;
  frecency_score: number;
  is_dir: boolean;
  category?: string | null;
}

export interface QueryResponse {
  results: SearchResult[];
  latency_ms: number;
  index_stale: boolean;
}

export type AppState = "resting" | "searching" | "loading" | "results" | "preview_open";
export type LayoutMode = "single" | "three-column" | "dynamic";
export type PreviewAnchor = "left" | "right" | "bottom";

export interface UIConfig {
  layout_mode: LayoutMode;
  preview_anchor: PreviewAnchor;
  opacity: number;
  animation_duration_ms: number;
  global_hotkey: string;
  show_frecency: boolean;
  highlight_active_app: boolean;
}

export const DEFAULT_CONFIG: UIConfig = {
  layout_mode: "three-column",
  preview_anchor: "right",
  opacity: 0.95,
  animation_duration_ms: 200,
  global_hotkey: "Ctrl+Space",
  show_frecency: true,
  highlight_active_app: true,
};
