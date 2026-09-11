import { useEffect, useState } from 'react';

/**
 * The current time, re-read every `intervalMs`. Lets "waiting 2 days" keep counting on a screen
 * left open all morning without refetching anything.
 */
export function useNow(intervalMs = 60_000): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);

  return now;
}

/** Names the browser tab after the page, so a reviewer with six tabs open can find this one. */
export function useDocumentTitle(title: string) {
  useEffect(() => {
    document.title = `${title} | Trips back office`;
  }, [title]);
}
