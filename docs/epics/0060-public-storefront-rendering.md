# Epic #60 — Public storefront rendering

**Epic:** [#60](https://github.com/innovateavitech/trips-agent/issues/60) ·
**Module:** Storefront · **Milestone:** M2 — Storefront, catalog & customer commerce
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Host-based tenant resolution; per-site ISR with cache invalidation on publish; template
> rendering from site_versions; catalog browse and filter; product and departure detail; SEO
> metadata, sitemap and structured data. Nothing on these pages may reference Trips.

Twelve issues below cover all six of those. Two are the anonymous read API the site is built on,
three are the Next.js plumbing (host resolution, ISR, the invalidation job), two are rendering
(blocks and theme), three are the pages themselves, and two are SEO.

---

## The one sentence that shapes this whole epic

From [hard rule #4](../../CLAUDE.md):

> Nothing traveller-facing may reference Trips.

Every other epic in M2 has *some* Trips-branded surface — the agent console is ours, the emails to
agents are ours. **This epic has none.** Every byte a child issue here emits is read by the
agent's customer, on the agent's domain, under the agent's logo. There is no page in this epic
where our brand is acceptable, and that is not a styling preference — it is the product. An agent
pays us precisely so their customer never learns we exist.

That is why `S7` exists as its own issue rather than as a bullet on the rendering issue. A
white-label guarantee enforced by everybody remembering is not a guarantee. It needs a test that
fails the build.

The second shaping rule follows from it, and it is the one most likely to be got wrong: **the
tenant comes from the `Host` header and from nothing else.** No `?agency=` query parameter, no
body field, no path prefix — not even "just for local development". A storefront that accepts an
agency identifier from the request is a storefront where anyone can render any agency's site on
any domain, and where the list of our customers is enumerable by a stranger with a loop.

---

## Read this before picking anything up

**None of these can start today, and the reason is not the usual one.**

This epic sits on top of four sibling epics that have not been broken down:

- [#58](https://github.com/innovateavitech/trips-agent/issues/58) — website builder. Owns
  `site_versions`, `site_pages`, `site_blocks`, `site_themes`. `S6` and `S7` render what #58
  writes; **there is nothing to render until it exists.**
- [#59](https://github.com/innovateavitech/trips-agent/issues/59) — custom domains. Owns
  `site_domains` and the Redis-cached host→agency lookup. `S1` and `S3` are consumers of that
  lookup, not its authors.
- [#56](https://github.com/innovateavitech/trips-agent/issues/56) and
  [#57](https://github.com/innovateavitech/trips-agent/issues/57) — catalog and departures. `S2`,
  `S8`, `S9` and `S10` read tables neither of them has created yet.

So the `Depends on:` lines below point at **epics**, not at issues, and cannot be pinned to real
numbers until those four breakdowns land. **The honest build order is #58 and #59 first, then #56
and #57, then this.** Creating these twelve issues before then is fine — knowing the shape of the
work is useful — but assigning one is not.

All of M2 is additionally behind the **five commercial questions** in
[§7 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md). This epic is the least exposed to them of
any M2 epic, because it renders and does not transact — no child issue here takes money. The
questions that do bite it are 6, 11, 14, 21 and 26, all in
[What this breakdown found](#what-this-breakdown-found).

---

## The split

`S1`–`S12` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| S1 | Anonymous host-resolved storefront site API | ~2 days | **#58**, **#59** |
| S2 | Anonymous public catalog API — browse, filter, detail | ~2 days | S1, **#56**, **#57**, #28 |
| S3 | Next.js host-based tenant resolution | ~2 days | S1, #6 |
| S4 | Per-site ISR and cache tagging | ~2 days | S3 |
| S5 | `StorefrontCacheInvalidator` job | ~1 day | S4, #30, #31 |
| S6 | Template and block rendering from `site_versions` | ~2 days | S3, **#58** |
| S7 | Agent theming and the no-Trips guarantee | ~2 days | S6, **#58**, #18 |
| S8 | Catalog browse and filter pages | ~2 days | S2, S6 |
| S9 | Product detail — tours, packages and visas | ~2 days | S2, S6 |
| S10 | Departure detail and live availability | ~2 days | S9, **#57** |
| S11 | SEO metadata, Open Graph and canonical URLs | ~1 day | S6, S7 |
| S12 | Sitemap, robots and JSON-LD structured data | ~2 days | S9, S10, S4 |

All twelve carry `module:storefront` and the `M2` milestone. All twelve carry `blocked` on
creation, for the reasons above — this is the one epic where every child is blocked, so the label
is honest rather than noise.

**Order:** `S1` first; it is the contract everything else is written against, and settling it
badly means rewriting eight issues. Then `S3` and `S2` in parallel — one person on the Next.js
side, one on the API. `S6` next, because a page cannot exist before the block registry does, then
`S7` immediately after it. Do **not** leave theming until the end: retrofitting a white-label
guarantee onto pages built against default styling means touching every page a second time. `S8`,
`S9` and `S10` are then three independent pieces three people can take at once. `S4` can land any
time after `S3` but should land before `S8`–`S10`, so those pages are written cache-aware rather
than made cache-aware later. `S5` follows `S4`. `S11` and `S12` last, because SEO metadata
describes pages, and the pages must exist to be described.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### S1 · `Storefront: Anonymous host-resolved storefront site API` 🚫

#### What
The single unauthenticated endpoint that turns a `Host` header into everything needed to render
that agent's site — the published `site_versions` snapshot, the theme, the pages and blocks, and
the agent's branding.

#### Why it is its own issue, and first
The plan's M2 backend scope names a *"host-resolved anonymous storefront API with aggressive
caching"* and **no issue owns it** (see findings). It is the seam between the .NET side and the
Next.js side, and both `S3` and `S2` are written against it. Agreeing it before either starts is
the difference between one contract and two half-contracts that meet in the middle.

#### Why it is blocked
`site_versions` is [#58](https://github.com/innovateavitech/trips-agent/issues/58) and the
host→agency lookup is [#59](https://github.com/innovateavitech/trips-agent/issues/59). Both are
epics that have not been broken down.

#### Acceptance criteria
- [ ] `GET /public/site` — **no authentication**, and the agency is resolved from the request
      `Host` and from nothing else. No query parameter, no header and no body field may influence
      which tenant is served, in any environment
- [ ] The lookup goes through #59's Redis-cached `site_domains` resolver. This is described in
      [§2.4 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) as **the hottest lookup in the
      system**; it must not become a table hit per request
- [ ] Returns the `content_snapshot` and `theme_snapshot` of the site's **`published_version_id`**
      — never the draft, never the live authoring tables. Staging preview is #58's problem and
      uses a different, authenticated path
- [ ] An unknown host, a host with no site, and a site with no published version are **three
      different responses**, distinguishable in logs, because "my domain doesn't work" is
      otherwise unanswerable
- [ ] `ETag` and `Cache-Control` on the response, and a `304` on a matching `If-None-Match`
- [ ] The payload contains **no net rate, no markup, no margin** and no internal identifiers the
      traveller should not see. `margin.view` is a console permission
      ([§2.6](../ARCHITECTURE_AND_DELIVERY_PLAN.md)); there is no anonymous holder of it
- [ ] The word `Trips`, our domain and our support address appear nowhere in the response
      ([hard rule #4](../../CLAUDE.md))
- [ ] **No `.IgnoreQueryFilters()`.** [Hard rule #3](../../CLAUDE.md) — the tenant filter is the
      only thing standing between agency A's site and agency B's domain
- [ ] Rate limited per IP and per host. This is an unauthenticated endpoint on the public internet
      (see findings)
- [ ] Integration test: a request with `Host` set to agency A's domain returns **only** agency A's
      site, and no field of the request can change that

**Depends on:** **#58**, **#59**

---

### S2 · `Storefront: Anonymous public catalog API — browse, filter and detail` 🚫

#### What
The read side of the catalog for travellers — published products, their detail, and their
departures — resolved from the same `Host` and cached the same way.

#### Why it is blocked
It reads `products`, `departures` and their satellites, which are
[#56](https://github.com/innovateavitech/trips-agent/issues/56) and
[#57](https://github.com/innovateavitech/trips-agent/issues/57).

#### Acceptance criteria
- [ ] List endpoint: published products for the host's agency, filterable by `product_type`,
      category, theme, destination country and city, duration, price band and departure-date
      window (FRD §2.12 RS-5, via `product_categories`)
- [ ] Detail endpoint by `slug`, not by id — `UNIQUE (agency_id, slug)`
      ([§2.5](../ARCHITECTURE_AND_DELIVERY_PLAN.md)) means a slug is unique within the tenant, and
      the tenant is already fixed by the host
- [ ] Detail returns itinerary days, inclusions and exclusions, media, price variants, and
      `visa_details` plus `visa_document_requirements` for visa products
- [ ] **Only `status = published`.** A draft or archived product returns the same 404 as a product
      that never existed — an agent's unreleased tour and its price are commercially sensitive,
      and a distinguishable response makes them enumerable
- [ ] Departures for a product with status, remaining capacity and the tiered price for the
      requested pax count. Remaining capacity is
      `capacity_total - capacity_reserved - capacity_confirmed`, computed by the API and never by
      the client
- [ ] Every price is the **sell price** — net + markup + tax, from the engine in
      [#28](https://github.com/innovateavitech/trips-agent/issues/28). The breakdown that produced
      it is not in the response
- [ ] Money is `bigint` minor units with an explicit currency, never a pre-formatted string and
      never a decimal ([hard rule #2](../../CLAUDE.md))
- [ ] Pagination on the list endpoint, with a bounded maximum page size
- [ ] Cached in Redis keyed on host plus the filter set, carrying the cache tags `S5` will purge
- [ ] Test: agency B's published product is not reachable through agency A's host — not by slug,
      and not by any filter combination

**Depends on:** S1, **#56**, **#57**, #28

---

### S3 · `Storefront: Next.js host-based tenant resolution` 🚫

#### What
The Next.js side of the same rule — middleware that turns the incoming `Host` into a site context
every server component can read, and sensible pages when there is no site.

#### Why it is blocked
It calls `S1`, which is blocked on #58 and #59.

#### Acceptance criteria
- [ ] Middleware resolves the site from the `Host` header on every request and exposes it to
      server components as **request-scoped** context — not a module-level variable. A
      module-level cache is shared across tenants in the same process, and the failure mode is
      serving agency A's site to agency B's visitor under load
- [ ] `X-Forwarded-Host` is trusted **only** from the known proxy, and the issue names which. An
      unvalidated forwarded host is a client-supplied tenant identifier wearing a different hat
- [ ] Local development resolves through an explicit env override (`STOREFRONT_DEV_HOST`, added to
      `.env.example`) rather than a query parameter, and the override is inert unless
      `NODE_ENV !== 'production'`
- [ ] Unknown host → a plain, **unbranded** holding page. It cannot carry the agent's brand,
      because there is no agent; it must not carry ours either
      ([hard rule #4](../../CLAUDE.md)). Plain text and a status code
- [ ] Site exists but has no published version → an "opening soon" page in the agent's branding
- [ ] Site belongs to a suspended agency → **behaviour is undefined and must be decided before
      this ships** (open question 14 — see findings). Do not invent one silently
- [ ] Nothing traveller-facing hard-codes our brand. This includes the default `<title>`, the
      favicon, and any error page the framework generates on our behalf
- [ ] Test: two requests with different `Host` values, served concurrently by one process, return
      two different sites

**Depends on:** S1, #6

---

### S4 · `Storefront: Per-site ISR and cache tagging` 🚫

#### What
Incremental static regeneration scoped per site, and the cache tags that make targeted purging
possible.

#### Why tags and not a TTL
A short TTL means an agent publishes a price change and waits. A long TTL means they wait longer.
Tagging every rendered page with the site and the products it was built from lets publish purge
exactly what changed and leave everything else static — which is the only reason to use Next.js
here rather than the Vite stack the other three apps use.

#### Acceptance criteria
- [ ] Rendered pages are tagged `site:{siteId}`, and pages that read a product additionally
      `product:{productId}` — so one product's publish does not purge the whole site
- [ ] A conservative `revalidate` window as a **backstop**, not as the primary mechanism. If tags
      are the only thing keeping a page fresh, a lost invalidation message means a page that is
      stale forever
- [ ] An on-demand revalidation route authenticated by a shared secret from configuration, added
      to `.env.example`. It is a purge endpoint on the public internet; unauthenticated, it is a
      free cache-flush button for anyone who finds it
- [ ] The cache key includes the host. Two sites must never share a rendered page — a bug here
      shows agency A's homepage on agency B's domain, which is the worst rendering failure this
      product has
- [ ] Filter and pagination state lives in the URL, so a filtered catalog page is separately
      cacheable, shareable and crawlable
- [ ] Documented in a runbook: how to purge one site by hand when something has gone wrong

**Depends on:** S3

---

### S5 · `Storefront: StorefrontCacheInvalidator job` 🚫

#### What
Job 15 in [§3 of the plan](../ARCHITECTURE_AND_DELIVERY_PLAN.md) — on a site or product publish,
purge the caches for the affected host.

#### Acceptance criteria
- [ ] Consumes `SitePublished` and `ProductPublished` through MassTransit, deduplicated on
      `inbox_messages.message_id`
      ([#30](https://github.com/innovateavitech/trips-agent/issues/30))
- [ ] Purges the Redis entries from `S1` and `S2`, then calls `S4`'s revalidation route with the
      affected tags, then purges the CDN
- [ ] **Ordering matters, and the issue says why:** purge the origin cache *before* asking the CDN
      to refetch, or the CDN refetches the stale response and both layers are now confidently
      wrong
- [ ] Idempotent — replaying the same event purges the same things and breaks nothing
- [ ] Failure is retried with backoff and **alerts on exhaustion**. A silently failed purge means
      an agent published a correction their customers cannot see, and nobody finds out until a
      customer books at the old price
- [ ] The CDN purge goes through a port (`ICdnPurger`) with a no-op implementation, because **the
      cloud is not chosen yet** ([CLAUDE.md](../../CLAUDE.md) — see findings). The no-op logs
      loudly rather than returning success quietly
- [ ] A site's custom domain and its `*.tripsagent.com` subdomain are **both** purged. They are
      two hostnames and therefore two cache keys

**Depends on:** S4, #30, #31

---

### S6 · `Storefront: Template and block rendering from site_versions` 🚫

#### What
The renderer — takes a published `content_snapshot` and produces the page. Includes the block
registry for the seven block types in [§2.4](../ARCHITECTURE_AND_DELIVERY_PLAN.md).

#### Why it is blocked
It renders what [#58](https://github.com/innovateavitech/trips-agent/issues/58) writes.

#### Acceptance criteria
- [ ] A registry mapping `block_type` → component: `hero`, `featured_tours`, `rich_text`,
      `gallery`, `contact_form`, `trip_request_widget`, `faq`
- [ ] Blocks render in `position` order within their `site_pages` row
- [ ] System pages route by `page_type` — `home`, `about`, `contact`, `terms`, `catalog` — and
      `custom` pages route by `slug`
- [ ] **An unrecognised `block_type` renders nothing and logs; it does not throw.** The snapshot
      may have been written by an older or newer builder version than the renderer deployed right
      now, and a rollback must not take an agent's site down
- [ ] Same for a block whose `config` fails validation: that block is skipped and the page still
      serves. A broken FAQ section is a defect; a white page is an outage
- [ ] Rendering reads the **snapshot only**. It never queries the live `site_pages` or
      `site_blocks` tables — that is what makes instant rollback instant
- [ ] `rich_text` content is sanitised on render. It is agent-authored HTML served on the agent's
      own domain, and a stored XSS there runs against their customers
- [ ] `trip_request_widget` renders a placeholder until
      [#62](https://github.com/innovateavitech/trips-agent/issues/62)'s `C4` lands, and the issue
      says so rather than leaving a mystery gap
- [ ] Built from `packages/ui` with `cva` variants. **No hard-coded colour, font or spacing**
      ([hard rule #7](../../CLAUDE.md)); `pnpm check:design` passes

**Depends on:** S3, **#58**

---

### S7 · `Storefront: Agent theming and the no-Trips guarantee` 🚫

#### What
The agent's logo, colours and typography applied to the rendered site — and the automated check
that proves our brand did not leak into it.

#### Why the guarantee is an acceptance criterion and not an instruction
[Hard rule #4](../../CLAUDE.md) is the product. A rule enforced by everyone remembering it holds
until the first tired Friday. `pnpm check:design` already proves we do not hard-code colours; this
adds the sibling check that we do not hard-code *ourselves*.

#### Acceptance criteria
- [ ] `theme_snapshot` (`colors`, `typography`, `logo_asset_id`, `custom_css` from `site_themes`)
      is projected onto the **existing design tokens** as CSS custom properties at the document
      root. The agent overrides token *values*; no component reads a theme field directly
- [ ] Therefore `packages/ui` needs no storefront-specific variants to be themeable, and
      [hard rule #7](../../CLAUDE.md) still holds — the storefront hard-codes nothing, it
      substitutes
- [ ] A theme that omits a colour falls back to the token default and renders legibly. A
      half-configured theme must not produce black on black
- [ ] Logo, favicon and OG image come from the agent's assets via
      [#18](https://github.com/innovateavitech/trips-agent/issues/18)'s pipeline
- [ ] `custom_css` is injected scoped and sanitised — it cannot reach outside the page — and is
      bounded in size. It is the one field here an agent can break their own site with
- [ ] `analytics_ids` (the agent's own GA / Meta / TikTok tags from `sites`) are injected as the
      agent's, never ours. **We do not put our analytics on their site.** Consent is an open
      question (see findings)
- [ ] **The test:** an automated check renders every page type with a fixture theme and asserts
      that `Trips`, `tripsagent`, our support address and our logo asset appear nowhere in the
      output — markup, CSS, inline scripts, meta tags, `alt` text, or the generator meta tag Next
      emits by default. It runs in CI and fails the build
- [ ] The same check covers the framework's own error pages, which are the easiest place for a
      default brand to survive unnoticed

**Depends on:** S6, **#58**, #18

---

### S8 · `Storefront: Catalog browse and filter pages` 🚫

#### What
The traveller-facing listing — every published tour, package and visa the agent sells, with the
filters `S2` exposes.

#### Acceptance criteria
- [ ] Listing page with filters for product type, category and theme, destination, duration, price
      band and departure month, plus sorting
- [ ] **Filter state lives in the URL.** A filtered view must be shareable, back-button-correct
      and crawlable; state held only in React is none of those, and it also defeats `S4`'s cache
- [ ] Pagination, also in the URL
- [ ] Prices are sell prices, formatted from minor units at the display layer via `@trips/utils` —
      the conversion happens once, in one place
- [ ] A departure-bearing product shows its next available date and its status
      (`open` / `guaranteed` / `nearly_full` / `sold_out`) rather than a bare price
- [ ] **Empty states are written, not left blank.** Three distinct ones: the agent has published
      nothing yet, the filters match nothing, and the request errored. An agency's first week has
      an empty catalog, and this is the page they will judge the product on
- [ ] Images are responsive and lazy-loaded below the fold. This is a Nigerian mobile audience on
      metered data; a 4 MB hero image is a bounced customer
- [ ] Design system rules as `S6`; `pnpm check:design` passes

**Depends on:** S2, S6

---

### S9 · `Storefront: Product detail — tours, packages and visas` 🚫

#### What
One product, in full. Three shapes behind one route, because a visa is not a tour.

#### Acceptance criteria
- [ ] Route by `slug`. Gallery, description, destination, duration, inclusions and exclusions
- [ ] **Tour and package:** the day-by-day itinerary from `tour_itinerary_days`, with meals and
      accommodation
- [ ] **Visa:** `visa_details` — visa type, processing time, validity, entry type — and the
      applicant checklist from `visa_document_requirements` with mandatory items marked. Consular
      and service fees shown as the traveller will pay them
- [ ] Price variants (`product_price_variants`) selectable by occupancy and pax type, with the
      displayed price updating from the values `S2` returned — **never recomputed in the browser.**
      A price arithmetic'd client-side is a price the server never agreed to
- [ ] A clear call to action per product type. Where it leads to a cart it is a link into
      [#61](https://github.com/innovateavitech/trips-agent/issues/61); this issue does not build
      the cart
- [ ] An unpublished, archived or wrong-tenant slug renders the same 404
- [ ] Design system rules as `S6`; `pnpm check:design` passes

**Depends on:** S2, S6

---

### S10 · `Storefront: Departure detail and live availability` 🚫

#### What
The dated instances of a tour — what a traveller actually books when the product is a group
departure.

#### Why it is separate from `S9`
Departures are the only genuinely **live** data on the storefront. Everything else is a snapshot
that changes when an agent publishes; capacity changes while the page is open, and a page that
caches capacity the way it caches a description will cheerfully sell a sold-out seat.

#### Acceptance criteria
- [ ] Departure list for a product: date, status, price for the selected pax count from
      `departure_price_tiers`, and remaining capacity
- [ ] Tiered pricing recalculates **from the server** as the pax count changes
- [ ] Deposit terms shown when `deposit_type` is set — what is due now and what is due later, both
      in minor units. A traveller must not discover the balance after paying the deposit
- [ ] `cutoff_at` is respected: a departure past its cutoff is not bookable, and says so
- [ ] `sold_out` offers the waitlist (FRD §2.13 RS-6, `departure_waitlist`) instead of a dead end
- [ ] **Availability is fetched fresh, not served from the ISR snapshot.** The surrounding page may
      be static; the capacity number on it may not be
- [ ] The page never asserts a seat is held. Holds are `departure_holds` and belong to checkout
      ([#61](https://github.com/innovateavitech/trips-agent/issues/61)); this page shows
      availability at a moment in time, and its copy must not imply more
- [ ] Design system rules as `S6`; `pnpm check:design` passes

**Depends on:** S9, **#57**

---

### S11 · `Storefront: SEO metadata, Open Graph and canonical URLs` 🚫

#### What
Titles, descriptions, social cards and canonicals — per site and per page, from the agent's own
SEO fields.

#### Why this epic has an SEO issue at all
An agent's storefront is the whole reason they pay for a custom domain. If it does not rank, and
does not preview correctly when pasted into WhatsApp — which is how Nigerian travel is actually
sold — the domain is decoration.

#### Acceptance criteria
- [ ] Per-page title and description from `site_pages.meta` and `products.seo`, falling back to
      the site-level SEO fields on `sites`, falling back to a sensible derivation. Three levels,
      and no level is ever our brand
- [ ] Open Graph and Twitter card tags with the agent's OG image, correctly sized
- [ ] `og:site_name` is the **agent's** business name. This is the single tag most likely to leak
      a default
- [ ] A canonical URL on every page, pointing at the hostname in the site's `primary_domain_id` —
      so a site reachable on both its custom domain and its `*.tripsagent.com` subdomain is not
      two competing copies in an index
- [ ] Filtered and paginated catalog URLs canonicalise correctly rather than generating unbounded
      near-duplicate pages
- [ ] `lang` reflects the site's language rather than being hard-coded, even though M2 ships
      English only
- [ ] The `S7` no-Trips check covers every tag this issue adds

**Depends on:** S6, S7

---

### S12 · `Storefront: Sitemap, robots and JSON-LD structured data` 🚫

#### What
Per-site `sitemap.xml` and `robots.txt`, and the structured data that produces rich results.

#### Acceptance criteria
- [ ] `sitemap.xml` generated **per host**, listing that agency's system pages, custom pages,
      published products and bookable departures — and nothing belonging to any other agency. A
      sitemap that leaks across tenants publishes the leak to a search engine
- [ ] `lastmod` from the site version's `published_at` and the product's updated timestamp
- [ ] The sitemap is cached and tagged like every other page, and purged by `S5`
- [ ] `robots.txt` per host, allowing indexing of a published site and **disallowing it entirely**
      for a site that is unpublished, on a preview host, or on a domain that has not completed
      verification. An indexed preview outlives the preview
- [ ] JSON-LD on the relevant pages: `TouristTrip` or `Product` with `Offer` for products,
      `Event`-shaped data for dated departures, `Organization` and `LocalBusiness` for the agent,
      `BreadcrumbList` for navigation, and `FAQPage` where an FAQ block is present
- [ ] Offer prices in the structured data are the **same sell prices** the page displays, with the
      currency. Structured data that disagrees with the visible price earns a manual action from
      Google, not a ranking boost
- [ ] `Organization` names the agent — not us. Including in `publisher`, which is where a default
      most often survives
- [ ] Output validates against Google's Rich Results expectations for each type, checked in a test
      with fixtures rather than by pasting into a web form once

**Depends on:** S9, S10, S4

---

## What this breakdown found

### Gaps with no owner

1. **Nothing owned the anonymous storefront read API.** The plan's M2 backend scope names a
   *"host-resolved anonymous storefront API with aggressive caching"*, but
   [#58](https://github.com/innovateavitech/trips-agent/issues/58) is the authoring side,
   [#59](https://github.com/innovateavitech/trips-agent/issues/59) is domains, and
   [#56](https://github.com/innovateavitech/trips-agent/issues/56) /
   [#57](https://github.com/innovateavitech/trips-agent/issues/57) are catalog authoring.
   **Claimed here as `S1` and `S2`** — this epic's pages are its only consumers, so it belongs
   here. Worth confirming with whoever breaks down #58, so it is not built twice.

2. **Nothing owns traveller-facing flight search.** Open question 11 recommends relaxing the
   publish gate to *"at least one published product **or** flight search enabled on the site"* — a
   storefront where a traveller searches flights. **No issue in any milestone builds that.**
   [#52](https://github.com/innovateavitech/trips-agent/issues/52) is the **agent console** search,
   and the M2 frontend scope for the storefront does not list search at all. If question 11 is
   answered as recommended, this epic is missing an issue and the flight-only agent still cannot
   sell anything through their own site. Flagged rather than invented, because it is a product
   decision.

3. **No CDN exists to purge.** `S5`'s job description says "purges CDN", and
   [CLAUDE.md](../../CLAUDE.md) says the cloud is **not chosen yet** and forbids introducing a
   cloud-specific SDK without raising it first. `S5` therefore ships an `ICdnPurger` port with a
   loudly-logging no-op. That is correct for the constraint, but it means **cache invalidation is
   not verifiable end-to-end until hosting is decided** — and per-host wildcard domains with ISR
   materially narrow which hosting options work. Worth raising alongside the #59 breakdown, which
   has the same dependency for SSL.

4. **The public storefront ships in M2 with no platform rate limiter.** `S1` and `S2` are
   unauthenticated read endpoints on the public internet, and the platform rate limiter is `S1` of
   [#71](https://github.com/innovateavitech/trips-agent/issues/71) — **M3**. This is the same
   finding the [#62 breakdown](0062-crm-leads-quotes-pipeline.md) recorded for the trip-request
   widget, now with considerably more surface area. Interim per-IP and per-host limits are in
   `S1`'s criteria, explicitly superseded when #71's `S1` lands. Two epics have now independently
   hit this, which is probably an argument for pulling the rate limiter forward into M2.

5. **Nobody owns cookie or analytics consent.** `S7` injects the agent's own GA and Meta tags from
   `sites.analytics_ids`. Under the NDPA that is tracking, on a page read by Nigerian consumers,
   with no consent mechanism anywhere in the plan — and *we* built the page. Open question 26
   covers erasure and retention but not consent. Deliberately **not** folded into `S7`: a consent
   banner is a product and legal decision rather than an engineering one, and guessing produces
   either an illegal site or an annoying one.

### Blocked

- **All twelve.** Every child issue depends on at least one of #56, #57, #58 or #59, and all four
  are epics that have not been broken down. This is the only M2 epic where that is true of *every*
  child — a reasonable signal that **#60 should be broken down last and built last.**
- The `Depends on:` lines above point at epic numbers. They must be repointed at real child issue
  numbers once those four breakdowns land, and this file updated at the same time.

### Open questions this touches

- **6 — sub-agent branding.** `sites.agency_id` is `UNIQUE`
  ([§2.4](../ARCHITECTURE_AND_DELIVERY_PLAN.md)) — one site per tenant — and the plan recommends
  sub-agents selling under the principal's brand. `S1` and `S3` are built on that assumption. If
  the client instead wants sub-agents to have their own storefronts, host resolution stops being
  host → agency and becomes host → agency → *whose* catalog, and `S2`'s filtering changes with it.
  Cheap to decide now, expensive once `S8`–`S10` exist.
- **11 — the publish gate blocks flight-only agents.** The gate itself belongs to
  [#58](https://github.com/innovateavitech/trips-agent/issues/58), but `S8` inherits the answer: if
  the gate relaxes, a published site can have a genuinely empty catalog page, and that empty state
  stops being an edge case and becomes the normal, permanent state for a whole class of agent. See
  gap 2 — the same answer also creates missing work.
- **14 — suspended agents with live forward bookings.** `S3` must do *something* when a site
  belongs to a suspended agency, and the FRD says only "take the site offline". A traveller who
  paid for travel next month may be trying to reach that site right now. The criterion deliberately
  records the behaviour as undefined rather than picking one, because the wrong pick stays
  invisible until a real suspension.
- **21 — do storefront travellers get accounts?** The M2 frontend scope lists "order tracking,
  customer account" on the storefront. Those belong to
  [#61](https://github.com/innovateavitech/trips-agent/issues/61) and are deliberately **not** in
  this epic — but if the answer is "yes, accounts", the storefront grows authenticated pages, and a
  site that is entirely anonymous and entirely cacheable stops being either. `S4`'s caching design
  is the thing that would have to change.
- **17 — multi-currency.** `S2`, `S8`, `S9` and `S10` all display prices, and all assume the
  agent's base currency with an explicit currency code on the wire. That matches the plan's MVP
  recommendation. Every one of those issues stays cheap to extend later **only because** the
  currency travels with the amount rather than being assumed at the display layer — which is why it
  is an acceptance criterion rather than an assumption.
- **26 — NDPA.** Beyond the consent gap above: data residency. There is no Nigerian region on the
  major clouds, and the storefront is the surface a Nigerian consumer actually touches. Relevant to
  the hosting decision in gap 3, and to `S5`'s CDN choice.
