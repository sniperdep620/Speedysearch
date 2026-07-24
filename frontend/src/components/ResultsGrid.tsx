import { useMemo } from "react";
import type { LayoutMode, SearchResult } from "../types/search";
import ResultCard from "./ResultCard";
import "./ResultsGrid.css";

interface ResultsGridProps {
  query: string;
  results: SearchResult[];
  selectedIndex: number;
  onSelectIndex: (index: number) => void;
  onSelectResult: (result: SearchResult) => void;
  onPreviewHover: (result: SearchResult | null) => void;
  layoutMode: LayoutMode;
  isLoading: boolean;
  error: string | null;
  showScore: boolean;
}

export default function ResultsGrid(props: ResultsGridProps) {
  const indexed = useMemo(() => props.results.map((result, index) => ({ result, index })), [props.results]);
  if (props.isLoading && !props.results.length) {
    return <div className="message-state" role="status"><span className="large-spinner" />Searching…</div>;
  }
  if (props.error) {
    return <div className="message-state error-state" role="alert"><strong>Search unavailable</strong><span>{props.error}</span></div>;
  }
  if (!props.results.length) {
    return <div className="message-state"><strong>{props.query ? `No results for “${props.query}”` : "Start typing to search"}</strong><span>Apps, files and settings appear here.</span></div>;
  }

  const groups = [
    { title: "Applications", items: indexed.filter(({ result }) => result.entry_type === "App") },
    { title: "Files", items: indexed.filter(({ result }) => result.entry_type === "File") },
    { title: "Settings", items: indexed.filter(({ result }) => result.entry_type === "Setting" || result.entry_type === "Command") },
  ];
  const useColumns = props.layoutMode === "three-column" || (props.layoutMode === "dynamic" && groups.filter((group) => group.items.length).length > 1);

  return (
    <div className={`results-grid layout-${useColumns ? "three-column" : "single"}`} role="listbox" aria-label="Search results">
      {(useColumns ? groups : [{ title: "Best matches", items: indexed }]).map((group) => (
        <section className="result-column" key={group.title}>
          <h2 className="column-title"><span>{group.title}</span><span>{group.items.length}</span></h2>
          <div className="result-list">
            {group.items.length ? group.items.map(({ result, index }) => (
              <ResultCard
                key={result.id}
                result={result}
                isSelected={props.selectedIndex === index}
                showScore={props.showScore}
                onClick={() => props.onSelectResult(result)}
                onHover={() => { props.onSelectIndex(index); props.onPreviewHover(result); }}
                onHoverEnd={() => props.onPreviewHover(null)}
              />
            )) : <p className="no-results">No matches</p>}
          </div>
        </section>
      ))}
    </div>
  );
}
