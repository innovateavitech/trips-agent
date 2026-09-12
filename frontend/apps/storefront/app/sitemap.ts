import type { MetadataRoute } from 'next';
import { getSitemap } from '../lib/api';

/**
 * `sitemap.xml`, built from the site that answers on this hostname.
 *
 * The addresses come from the API against the site's *main* hostname, not the one this request
 * arrived on: a site can answer on several, and pointing a search engine at more than one copy of
 * the same page splits its ranking between them.
 */
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const map = await getSitemap();

  if (!map) {
    return [];
  }

  const pages = map.entries.map((entry) => ({
    url: `${map.baseUrl}${entry.path}`,
    lastModified: new Date(entry.lastModified),
    changeFrequency: entry.changeFrequency as MetadataRoute.Sitemap[number]['changeFrequency'],
  }));

  // The enquiry form is the storefront's own route rather than a page the agent built, so the API
  // does not know about it — but it is the page most worth finding, so it is listed here. A quote's
  // page is deliberately never listed: its address is its secret.
  return [...pages, { url: `${map.baseUrl}/enquire`, changeFrequency: 'monthly' as const }];
}
