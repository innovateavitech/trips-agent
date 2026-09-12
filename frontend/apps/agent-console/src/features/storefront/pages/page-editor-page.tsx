import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Alert, Button, Card, ErrorState, Input, LoadingState, Select, Textarea } from '@trips/ui';
import { int64 } from '@trips/utils';
import { ApiError, describeError, fieldError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { BlockEditor } from '../components/block-editor';
import { BLOCK_LABELS, BLOCK_TYPES, emptyBlock, move, type BlockType } from '../site-blocks';
import { useSaveSitePage, useSitePage, type SiteBlock } from '../storefront-queries';

/**
 * ============================================================================
 *  Issue 58 — editing one page of the website.
 * ============================================================================
 *
 * A page is a title, a few details, and a list of sections in the order they appear. The whole lot
 * saves at once, so adding a section, rewording another and moving a third either all happen or
 * none do — there is no half-saved page for a traveller to land on.
 *
 * The revision the editor loaded goes back with the save. If somebody else at the agency saved
 * first, the API refuses rather than letting this save quietly wipe out theirs, and the screen says
 * so in those words.
 */
export function PageEditorPage() {
  const { pageId } = useParams<{ pageId: string }>();
  const page = useSitePage(pageId);
  const save = useSaveSitePage(pageId ?? '');

  // The editor's own copy of the page. Loaded once, then owned here until it is saved: a background
  // refetch must not throw away what the agent is halfway through typing.
  const [draft, setDraft] = useState<EditorDraft | null>(null);
  const [saved, setSaved] = useState(false);
  const [adding, setAdding] = useState<BlockType>('Text');

  useEffect(() => {
    if (page.data && !draft) {
      setDraft({
        title: page.data.title,
        slug: page.data.slug,
        showInNav: page.data.showInNav,
        metaTitle: page.data.metaTitle ?? '',
        metaDescription: page.data.metaDescription ?? '',
        revision: int64(page.data.revision),
        blocks: page.data.blocks.map((entry) => ({ id: entry.id, block: entry.block })),
      });
    }
  }, [page.data, draft]);

  if (page.isPending || !draft) {
    return <LoadingState size="page" label="Loading the page" />;
  }

  if (page.isError) {
    return (
      <ErrorState title={describeError(page.error).title} onRetry={() => void page.refetch()} />
    );
  }

  const isHome = page.data.slug === 'home';
  const conflict = save.error instanceof ApiError && save.error.status === 409;

  function set<K extends keyof EditorDraft>(key: K, value: EditorDraft[K]) {
    setSaved(false);
    setDraft((current) => (current ? { ...current, [key]: value } : current));
  }

  async function submit() {
    const result = await save.mutateAsync({
      title: draft!.title.trim(),
      slug: isHome ? null : draft!.slug.trim(),
      showInNav: draft!.showInNav,
      metaTitle: blankToNull(draft!.metaTitle),
      metaDescription: blankToNull(draft!.metaDescription),
      revision: draft!.revision,
      blocks: draft!.blocks.map((entry) => ({ id: entry.id, block: entry.block })),
    });

    // The save returns the page as it now stands, revision and all, so the next save is against the
    // right one without a round trip.
    setDraft({
      title: result.title,
      slug: result.slug,
      showInNav: result.showInNav,
      metaTitle: result.metaTitle ?? '',
      metaDescription: result.metaDescription ?? '',
      revision: int64(result.revision),
      blocks: result.blocks.map((entry) => ({ id: entry.id, block: entry.block })),
    });
    setSaved(true);
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={draft.title || 'Untitled page'}
        description={
          <>
            Sections appear on the page in this order. Nothing you change here is live until you
            publish —{' '}
            <Link to="/website" className="text-primary underline-offset-4 hover:underline">
              back to your website
            </Link>
            .
          </>
        }
        actions={
          <Button onClick={() => void submit()} disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save page'}
          </Button>
        }
      />

      {conflict ? (
        <Alert tone="warning">
          Somebody else saved this page while you were editing it. Reload to see their version —
          this save was not applied, so nothing of theirs was lost.
        </Alert>
      ) : save.isError ? (
        <Alert tone="destructive">{describeError(save.error).title}</Alert>
      ) : null}

      {saved && !save.isPending ? <Alert tone="success">Page saved.</Alert> : null}

      <Card className="grid gap-4 p-6 md:grid-cols-2">
        <Input
          label="Page title"
          value={draft.title}
          onChange={(event) => set('title', event.target.value)}
          error={fieldError(save.error, 'Title')}
        />

        <Input
          label="Web address"
          hint={
            isHome
              ? 'Your home page always sits at the top of your site.'
              : 'The part after your domain name.'
          }
          value={isHome ? '/' : `/${draft.slug}`}
          disabled={isHome}
          onChange={(event) => set('slug', event.target.value.replace(/^\//, ''))}
          error={fieldError(save.error, 'Slug')}
        />

        <Input
          label="Search engine title"
          hint="Leave empty to use the page title."
          value={draft.metaTitle}
          onChange={(event) => set('metaTitle', event.target.value)}
          error={fieldError(save.error, 'MetaTitle')}
        />

        <Textarea
          label="Search engine description"
          rows={2}
          value={draft.metaDescription}
          onChange={(event) => set('metaDescription', event.target.value)}
          error={fieldError(save.error, 'MetaDescription')}
        />

        <label className="flex items-center gap-2 text-sm text-foreground md:col-span-2">
          <input
            type="checkbox"
            checked={draft.showInNav}
            onChange={(event) => set('showInNav', event.target.checked)}
            className="h-4 w-4 rounded border-input text-primary focus-visible:ring-2 focus-visible:ring-ring"
          />
          Show this page in the menu at the top of my site
        </label>
      </Card>

      <div className="flex flex-col gap-4">
        {draft.blocks.map((entry, index) => (
          <BlockEditor
            key={entry.id ?? `new-${index}`}
            block={entry.block}
            index={index}
            count={draft.blocks.length}
            onChange={(block) =>
              set(
                'blocks',
                draft.blocks.map((candidate, position) =>
                  position === index ? { ...candidate, block } : candidate,
                ),
              )
            }
            onMove={(to) => set('blocks', move(draft.blocks, index, to))}
            onRemove={() =>
              set(
                'blocks',
                draft.blocks.filter((_, position) => position !== index),
              )
            }
          />
        ))}

        {draft.blocks.length === 0 ? (
          <Card className="p-8 text-center text-sm text-muted-foreground">
            This page has no sections yet. Add one below.
          </Card>
        ) : null}
      </div>

      <Card className="flex flex-wrap items-end gap-3 p-6">
        <div className="min-w-56 flex-1">
          <Select
            label="Add a section"
            value={adding}
            onChange={(event) => setAdding(event.target.value as BlockType)}
          >
            {BLOCK_TYPES.map((type) => (
              <option key={type} value={type}>
                {BLOCK_LABELS[type].name} — {BLOCK_LABELS[type].description}
              </option>
            ))}
          </Select>
        </div>

        <Button
          variant="outline"
          onClick={() => set('blocks', [...draft.blocks, { id: null, block: emptyBlock(adding) }])}
        >
          Add section
        </Button>
      </Card>
    </div>
  );
}

interface EditorDraft {
  title: string;
  slug: string;
  showInNav: boolean;
  metaTitle: string;
  metaDescription: string;
  revision: number;
  blocks: { id: string | null; block: SiteBlock }[];
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
