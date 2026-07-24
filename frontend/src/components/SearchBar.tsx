import { useEffect, useRef } from "react";
import { listen } from "@tauri-apps/api/event";
import "./SearchBar.css";

interface SearchBarProps {
  query: string;
  onQueryChange: (query: string) => void;
  onFocus: () => void;
  isLoading: boolean;
  latency: number;
}

export default function SearchBar({ query, onQueryChange, onFocus, isLoading, latency }: SearchBarProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    inputRef.current?.focus();
    let disposed = false;
    let unlisten: (() => void) | undefined;
    listen("launcher-shown", () => inputRef.current?.focus()).then((cleanup) => {
      if (disposed) cleanup(); else unlisten = cleanup;
    }).catch(() => undefined);
    return () => { disposed = true; unlisten?.(); };
  }, []);

  return (
    <div className="search-bar-container" data-tauri-drag-region>
      <div className="search-bar">
        <span className="search-icon" aria-hidden="true">⌕</span>
        <input
          ref={inputRef}
          type="search"
          placeholder="Search apps, files, settings…"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          onFocus={onFocus}
          className="search-input"
          aria-label="Search apps, files and settings"
          autoComplete="off"
          spellCheck={false}
        />
        {isLoading && <span className="spinner" role="status" aria-label="Searching" />}
        {query && (
          <button className="clear-btn" onClick={() => onQueryChange("")} aria-label="Clear search">×</button>
        )}
      </div>
      {latency > 0 && <span className="latency-indicator" aria-label={`Search took ${latency} milliseconds`}>{latency} ms</span>}
    </div>
  );
}
