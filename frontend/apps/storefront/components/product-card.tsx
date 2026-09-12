import Link from 'next/link';
import { formatMoneyShort, int64 } from '@trips/utils';
import type { ProductSummary } from '../lib/api';

/**
 * One product in a grid or a list.
 *
 * The only price shown is `fromPriceMinor` — the sell price the traveller pays. A net rate and a
 * markup are the agency's business and never leave the console (CLAUDE.md rules 4 and 5).
 */
export function ProductCard({ product }: { product: ProductSummary }) {
  const place = [product.destinationCity, product.destinationCountry].filter(Boolean).join(', ');

  return (
    <Link
      href={`/tours/${product.slug}`}
      className="group flex flex-col overflow-hidden rounded-lg border border-border bg-card transition-shadow hover:shadow-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
    >
      <div className="aspect-[3/2] w-full overflow-hidden bg-muted">
        {product.imageUrl && (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={product.imageUrl}
            alt=""
            className="h-full w-full object-cover transition-transform duration-300 group-hover:scale-105"
          />
        )}
      </div>

      <div className="flex flex-1 flex-col gap-2 p-4">
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {product.productType}
          {place && <span className="normal-case tracking-normal"> &middot; {place}</span>}
        </p>

        <h3 className="text-base font-semibold leading-snug text-foreground">{product.title}</h3>

        {product.summary && (
          <p className="line-clamp-2 text-sm text-muted-foreground">{product.summary}</p>
        )}

        <div className="mt-auto flex items-baseline gap-2 pt-2">
          <span className="text-xs text-muted-foreground">from</span>
          <span className="text-lg font-semibold text-foreground">
            {formatMoneyShort(int64(product.fromPriceMinor), product.currency)}
          </span>
          {product.durationDays ? (
            <span className="ml-auto text-xs text-muted-foreground">
              {product.durationDays} {int64(product.durationDays) === 1 ? 'day' : 'days'}
            </span>
          ) : null}
        </div>
      </div>
    </Link>
  );
}
