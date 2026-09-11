import { useEffect, useState } from 'react';
import { secondsUntil } from './search-rules';

/**
 * Seconds until `expiresAt`, ticking once a second and stopping at zero.
 * One hook for the banner and the cards, so they cannot disagree about whether
 * the fares have expired.
 */
export function useCountdown(expiresAt: string): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    setNow(Date.now());
    const timer = setInterval(() => {
      const current = Date.now();
      setNow(current);
      if (secondsUntil(expiresAt, current) === 0) clearInterval(timer);
    }, 1000);
    return () => clearInterval(timer);
  }, [expiresAt]);

  return secondsUntil(expiresAt, now);
}
