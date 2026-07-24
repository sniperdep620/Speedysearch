import { memo } from "react";
import { convertFileSrc } from "@tauri-apps/api/core";
import type { SearchResult } from "../types/search";
import "./ResultCard.css";

interface ResultCardProps {
  result: SearchResult;
  isSelected: boolean;
  showScore: boolean;
  onClick: () => void;
  onHover: () => void;
  onHoverEnd: () => void;
}

function ResultCard({ result, isSelected, showScore, onClick, onHover, onHoverEnd }: ResultCardProps) {
  const image = result.icon_path?.startsWith("/") ? convertFileSrc(result.icon_path) : null;
  return (
    <button
      className={`result-card ${isSelected ? "selected" : ""}`}
      onClick={onClick}
      onMouseEnter={onHover}
      onMouseLeave={onHoverEnd}
      onFocus={onHover}
      aria-label={`${result.name}, ${result.entry_type}`}
      aria-current={isSelected ? "true" : undefined}
      type="button"
    >
      <span className="result-icon" aria-hidden="true">
        {image ? <img src={image} alt="" /> : <span>{iconFor(result)}</span>}
      </span>
      <span className="result-content">
        <span className="result-name" title={result.name}>{result.name}</span>
        <span className="result-path" title={result.path}>{result.category || result.path}</span>
      </span>
      {showScore && <span className="result-score" title="Ranking score">{Math.round(result.ml_score * 100)}%</span>}
    </button>
  );
}

function iconFor(result: SearchResult) {
  if (result.entry_type === "App") return "◆";
  if (result.entry_type === "Setting") return "⚙";
  if (result.entry_type === "Command") return ">_";
  return result.is_dir ? "□" : "◇";
}

export default memo(ResultCard);
