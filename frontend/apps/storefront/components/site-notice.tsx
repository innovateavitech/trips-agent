import type { Site } from '../lib/api';

/**
 * What a site shows when it has nothing published to show.
 *
 * A shop that has not opened yet and one that is suspended (MVP decision 14) both answer here, in the
 * agency's own name and colours. A traveller who followed a link to an agency's address must not be
 * handed a stranger's error page, and must certainly not be handed ours (CLAUDE.md rule 4).
 */
export function SiteNotice({ site }: { site: Site }) {
  const name = site.content.site.name;

  const message =
    site.status === 'Offline'
      ? 'Our website is temporarily unavailable. Please get in touch and we will be glad to help.'
      : 'Our website is on its way. Please get in touch in the meantime — we are already taking bookings.';

  const { business } = site.content;

  return (
    <section className="mx-auto flex max-w-2xl flex-col items-center gap-4 px-4 py-24 text-center sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">{name}</h1>
      <p className="text-base text-muted-foreground">{message}</p>

      {(business.email || business.phone) && (
        <p className="text-sm text-foreground">
          {business.email && (
            <a href={`mailto:${business.email}`} className="underline-offset-4 hover:underline">
              {business.email}
            </a>
          )}
          {business.email && business.phone && (
            <span className="text-muted-foreground"> &middot; </span>
          )}
          {business.phone && (
            <a href={`tel:${business.phone}`} className="underline-offset-4 hover:underline">
              {business.phone}
            </a>
          )}
        </p>
      )}
    </section>
  );
}
