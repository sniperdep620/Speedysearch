import { convertFileSrc } from "@tauri-apps/api/core";
import type { PreviewAnchor, SearchResult } from "../types/search";
import "./PreviewPane.css";

interface PreviewPaneProps {
  result: SearchResult;
  anchor: PreviewAnchor;
  onAction: (action: "open" | "show_in_folder" | "copy_path" | "close") => void;
  onKeepOpen?: () => void;
  onLeave?: () => void;
}

export default function PreviewPane({ result, anchor, onAction, onKeepOpen, onLeave }: PreviewPaneProps) {
  const image = result.icon_path?.startsWith("/") ? convertFileSrc(result.icon_path) : null;
  return (
    <aside className={`preview-pane anchor-${anchor}`} aria-label={`Preview ${result.name}`} onMouseEnter={onKeepOpen} onMouseLeave={onLeave}>
      <header className="preview-header"><span>Preview</span><button onClick={() => onAction("close")} aria-label="Close preview">×</button></header>
      <div className="preview-identity">
        <span className="preview-icon">{image ? <img src={image} alt="" /> : result.entry_type === "Setting" ? "⚙" : "◇"}</span>
        <div><strong>{result.name}</strong><span>{result.entry_type}</span></div>
      </div>
      <dl className="preview-info">
        <div><dt>Location</dt><dd title={result.path}>{result.path}</dd></div>
        {result.category && <div><dt>Category</dt><dd>{result.category}</dd></div>}
        <div><dt>Rank score</dt><dd>{Math.round(result.ml_score * 100)}%</dd></div>
      </dl>
      <div className="preview-actions">
        <button className="primary-action" onClick={() => onAction("open")}>Open</button>
        {result.entry_type === "File" && <button onClick={() => onAction("show_in_folder")}>Show in folder</button>}
        <button onClick={() => onAction("copy_path")}>Copy path</button>
      </div>
    </aside>
  );
}
