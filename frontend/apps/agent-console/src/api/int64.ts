/**
 * The API's 64-bit integers — kobo amounts, counts — arrive typed `number | string`, because the
 * server's JSON reader also accepts them written as strings. It always writes numbers; this reads
 * either, and refuses anything that is not a whole, safe number rather than guess at money.
 */
export function int64(value: number | string): number {
  const parsed = typeof value === 'number' ? value : Number(value);

  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`Expected a whole number from the API, got ${String(value)}.`);
  }

  return parsed;
}
