import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { SearchResult } from "../types/search";
import PreviewPane from "./PreviewPane";

const result: SearchResult = {
  id: "42", entry_type: "File", name: "notes.txt", path: "/tmp/notes.txt",
  icon_path: null, ml_score: 0.75, frecency_score: 0.2, is_dir: false, category: null,
};

describe("PreviewPane", () => {
  it("shows details and exposes file actions", () => {
    const action = vi.fn();
    render(<PreviewPane result={result} anchor="right" onAction={action} />);
    expect(screen.getByLabelText("Preview notes.txt")).toBeInTheDocument();
    expect(screen.getByText("/tmp/notes.txt")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Show in folder" }));
    expect(action).toHaveBeenCalledWith("show_in_folder");
  });

  it("can close the preview", () => {
    const action = vi.fn();
    render(<PreviewPane result={result} anchor="bottom" onAction={action} />);
    fireEvent.click(screen.getByRole("button", { name: "Close preview" }));
    expect(action).toHaveBeenCalledWith("close");
  });
});
