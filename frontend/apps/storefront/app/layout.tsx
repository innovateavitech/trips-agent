import type { ReactNode } from 'react';
import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { getSite } from '../lib/api';
import { themeVariables } from '../lib/theme';
import { SiteFooter } from '../components/site-footer';
import { SiteHeader } from '../components/site-header';
import './globals.css';

/**
 * Every page of every agency's website.
 *
 * The agency is resolved from the request's hostname, and the page is rendered in their name, their
 * logo and their colours (CLAUDE.md rule 4). Nothing on it — not a title, not a footer, not a
 * "powered by" — may say who built it.
 */

/**
 * The title and description search engines see, and whether they may index this address at all.
 *
 * `indexable` is the API's answer, not ours: only a live site on its main hostname is worth
 * indexing. A second hostname would otherwise compete with the first for the same pages, and a shop
 * that is shut or not yet open has nothing to find.
 */
export async function generateMetadata(): Promise<Metadata> {
  const site = await getSite();

  if (!site) {
    return { title: 'Not found', robots: { index: false, follow: false } };
  }

  const { site: settings } = site.content;
  const title = settings.seoTitle ?? settings.name;

  return {
    title: { default: title, template: `%s · ${settings.name}` },
    description: settings.seoDescription ?? undefined,
    metadataBase: new URL(`https://${site.primaryHostname}`),
    alternates: { canonical: '/' },
    robots: site.indexable ? undefined : { index: false, follow: false },
    openGraph: {
      siteName: settings.name,
      title,
      description: settings.seoDescription ?? undefined,
      type: 'website',
    },
  };
}

export default async function RootLayout({ children }: { children: ReactNode }) {
  const site = await getSite();

  if (!site) {
    notFound();
  }

  // The agency's brand colour, written into the design system's own token variables. Every component
  // below still uses `bg-primary` and friends; only the value behind the name changes. See lib/theme.
  const variables = themeVariables(site);

  return (
    <html lang={site.content.site.language || 'en'} className="font-sans">
      <body
        className="flex min-h-screen flex-col bg-background text-foreground"
        style={variables ? parseStyle(variables) : undefined}
      >
        <SiteHeader site={site} />
        <main className="flex-1">{children}</main>
        <SiteFooter site={site} />
      </body>
    </html>
  );
}

/**
 * The declaration block from `themeVariables` as React's style object.
 *
 * React will not take a raw CSS string, and a `<style>` tag would need a selector to hang the
 * variables on. Setting them on `body` keeps them scoped to the page without one.
 */
function parseStyle(declarations: string): Record<string, string> {
  const style: Record<string, string> = {};

  for (const declaration of declarations.split(';')) {
    const [name, ...rest] = declaration.split(':');

    if (name?.trim() && rest.length > 0) {
      style[name.trim()] = rest.join(':').trim();
    }
  }

  return style;
}
