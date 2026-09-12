import { getGridsFor, getSite } from '../lib/api';
import { BlockList } from '../components/blocks/blocks';
import { SiteNotice } from '../components/site-notice';

/** The agency's home page: the blocks of the home page in their published version. */
export default async function Home() {
  const site = await getSite();

  if (!site) {
    return null;
  }

  const page = site.content.pages.find((candidate) => candidate.slug === 'home');

  if (!page) {
    return <SiteNotice site={site} />;
  }

  const grids = await getGridsFor(site.siteId, page);

  return <BlockList blocks={page.blocks} site={site} gridProducts={grids} />;
}
