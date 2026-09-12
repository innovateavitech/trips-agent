import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Badge, Button, buttonVariants, Card, ErrorState, LoadingState } from '@trips/ui';
import { int64 } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { PublishPanel } from '../components/publish-panel';
import { SiteSettingsForm } from '../components/site-settings-form';
import {
  useCreateSite,
  useCreateSitePage,
  useDeleteSitePage,
  useSite,
  useSiteTemplates,
  useSiteVersions,
  type SitePageSummary,
} from '../storefront-queries';

/**
 * ============================================================================
 *  Issue 58 — the agency's own website.
 * ============================================================================
 *
 * One screen with the three things an agent does here: change what the site says, edit its pages,
 * and put it live. Design and addresses get screens of their own because they are each a job with
 * its own shape, not a field on this form.
 *
 * Nothing on the website itself may mention us (CLAUDE.md rule 4) — this console is the only place
 * the agent sees our name at all.
 */
export function WebsitePage() {
  const site = useSite();
  const versions = useSiteVersions();

  if (site.isPending) {
    return <LoadingState size="page" label="Loading your website" />;
  }

  if (site.isError) {
    return (
      <ErrorState
        title={describeError(site.error).title}
        detail={describeError(site.error).detail}
        onRetry={() => void site.refetch()}
      />
    );
  }

  if (!site.data) {
    return <ChooseTemplate />;
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Your website"
        description="Everything travellers see when they visit your own address."
        actions={
          <>
            <Link to="/website/design" className={buttonVariants({ variant: 'outline' })}>
              Design
            </Link>
            <Link to="/website/addresses" className={buttonVariants({ variant: 'outline' })}>
              Web address
            </Link>
          </>
        }
      />

      <PublishPanel site={site.data} versions={versions.data ?? []} />
      <PageList pages={site.data.pages} />
      <SiteSettingsForm settings={site.data.settings} />
    </div>
  );
}

/**
 * The first screen an agency sees: pick a layout and we build the pages.
 *
 * Two templates in the MVP (docs/BUILD_PLAN.md). Both come with a home page, About, Contact and
 * Terms already written, because a blank site is a site nobody ever finishes.
 */
function ChooseTemplate() {
  const templates = useSiteTemplates();
  const create = useCreateSite();
  const [chosen, setChosen] = useState<string | null>(null);

  if (templates.isPending) {
    return <LoadingState size="page" label="Loading the layouts" />;
  }

  if (templates.isError) {
    return (
      <ErrorState
        title={describeError(templates.error).title}
        onRetry={() => void templates.refetch()}
      />
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Your website"
        description="Pick a layout to start with. You can change everything on it afterwards, and change your mind about the layout later too."
      />

      {create.isError ? (
        <Alert tone="destructive">{describeError(create.error).title}</Alert>
      ) : null}

      <div className="grid gap-4 md:grid-cols-2">
        {templates.data.map((template) => (
          <Card
            key={template.code}
            className={`flex flex-col p-6 ${chosen === template.code ? 'border-primary' : ''}`}
          >
            <h2 className="text-lg font-semibold text-foreground">{template.name}</h2>
            <p className="mt-1 text-sm text-muted-foreground">{template.description}</p>

            <p className="mt-4 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Pages you get
            </p>
            <ul className="mt-1.5 flex flex-wrap gap-2">
              {template.pageTitles.map((title) => (
                <li key={title}>
                  <Badge>{title}</Badge>
                </li>
              ))}
            </ul>

            <Button
              className="mt-6"
              onClick={() => {
                setChosen(template.code);
                create.mutate(template.code);
              }}
              disabled={create.isPending}
            >
              {create.isPending && chosen === template.code
                ? 'Building your website…'
                : 'Use this layout'}
            </Button>
          </Card>
        ))}
      </div>
    </div>
  );
}

/** The site's pages, in the order they appear in its navigation. */
function PageList({ pages }: { pages: SitePageSummary[] }) {
  const create = useCreateSitePage();
  const remove = useDeleteSitePage();
  const [title, setTitle] = useState('');

  const ordered = [...pages].sort(
    (first, second) => int64(first.position) - int64(second.position),
  );

  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold text-foreground">Pages</h2>
      <p className="mt-1 text-sm text-muted-foreground">
        About, Contact and Terms come with your layout and cannot be deleted — travellers expect to
        find them, and your terms are what they agree to.
      </p>

      <ul className="mt-5 divide-y divide-border">
        {ordered.map((page) => (
          <li key={page.id} className="flex flex-wrap items-center gap-3 py-3">
            <Link
              to={`/website/pages/${page.id}`}
              className="font-medium text-foreground underline-offset-4 hover:underline"
            >
              {page.title}
            </Link>

            <span className="text-sm text-muted-foreground">
              /{page.slug === 'home' ? '' : page.slug}
            </span>

            {page.isSystem ? <Badge tone="neutral">Standard page</Badge> : null}
            {!page.showInNav ? <Badge tone="neutral">Hidden from the menu</Badge> : null}

            <span className="text-sm text-muted-foreground">
              {int64(page.blockCount) === 1 ? '1 section' : `${page.blockCount} sections`}
            </span>

            {!page.isSystem ? (
              <Button
                size="sm"
                variant="ghost"
                className="ml-auto"
                onClick={() => remove.mutate(page.id)}
                disabled={remove.isPending}
              >
                Delete
              </Button>
            ) : null}
          </li>
        ))}
      </ul>

      {create.isError ? (
        <Alert tone="destructive" className="mt-4">
          {describeError(create.error).title}
        </Alert>
      ) : null}

      <form
        className="mt-5 flex flex-wrap items-end gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          if (title.trim().length === 0) return;
          create.mutate({ title: title.trim(), slug: null });
          setTitle('');
        }}
      >
        <div className="min-w-56 flex-1">
          <label className="flex flex-col gap-1.5">
            <span className="text-sm font-medium text-foreground">Add a page</span>
            <input
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder="Group tours, Testimonials…"
              className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </label>
        </div>
        <Button type="submit" variant="outline" disabled={create.isPending}>
          Add page
        </Button>
      </form>
    </Card>
  );
}
