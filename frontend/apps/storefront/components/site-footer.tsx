import type { Site } from '../lib/api';

/**
 * The bottom of every page: how to reach the agency, and their copyright.
 *
 * The notice names the agency and the year, and nobody else (CLAUDE.md rule 4). There is no "powered
 * by" line here and there must never be one — the whole product is that the traveller cannot tell we
 * exist.
 */
export function SiteFooter({ site }: { site: Site }) {
  const { business, site: settings } = site.content;
  const year = new Date().getFullYear();

  const contacts = [
    business.email
      ? { label: 'Email', value: business.email, href: `mailto:${business.email}` }
      : null,
    business.phone
      ? { label: 'Phone', value: business.phone, href: `tel:${business.phone}` }
      : null,
    business.whatsApp
      ? {
          label: 'WhatsApp',
          value: business.whatsApp,
          href: `https://wa.me/${business.whatsApp.replace(/[^0-9]/g, '')}`,
        }
      : null,
  ].filter((contact) => contact !== null);

  return (
    <footer className="mt-16 border-t border-border bg-muted">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-10 sm:px-6 md:grid-cols-2">
        <div className="space-y-2">
          <p className="text-base font-semibold text-foreground">{settings.name}</p>
          {business.contactAddress && (
            <p className="whitespace-pre-line text-sm text-muted-foreground">
              {business.contactAddress}
            </p>
          )}
        </div>

        {(contacts.length > 0 || business.socialLinks.length > 0) && (
          <div className="space-y-3">
            {contacts.length > 0 && (
              <ul className="space-y-1 text-sm">
                {contacts.map((contact) => (
                  <li key={contact.label}>
                    <span className="text-muted-foreground">{contact.label}: </span>
                    <a
                      href={contact.href}
                      className="text-foreground underline-offset-4 hover:underline"
                    >
                      {contact.value}
                    </a>
                  </li>
                ))}
              </ul>
            )}

            {business.socialLinks.length > 0 && (
              <ul className="flex flex-wrap gap-4 text-sm">
                {business.socialLinks.map((link) => (
                  <li key={link.network}>
                    <a
                      href={link.url}
                      rel="noreferrer noopener"
                      target="_blank"
                      className="capitalize text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
                    >
                      {link.network}
                    </a>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>

      <div className="border-t border-border">
        <p className="mx-auto max-w-6xl px-4 py-4 text-xs text-muted-foreground sm:px-6">
          &copy; {year} {settings.name}. All rights reserved.
        </p>
      </div>
    </footer>
  );
}
