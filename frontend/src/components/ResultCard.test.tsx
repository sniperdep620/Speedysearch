import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { SearchResult } from "../types/search";
import ResultCard from "./ResultCard";

const result: SearchResult = {
  id: "18446744073709551615", entry_type: "File", name: "A very important document.pdf",
  path: "/home/test/Documents/A very important document.pdf", icon_path: null,
  ml_score: 0.91, frecency_score: 0.5, is_dir: false, category: null,
};

describe("ResultCard", () => {
  it("renders result metadata and score without truncating accessible text", () => {
    render(<ResultCard result={result} isSelected showScore onClick={vi.fn()} onHover={vi.fn()} onHoverEnd={vi.fn()} />);
    expect(screen.getByText(result.name)).toBeInTheDocument();
    expect(screen.getByText("91%")).toBeInTheDocument();
    expect(screen.getByRole("button")).toHaveAttribute("aria-current", "true");
  });

  it("activates and previews through pointer interactions", () => {
    const click = vi.fn();
    const hover = vi.fn();
    render(<ResultCard result={result} isSelected={false} showScore={false} onClick={click} onHover={hover} onHoverEnd={vi.fn()} />);
    fireEvent.mouseEnter(screen.getByRole("button"));
    fireEvent.click(screen.getByRole("button"));
    expect(hover).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledOnce();
  });
});
