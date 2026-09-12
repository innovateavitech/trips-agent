import { int64 } from '@trips/utils';
import Link from 'next/link';
import type { Site } from '../lib/api';

/**
 * The top of every page: the agency's logo or name, and their navigation.
 *
 * Nothing here names the platform (CLAUDE.md rule 4). The traveller is on their travel agent's
 * website, and as far as this page is concerned that is the only company that exists.
 */
export function SiteHeader({ site }: { site: Site }) {
  const logoUrl = site.theme.logoAssetId ? site.images[site.theme.logoAssetId] : undefined;

  const navigation = site.content.pages
    .filter((page) => page.showInNav)
    .sort((first, second) => int64(first.position) - int64(second.position));

  return (
    <header className="border-b border-border bg-background">
      <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-8 gap-y-3 px-4 py-4 sm:px-6">
        <Link href="/" className="flex shrink-0 items-center gap-3">
          {logoUrl ? (
            // The agency's own logo, at its own size. Next's image optimiser is not in front of it:
            // the link is signed and expires, so it cannot be fetched and cached by a second server.
            // eslint-disable-next-line @next/next/no-img-element
            <img src={logoUrl} alt={site.content.site.name} className="h-9 w-auto object-contain" />
          ) : (
            <span className="text-lg font-semibold tracking-tight text-foreground">
              {site.content.site.name}
            </span>
          )}
        </Link>

        {navigation.length > 0 && (
          <nav aria-label="Pages" className="flex flex-wrap items-center gap-x-6 gap-y-2">
            {navigation.map((page) => (
              <Link
                key={page.slug}
                href={page.slug === 'home' ? '/' : `/${page.slug}`}
                className="text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
              >
                {page.title}
              </Link>
            ))}
          </nav>
        )}
      </div>
    </header>
  );
}
