import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import SearchBar from "./SearchBar";

describe("SearchBar", () => {
  it("focuses and renders an accessible search field", () => {
    render(<SearchBar query="" onQueryChange={vi.fn()} onFocus={vi.fn()} isLoading={false} latency={0} />);
    const input = screen.getByRole("searchbox", { name: /search apps/i });
    expect(input).toHaveAttribute("placeholder", expect.stringMatching(/Search apps/));
    expect(input).toHaveFocus();
  });

  it("reports typed text and clears an existing query", () => {
    const onChange = vi.fn();
    const { rerender } = render(<SearchBar query="" onQueryChange={onChange} onFocus={vi.fn()} isLoading={false} latency={0} />);
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "document" } });
    expect(onChange).toHaveBeenCalledWith("document");
    rerender(<SearchBar query="document" onQueryChange={onChange} onFocus={vi.fn()} isLoading={false} latency={2} />);
    fireEvent.click(screen.getByRole("button", { name: /clear search/i }));
    expect(onChange).toHaveBeenCalledWith("");
    expect(screen.getByText("2 ms")).toBeInTheDocument();
  });
});
