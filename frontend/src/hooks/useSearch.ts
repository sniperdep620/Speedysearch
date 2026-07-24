import { useEffect, useRef, useState } from "react";
import { backend } from "../lib/backend";
import type { QueryResponse } from "../types/search";

const EMPTY_RESPONSE: QueryResponse = { results: [], latency_ms: 0, index_stale: false };

export function useSearch(query: string, delay = 100) {
  const [response, setResponse] = useState<QueryResponse>(EMPTY_RESPONSE);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const request = useRef(0);

  useEffect(() => {
    const normalized = query.trim();
    const current = ++request.current;
    if (!normalized) {
      setResponse(EMPTY_RESPONSE);
      setIsLoading(false);
      setError(null);
      return;
    }
    setIsLoading(true);
    const timer = window.setTimeout(async () => {
      try {
        const next = await backend.search(query);
        if (request.current === current) {
          setResponse(next);
          setError(null);
        }
      } catch (cause) {
        if (request.current === current) {
          setResponse(EMPTY_RESPONSE);
          setError(cause instanceof Error ? cause.message : String(cause));
        }
      } finally {
        if (request.current === current) setIsLoading(false);
      }
    }, delay);
    return () => window.clearTimeout(timer);
  }, [delay, query]);

  return { ...response, isLoading, error };
}
