import Link from 'next/link';
import type { Block, ProductSummary, Site } from '../../lib/api';
import { PlainText } from '../plain-text';
import { ProductCard } from '../product-card';

/**
 * The four blocks a page is built from (docs/BUILD_PLAN.md, F4).
 *
 * A block type this renderer does not know is skipped rather than thrown on. A published version
 * outlives the code that rendered it: an old snapshot meeting a newer storefront, or a newer snapshot
 * meeting a storefront that has not deployed yet, must lose one section — never the whole site.
 */
export function BlockList({
  blocks,
  site,
  gridProducts,
}: {
  blocks: readonly Block[];
  site: Site;
  gridProducts: Record<number, ProductSummary[]>;
}) {
  return (
    <>
      {blocks.map((block, index) => (
        // A block has no id in the published snapshot; its position in the page is what identifies it.
        <BlockView key={index} block={block} site={site} products={gridProducts[index] ?? []} />
      ))}
    </>
  );
}

function BlockView({
  block,
  site,
  products,
}: {
  block: Block;
  site: Site;
  products: ProductSummary[];
}) {
  switch (block.type) {
    case 'Hero':
      return block.hero ? <Hero block={block.hero} site={site} /> : null;
    case 'Text':
      return block.text ? <TextSection block={block.text} /> : null;
    case 'ProductGrid':
      return block.productGrid ? (
        <ProductGrid heading={block.productGrid.heading} products={products} />
      ) : null;
    case 'Contact':
      return block.contact ? <Contact block={block.contact} site={site} /> : null;
    default:
      return null;
  }
}

function Hero({ block, site }: { block: NonNullable<Block['hero']>; site: Site }) {
  const imageUrl = block.imageAssetId ? site.images[block.imageAssetId] : undefined;

  return (
    <section className="relative isolate overflow-hidden bg-sidebar">
      {imageUrl && (
        <>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={imageUrl} alt="" className="absolute inset-0 h-full w-full object-cover" />
          {/* Holds the text legible over whatever the agency uploaded, light or dark. */}
          <div className="absolute inset-0 bg-sidebar/70" />
        </>
      )}

      <div className="relative mx-auto max-w-4xl px-4 py-20 text-center sm:px-6 sm:py-28">
        <h1 className="text-3xl font-semibold tracking-tight text-sidebar-foreground sm:text-5xl">
          {block.heading}
        </h1>

        {block.subheading && (
          <p className="mx-auto mt-4 max-w-2xl text-base text-sidebar-muted-foreground sm:text-lg">
            {block.subheading}
          </p>
        )}

        {block.ctaLabel && block.ctaHref && (
          <Link
            href={block.ctaHref}
            className="mt-8 inline-flex items-center justify-center rounded-md bg-primary px-6 py-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            {block.ctaLabel}
          </Link>
        )}
      </div>
    </section>
  );
}

function TextSection({ block }: { block: NonNullable<Block['text']> }) {
  return (
    <section className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      {block.heading && (
        <h2 className="mb-4 text-2xl font-semibold tracking-tight text-foreground">
          {block.heading}
        </h2>
      )}
      <PlainText text={block.body} className="space-y-4" />
    </section>
  );
}

function ProductGrid({ heading, products }: { heading: string; products: ProductSummary[] }) {
  if (products.length === 0) {
    return null;
  }

  return (
    <section className="mx-auto max-w-6xl px-4 py-12 sm:px-6">
      <h2 className="mb-6 text-2xl font-semibold tracking-tight text-foreground">{heading}</h2>

      <div className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
        {products.map((product) => (
          <ProductCard key={product.slug} product={product} />
        ))}
      </div>
    </section>
  );
}

function Contact({ block, site }: { block: NonNullable<Block['contact']>; site: Site }) {
  const { business } = site.content;

  const details = [
    block.showEmail && business.email
      ? { label: 'Email', value: business.email, href: `mailto:${business.email}` }
      : null,
    block.showPhone && business.phone
      ? { label: 'Phone', value: business.phone, href: `tel:${business.phone}` }
      : null,
    block.showWhatsApp && business.whatsApp
      ? {
          label: 'WhatsApp',
          value: business.whatsApp,
          href: `https://wa.me/${business.whatsApp.replace(/[^0-9]/g, '')}`,
        }
      : null,
  ].filter((detail) => detail !== null);

  return (
    <section className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <h2 className="text-2xl font-semibold tracking-tight text-foreground">{block.heading}</h2>

      {block.intro && <PlainText text={block.intro} className="mt-3 space-y-3" />}

      <dl className="mt-6 space-y-3">
        {details.map((detail) => (
          <div key={detail.label} className="flex flex-wrap gap-x-2 text-sm">
            <dt className="font-medium text-muted-foreground">{detail.label}</dt>
            <dd>
              <a href={detail.href} className="text-foreground underline-offset-4 hover:underline">
                {detail.value}
              </a>
            </dd>
          </div>
        ))}

        {block.showAddress && business.contactAddress && (
          <div className="flex flex-wrap gap-x-2 text-sm">
            <dt className="font-medium text-muted-foreground">Address</dt>
            <dd className="whitespace-pre-line text-foreground">{business.contactAddress}</dd>
          </div>
        )}
      </dl>
    </section>
  );
}
