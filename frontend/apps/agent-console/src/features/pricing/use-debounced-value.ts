import { useEffect, useState } from 'react';

/**
 * `value`, but only once it has stopped changing for `delayMs`.
 *
 * The preview asks the server on every change; without this, typing "250000"
 * sends six requests. Pass primitives only: an object literal is a new value on
 * every render, so it would never settle.
 */
export function useDebouncedValue<T extends string | number | boolean | null>(
  value: T,
  delayMs: number,
): T {
  const [settled, setSettled] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return settled;
}
