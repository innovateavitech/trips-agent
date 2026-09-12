import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { getGridsFor, getSite } from '../../lib/api';
import { BlockList } from '../../components/blocks/blocks';

/**
 * Any page the agency built that is not the home page or the catalog: About, Contact, Terms, or one
 * of their own.
 *
 * Which pages exist is decided by the published version, not by this file — so an agency adding a
 * page in the console gets it on their site without a deploy.
 */

interface Props {
  params: Promise<{ slug: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const [{ slug }, site] = await Promise.all([params, getSite()]);
  const page = site?.content.pages.find((candidate) => candidate.slug === slug);

  if (!page) {
    return {};
  }

  return {
    title: page.metaTitle ?? page.title,
    description: page.metaDescription ?? undefined,
    alternates: { canonical: `/${slug}` },
  };
}

export default async function SitePage({ params }: Props) {
  const [{ slug }, site] = await Promise.all([params, getSite()]);

  if (!site) {
    notFound();
  }

  const page = site.content.pages.find((candidate) => candidate.slug === slug);

  if (!page || page.slug === 'home') {
    notFound();
  }

  const grids = await getGridsFor(site.siteId, page);

  return (
    <article>
      <BlockList blocks={page.blocks} site={site} gridProducts={grids} />
    </article>
  );
}
