import type { MetadataRoute } from 'next';
import { getSite } from '../lib/api';

/**
 * `robots.txt` for this hostname.
 *
 * Only a live site on its main address invites crawlers. A second hostname, a preview, a shop that is
 * shut and one that has not opened all say no — a page nobody should find yet is worse in an index
 * than not in one.
 */
export default async function robots(): Promise<MetadataRoute.Robots> {
  const site = await getSite();

  if (!site?.indexable) {
    return { rules: { userAgent: '*', disallow: '/' } };
  }

  return {
    rules: { userAgent: '*', allow: '/' },
    sitemap: `https://${site.primaryHostname}/sitemap.xml`,
  };
}
