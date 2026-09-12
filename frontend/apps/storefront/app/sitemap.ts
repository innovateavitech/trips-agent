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

  return map.entries.map((entry) => ({
    url: `${map.baseUrl}${entry.path}`,
    lastModified: new Date(entry.lastModified),
    changeFrequency: entry.changeFrequency as MetadataRoute.Sitemap[number]['changeFrequency'],
  }));
}
