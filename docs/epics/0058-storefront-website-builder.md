# Epic #58 — Storefront: Website builder

**Epic:** [#58](https://github.com/innovateavitech/trips-agent/issues/58) ·
**Module:** Storefront · **Milestone:** M2 — Storefront, catalog & customer commerce
**Status:** breakdown proposed, child issues not yet created

---

## What the epic asks for

> Template library; logo and colour upload; block-based page editing; About/Contact/Terms;
> staging preview; publish with rollback via versioned snapshots. FRD blocks publishing until a
> product is published — see open question 11, which may relax this for flight-only agents.

Eleven issues below cover all six of those. Three are the write model (schema, templates,
versions), three are the editing surface (pages, blocks, theme), two are preview and publish, and
three are the agent-console screens that make any of it usable.

---

## The two sentences that shape this whole epic

**1. This epic writes the site. It does not render it.**

[#60](https://github.com/innovateavitech/trips-agent/issues/60) resolves a `Host` header to a site
and renders the published snapshot; this epic produces that snapshot. The seam between them is
`site_versions.content_snapshot jsonb` — a stored, versioned, cross-epic interface that outlives
both. It is defined in `W3` and consumed by #60, and it is the most consequential decision in this
breakdown. Get it wrong and every published site in the database is wrong, with no code change
able to fix those rows retroactively.

**2. Everything this epic produces is read by the agent's customer.**

[Hard rule #4](../../CLAUDE.md): nothing traveller-facing may reference Trips. A site builder is
the most direct expression of that rule in the product — the entire point is that the output looks
like the agent's own website. Two places in this epic quietly break it if nobody is watching: a
default template that ships our copy or our palette, and a preview link on our domain that an
agent forwards to their customer.

---

## Read this before picking anything up

**Do not start `W7` or `W10` yet.** Staging preview renders a *draft* version, and the renderer is
[#60](https://github.com/innovateavitech/trips-agent/issues/60) — an epic that has not been broken
down. Their real dependencies are child issues that do not exist yet, so their `Depends on:` lines
can only be pinned to numbers once that breakdown lands. The same pattern was recorded for `C4`
and `C7` in [the CRM breakdown](0062-crm-leads-quotes-pipeline.md).

`W8` is not blocked, but it walks straight into
**[open question 11](../ARCHITECTURE_AND_DELIVERY_PLAN.md)** — the FRD's publish gate makes it
impossible for a flight-only agent ever to go live. `W8` ships the gate as *configuration* rather
than a constant, precisely so that answering question 11 later is a settings change and not a
migration.

`W1`–`W6`, `W9` and `W11` need only M1 foundations. `W1` is where the epic starts, and nothing
else in it compiles until `W1` is merged.

---

## The split

`W1`–`W11` are placeholders. They become real issue numbers when the issues are created, and the
`Depends on:` lines must be rewritten to match at that point.

| | Proposed issue | Size | Depends on |
|---|---|---|---|
| W1 | Site builder schema | ~2 days | #8, #11, #12 |
| W2 | Template library and site provisioning | ~2 days | W1, #18 |
| W3 | Draft versions, snapshots and copy-on-write | ~2 days | W1 |
| W4 | Site pages — system pages and custom pages | ~2 days | W3 |
| W5 | Block editing — add, configure, reorder, remove | ~2 days | W4 |
| W6 | Theme editor — logo, colours and typography | ~2 days | W3, #18 |
| W7 | Staging preview 🚫 | ~2 days | W3, **#60** — **blocked** |
| W8 | Publish, the publish gate and rollback | ~2 days | W3, W5, W6, #21, #30, #56 |
| W9 | Agent console — builder shell, template picker, page list | ~2 days | #48, W2, W4 |
| W10 | Agent console — block editor with live preview 🚫 | ~2 days | W9, W5, **W7** — **blocked** |
| W11 | Agent console — theme editor, publish and version history | ~2 days | #48, W6, W8 |

All eleven carry `module:storefront` and the `M2` milestone. `W7` and `W10` additionally carry
`blocked`; `W8` carries `needs-decision` for open question 11.

**Order:** `W1` first. Then `W2` and `W3` in parallel — they touch different tables. `W4` → `W5`
is the editing spine; `W6` runs alongside it and is the most self-contained work in the epic, so
it is the best entry point for someone new. `W8` once `W5` and `W6` exist, because publishing
something that cannot yet be edited proves nothing. `W7` waits on #60. Screens last — `W9`, then
`W11`, then `W10` when `W7` unblocks it. A screen built against an endpoint that is still moving
gets built twice.

---

## The issues in full

Each block below is the issue body, ready to create as-is.

---

### W1 · `Storefront: Site builder schema`

#### What
The six tables from [the plan §2.4](../ARCHITECTURE_AND_DELIVERY_PLAN.md): `site_templates`,
`sites`, `site_versions`, `site_themes`, `site_pages`, `site_blocks` — with their EF Core
configuration, the tenant filter, and RLS policies.

`site_domains` and `site_domain_checks` are in the same section of the plan but belong to
[#59](https://github.com/innovateavitech/trips-agent/issues/59). This issue must not create them,
and #59 must not create these six. See [Gaps](#what-this-breakdown-found).

#### The tenancy decision this issue has to make
`site_pages` and `site_blocks` hang off a version and a page respectively — their natural keys do
not include `agency_id` at all. But
[hard rule #3](../../CLAUDE.md#3-never-bypass-the-tenant-filter) and the RLS backstop in
[#12](https://github.com/innovateavitech/trips-agent/issues/12) both work per-table, and an RLS
policy that has to join two levels up to find the tenant is both slow and easy to get wrong.

**Denormalise `agency_id` onto every tenant-scoped table in this set**, and enforce it with
foreign keys that include it, so a page cannot be attached to another agency's version. It is a
redundant column; it is also the difference between a policy that is obviously correct and one
nobody can review.

`site_templates` is the exception: templates are **platform-owned**, shared by every agency, and
carry no `agency_id`. That exclusion has to be deliberate and commented, because a table without
`agency_id` is otherwise indistinguishable from a table where somebody forgot.

#### Acceptance criteria
- [ ] All six tables created in a `storefront` migration, matching the column list in the plan
- [ ] `agency_id` on `sites`, `site_versions`, `site_themes`, `site_pages` and `site_blocks`, each
      with an EF Core global query filter and an RLS policy
- [ ] `site_templates` has no `agency_id`, is excluded from the filter explicitly, and the code
      says why in a comment
- [ ] `sites.agency_id` is **UNIQUE** — one site per agency, per the plan. See open question 6
- [ ] `site_domains` and `site_domain_checks` are **not** created here
- [ ] Composite foreign keys carry `agency_id`, so a child row cannot point at another agency's
      parent
- [ ] `UNIQUE (version_id, slug)` on `site_pages`; `(page_id, position)` ordering on `site_blocks`
- [ ] `sites.published_version_id` and `sites.draft_version_id` are nullable FKs to
      `site_versions`
- [ ] Seed data for `site_templates` ships as a migration, not as runtime code
- [ ] Integration test: an agency cannot read or write another agency's site, pages or blocks —
      including with `.IgnoreQueryFilters()`

**Depends on:** #8, #11, #12

---

### W2 · `Storefront: Template library and site provisioning`

#### What
The starter templates, and the operation that turns "this agency has no website" into a complete
draft site: a template chosen, a theme seeded from the agency's branding, and the system pages
already there.

#### Why provisioning is one operation and not a wizard
An agent who lands on a half-created site — a theme but no pages, or pages with no home block —
has no way to tell whether the product is broken or they are. Provisioning either produces a site
that would render, or it produces nothing and rolls back.

#### Acceptance criteria
- [ ] At least two templates seeded, differing in layout rather than only in colour
- [ ] `block_schema jsonb` per template declares which block types it supports and the shape of
      each block's `config` — this is what `W5` validates against
- [ ] Templates are versioned (`site_templates.version`); a template is never edited in place once
      a site references it
- [ ] Provisioning happens in one transaction: `sites` row, `site_themes` row seeded **from
      `agency_branding`** so the agent's logo and colours are already right on first load, a draft
      `site_versions` row, and Home / About / Contact / Terms pages
- [ ] Provisioning is idempotent — a second call returns the existing site rather than failing on
      the unique constraint with a 500
- [ ] Template preview images are served from our own assets and are never referenced by a
      published storefront page
- [ ] **No template ships Trips copy, Trips colours or Trips imagery.** Placeholder text is
      generic ("Your agency name"), and the seeded palette comes from the agent's branding record
- [ ] Changing template after provisioning is out of scope, and the issue says so — see
      [Gaps](#what-this-breakdown-found)
- [ ] Test: provisioning twice yields one site; a provisioned site has exactly the system pages

**Depends on:** W1, #18

---

### W3 · `Storefront: Draft versions, snapshots and copy-on-write`

#### What
The versioning model everything else in this epic sits on: one editable draft per site, an
immutable snapshot per published version, and the `content_snapshot` format that
[#60](https://github.com/innovateavitech/trips-agent/issues/60) reads.

#### Why this is its own issue
Rollback, staging preview and publish are the same mechanism seen from three angles. If versioning
is invented inside the publish endpoint, it will be invented again — differently — inside preview.
This issue builds it once, before either of them exists.

#### The snapshot is a cross-epic contract
`content_snapshot` and `theme_snapshot` are written here and read by #60, possibly months later
and certainly by a different person. Treat them as a public interface:

- A `schema_version` integer inside the JSON, from the very first row. Adding it later means
  guessing what old rows meant
- The snapshot is **self-contained**: everything needed to render the page without reading
  `site_pages`, `site_blocks` or `site_themes` again. A published version must still render
  identically after the draft has been edited fifty times
- The format is documented in the issue and in `docs/`, not only in a C# record

#### Acceptance criteria
- [ ] At most one `draft` version per site, enforced by a partial unique index rather than by
      application code
- [ ] Editing a published site copies the published version into a new draft (copy-on-write); the
      published version is never mutated
- [ ] A version in `published` or `archived` status is **immutable** — enforced by a database
      trigger, in the same spirit as the frozen price snapshot in
      [hard rule #5](../../CLAUDE.md#5-prices-are-frozen-at-purchase-never-recalculated)
- [ ] `version_no` increments per site and is gapless
- [ ] Optimistic concurrency on the draft, so two people editing the same site in two tabs produce
      a conflict rather than a silent overwrite
- [ ] `content_snapshot` carries `schema_version` and is self-contained
- [ ] The snapshot format is written down in `docs/` and linked from the issue
- [ ] Old `archived` versions are retained, not deleted — rollback is the entire point
- [ ] Test: publish, then edit the draft heavily, and assert the published snapshot is unchanged

**Depends on:** W1

---

### W4 · `Storefront: Site pages — system pages and custom pages`

#### What
Page CRUD inside a draft version: the four system pages that always exist, plus custom pages, with
slug rules and per-page SEO metadata.

#### Acceptance criteria
- [ ] Home, About, Contact and Terms are created by provisioning, marked `is_system`, and
      **cannot be deleted** — an agent who deletes their own Terms page has created a legal
      problem and a 404 at the same time
- [ ] A system page's `page_type` and slug are fixed; its title, blocks and meta are editable
- [ ] Custom pages: create, rename, reorder in navigation, delete
- [ ] Slugs are lowercase, hyphenated, unique per version, and validated against a **reserved
      list** — `api`, `cart`, `checkout`, `search`, `_next`, and anything else the renderer or the
      cart owns. A site whose custom page shadows `/checkout` is broken in a way the agent cannot
      diagnose
- [ ] `meta jsonb` holds title, description and social image, with lengths validated at the limits
      search engines actually truncate at
- [ ] Deleting a page cascades to its blocks within the draft only
- [ ] Every mutation targets the **draft** version; a request naming a published version is
      rejected, not silently redirected
- [ ] Test: system pages survive a delete attempt; a reserved slug is rejected; a duplicate slug
      inside one version is rejected

**Depends on:** W3

---

### W5 · `Storefront: Block editing — add, configure, reorder, remove`

#### What
The block API: place a block on a page, set its `config`, move it, remove it — validated against
the template's `block_schema`.

#### Why a registry, not a switch statement
The block types in the plan are `hero`, `featured_tours`, `rich_text`, `gallery`, `contact_form`,
`trip_request_widget` and `faq`. Two of those depend on epics that are not built:
`featured_tours` needs the catalog
([#56](https://github.com/innovateavitech/trips-agent/issues/56)) and `trip_request_widget` needs
CRM lead capture ([#62](https://github.com/innovateavitech/trips-agent/issues/62)).

So the block types have to be able to **arrive one at a time**. A registry — block type, config
schema, validator, default config — lets `hero`, `rich_text`, `gallery` and `faq` ship in this
issue and the other three be added by their own epics without reopening it.

#### Acceptance criteria
- [ ] A server-side block registry keyed by `block_type`, each entry declaring its config schema
      and defaults
- [ ] `config jsonb` is validated on write against that schema — an invalid config is rejected at
      the API, never stored. The renderer must be able to trust what it reads
- [ ] `hero`, `rich_text`, `gallery` and `faq` implemented here
- [ ] `featured_tours`, `contact_form` and `trip_request_widget` are **registered as unavailable**
      with a stated owning issue, so the gap is visible in the product rather than mysterious
- [ ] Reordering is a single atomic operation over a page's blocks — not N update calls that can
      half-fail and leave two blocks at position 3
- [ ] Rich text is stored as sanitised, structured content, not raw HTML. **Whatever an agent
      types here renders on a public page for their customers** — an unsanitised `<script>` is
      stored XSS against travellers
- [ ] Image blocks reference `asset_id` values from
      [#18](https://github.com/innovateavitech/trips-agent/issues/18); no external image URLs
      pasted straight into config
- [ ] A block cannot be added to a page whose template does not declare that block type
- [ ] Test: invalid config rejected; reorder is atomic; a script tag in rich text does not survive
      the round trip

**Depends on:** W4

---

### W6 · `Storefront: Theme editor — logo, colours and typography`

#### What
The `site_themes` write model: logo upload, palette, typography, and the validation that stops an
agent shipping an unreadable site.

#### The design-system tension, stated plainly
[Hard rule #7](../../CLAUDE.md#7-never-hard-code-a-colour-font-or-spacing-value) says colour lives
only in `packages/ui/src/styles/tokens.css`. A storefront's colours are **the agent's**, chosen at
runtime, and cannot live in a build-time token file.

These do not actually conflict, but only if the resolution is written down: the theme is applied
as **CSS custom properties injected per request**, overriding the same token names the design
system already defines. No arbitrary Tailwind values, no inline hex, no per-agency stylesheet
build. `pnpm check:design` keeps passing unchanged, and it must not be weakened to make this work.

This issue owns producing and validating the values; #60 owns injecting them. That the pattern is
undocumented today is recorded in [Gaps](#what-this-breakdown-found).

#### Acceptance criteria
- [ ] Logo upload through [#18](https://github.com/innovateavitech/trips-agent/issues/18) —
      `logo_asset_id`, never a pasted URL. Dimension and file-size limits enforced server-side
- [ ] Palette stored in `colors jsonb` under the **same names as the design tokens**
      (`primary`, `primary-foreground`, `background`, `foreground`, …), so injection is a
      substitution and not a translation layer
- [ ] Colours are validated for **WCAG AA contrast** on the pairs that matter, and a failing pair
      is rejected with a message naming both colours. An agent picking white text on a yellow
      button is not a taste question; it is a site their customers cannot read
- [ ] Typography restricted to a curated font list — an arbitrary font URL is a third-party
      request from the agent's domain, and a performance and privacy problem
- [ ] The theme defaults from `agency_branding`, and changing it here does **not** rewrite that
      record — invoices and emails keep their own branding source
- [ ] `custom_css` is **out of scope for v1** and the issue says so. See
      [Gaps](#what-this-breakdown-found) — it is stored CSS on a public page and needs a decision
      before it is built, not after
- [ ] Test: a failing contrast pair is rejected; the theme snapshot round-trips through publish

**Depends on:** W3, #18

---

### W7 · `Storefront: Staging preview` 🚫

#### What
A way for an agent to see their **draft** exactly as a customer would, before publishing it.

#### ⚠️ Blocked — do not start
Preview renders a site, and the renderer is
[#60](https://github.com/innovateavitech/trips-agent/issues/60), an epic that has not been broken
down. Building a second, preview-only renderer here would guarantee that preview and production
drift — which makes preview worse than useless, because it becomes a check people trust and
shouldn't.

#### Acceptance criteria (once #60 is broken down)
- [ ] Preview renders a specific `site_versions` row through the **same** renderer as production —
      one code path, one set of components
- [ ] Access is authenticated as the owning agency, or via a **signed, expiring token** for
      sharing with a colleague. A guessable `/preview/{site_id}` URL exposes every unpublished
      site in the platform
- [ ] `X-Robots-Tag: noindex` and a `robots.txt` deny on every preview response — a staged draft
      indexed by Google and outranking the live site is a genuine and long-lived problem
- [ ] Preview is visibly marked as a draft, in a way that cannot be mistaken for the live site
- [ ] Preview is never cached at the CDN and never shares a cache key with the published site
- [ ] **The preview host must not leak the Trips brand** if an agent forwards the link to their
      customer — which they will. Decide the host with #60 and #59 rather than defaulting to ours

**Depends on:** W3, **#60** (rewrite with real numbers when that epic is broken down)

---

### W8 · `Storefront: Publish, the publish gate and rollback`

#### What
Publish: freeze the draft into an immutable snapshot and point the site at it. Rollback: point it
back at an earlier one. Plus the pre-publish validation gate.

#### ⚠️ This issue touches open question 11
The FRD (§2.11 RS-6) forbids publishing until a tour, visa or group tour is published — which
means **an agent whose entire business is flight ticketing can never go live**. The plan's
recommendation is to relax it to *"at least one published product **or** flight search enabled on
the site"*, and that is a product decision, not an engineering one.

So the gate is **a set of configurable rules**, not an `if`. Whichever way question 11 is
answered, the answer is a configuration change and an added rule — not a migration and a redeploy.
Labelled `needs-decision` for that reason.

#### Acceptance criteria
- [ ] Publish is atomic: snapshot written, `sites.published_version_id` moved, previous published
      version marked `archived` — one transaction, or none of it
- [ ] Publish validation runs first and returns **all** failures at once, each naming the page and
      what to fix. Discovering four problems one publish attempt at a time is how an agent gives
      up on the product
- [ ] Rules enforced: system pages exist and are non-empty; required blocks have valid config; a
      logo is set; the theme passes contrast; the product gate above
- [ ] The product gate is a rule object behind a configuration switch, defaulting to the FRD's
      behaviour, with the relaxed variant already implemented behind it
- [ ] Rollback creates a **new version from an old snapshot** rather than deleting history — the
      record of what was live and when survives
- [ ] Both actions write to the audit log
      ([#21](https://github.com/innovateavitech/trips-agent/issues/21)): who, when, from which
      version to which
- [ ] Publishing emits a `SitePublished` domain event through the outbox
      ([#30](https://github.com/innovateavitech/trips-agent/issues/30)) — this is what job 15,
      `StorefrontCacheInvalidator`, consumes. Emit it here; the consumer belongs with rendering
- [ ] A suspended agency cannot publish (FRD §2.15 RS-3)
- [ ] Publish and rollback are idempotent under a double-click
- [ ] Test: publish → edit draft → rollback → the earlier content is live again and the newer
      version still exists

**Depends on:** W3, W5, W6, #21, #30, #56 (for the product gate)

---

### W9 · `Storefront: Agent console — builder shell, template picker and page list`

#### What
The first screens: choose a template, see your pages, get to the editor.

#### Acceptance criteria
- [ ] Template gallery with previews, and a first-run state for an agency with no site yet
- [ ] Page list showing system and custom pages, with add / rename / reorder / delete
- [ ] A persistent status indicator: draft with unpublished changes, published, or never published
      — an agent who cannot tell whether their edits are live will publish repeatedly to be sure
- [ ] Built from `packages/ui` components and design tokens only —
      [hard rule #7](../../CLAUDE.md#7-never-hard-code-a-colour-font-or-spacing-value). The
      console is **our** product and wears **our** brand; only the storefront wears the agent's
- [ ] `pnpm check:design` passes
- [ ] Loading, empty, error and permission-denied states all designed, not left to chance

**Depends on:** #48, W2, W4

---

### W10 · `Storefront: Agent console — block editor with live preview` 🚫

#### What
The editing surface: add blocks, fill them in, reorder them, watch the page change.

#### ⚠️ Blocked on W7
Live preview is `W7` embedded in the editor. Until preview exists there is nothing to embed, and
building a stand-in means building the renderer twice.

#### Acceptance criteria
- [ ] Add, configure, reorder (drag and drop, with a keyboard-accessible alternative) and remove
      blocks
- [ ] Per-block-type config forms generated from the same schema `W5` validates against, so the
      form and the API cannot disagree
- [ ] Live preview pane rendering the draft through `W7`
- [ ] Autosave to the draft with a visible saved/saving state and a recoverable failure — an agent
      who loses twenty minutes of copy does not come back to the feature
- [ ] The optimistic-concurrency conflict from `W3` surfaces as an explainable message, not a 409
- [ ] Unavailable block types are shown as coming soon, with no way to add them
- [ ] Accessible: keyboard reorder, focus management, labelled controls
- [ ] `pnpm check:design` passes

**Depends on:** W9, W5, **W7**

---

### W11 · `Storefront: Agent console — theme editor, publish and version history`

#### What
Branding, the publish button, and the list of what has been live.

#### Acceptance criteria
- [ ] Logo upload with a crop/preview step, and colour pickers bound to the `W6` palette names
- [ ] Contrast failures shown **inline as the agent picks**, not on submit — a rejected publish
      three screens later does not teach anyone anything
- [ ] Publish shows the full validation result: every failure, each linking to the page that
      causes it
- [ ] Version history lists published versions with who published them and when, and offers
      rollback with a confirmation naming the version being restored
- [ ] Rollback is described accurately in the UI — it creates a new version from an old one, and
      the wording should not imply anything is being deleted
- [ ] `pnpm check:design` passes

**Depends on:** #48, W6, W8

---

## What this breakdown found

Six things with no owner, and three open questions this epic touches.

### 1. The snapshot format is a cross-epic contract nobody owns

`site_versions.content_snapshot` is written by this epic and read by
[#60](https://github.com/innovateavitech/trips-agent/issues/60). It is stored in the database, so
unlike a C# interface it cannot be changed by changing both sides — every historical row keeps
whatever shape it was written with, and those rows are what agents' live sites render from.

`W3` proposes owning it, with a `schema_version` from the first row and the format documented in
`docs/`. **That needs agreeing before `W3` is picked up**, and ideally with whoever breaks down
#60 — this is the seam where the two storefront epics meet, and it is much cheaper to agree on now
than to reconcile in M3.

### 2. Nothing documents how per-tenant theming coexists with the design system

[`docs/DESIGN_SYSTEM.md`](../DESIGN_SYSTEM.md) has no per-tenant story, and does not need one for
the consoles — but the storefront's colours are runtime data belonging to the agent. `W6` proposes
runtime CSS custom properties overriding the existing token names, so `scripts/check-design.sh`
never has to be relaxed.

**Suggested:** an ADR recording that decision, since the alternative — a build-time stylesheet per
agency, or arbitrary Tailwind values on the storefront — is a plausible-looking wrong turn that
someone will otherwise take. Shared with #60; raised here because `W6` is where the values are
first produced.

### 3. `custom_css` is in the schema and nobody has decided whether it ships

The plan lists `site_themes.custom_css`. As written it is arbitrary CSS, authored by an agent,
served on a page their customers load. That is a defacement and data-exfiltration vector
(`background: url(...)` beacons, overlaid fake forms), and it makes every future template change a
potential visual break we cannot test for.

**Suggested:** leave the column, ship nothing that writes to it in v1, and raise the decision
separately — sanitised subset, tier-gated with a support disclaimer, or dropped. `W6` assumes it
is out of scope.

### 4. Changing template after publish has no defined behaviour

`site_templates.version` exists, and templates declare which blocks they support. Neither the plan
nor the epic says what happens when an agent switches template, or when a template's
`block_schema` changes under a site already using it — blocks that no longer exist in the new
template have to go somewhere, and "somewhere" is currently undefined.

**Suggested:** `W2` explicitly excludes template switching, and a separate issue owns it — with
the sites already published being the reason it needs designing rather than improvising.

### 5. `site_domains` sits in the same plan section as this epic's tables but belongs to #59

[The plan §2.4](../ARCHITECTURE_AND_DELIVERY_PLAN.md) covers "site builder **& domains**" in one
table, while the epic body for #58 lists only the six builder tables. Read quickly, `site_domains`
looks like it belongs to whoever gets there first — and it is
[#59](https://github.com/innovateavitech/trips-agent/issues/59)'s core subject.

`W1` states the boundary. Worth a comment on #59 so both sides agree before either migration is
written; two migrations creating the same table is a merge conflict in the one place that is
painful to resolve.

### 6. Whether publishing a site is itself entitlement-gated is undecided

[#64](https://github.com/innovateavitech/trips-agent/issues/64) lists `custom_domain` as an
entitlement, so a tier can withhold a custom domain. Nothing says whether a tier can withhold
*publishing at all*, or cap pages or blocks. `W8` implements the product gate from question 11 and
no tier gate; if a commercial answer arrives later it slots into the same rule set — which is the
main reason the gate is a rule set.

### 7. Open questions this epic touches

Per [CLAUDE.md](../../CLAUDE.md#things-that-will-surprise-you), these are flagged rather than
guessed at:

| # | Question | Effect here |
|---|---|---|
| 11 | The publish gate blocks flight-only agents | **`W8` directly.** Ships the gate as configurable rules, defaulting to the FRD's behaviour, with the relaxed variant implemented behind the switch |
| 6 | Sub-agent branding — one site per tenant, or one per sub-agent? | `sites.agency_id` is UNIQUE, matching the plan's recommendation. But §1 of the plan also says a sub-agent "eventually needs its own storefront", and if that wins, the unique constraint and the host→site resolution both change |
| 15 | Subscription downgrade with entitlements in use | Mostly #59's problem (a live custom domain), but if publishing ever becomes tier-gated, gap 6 above becomes the same question for sites |

---

## Next steps

1. Review and merge this breakdown
2. Post it as a comment on [#58](https://github.com/innovateavitech/trips-agent/issues/58)
3. Agree the `content_snapshot` contract with whoever breaks down
   [#60](https://github.com/innovateavitech/trips-agent/issues/60) — before `W3` starts
4. Create `W1`–`W11` with `module:storefront` + `M2` (`W7` and `W10` also `blocked`; `W8` also
   `needs-decision`), rewriting the `Depends on:` lines with real numbers
5. Comment on [#59](https://github.com/innovateavitech/trips-agent/issues/59) confirming it owns
   `site_domains` and `site_domain_checks`
6. Raise the `custom_css` decision and the template-switching issue separately
7. Raise the per-tenant theming ADR, jointly with #60
8. Close #58 as broken down, or keep it open as the tracking epic — the same repo convention
   question [#71](https://github.com/innovateavitech/trips-agent/issues/71) raised
9. `./scripts/generate-backlog.sh`
