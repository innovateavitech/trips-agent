import { useState } from 'react';
import { Alert, Badge, Button, Card } from '@trips/ui';
import { describeError } from '../../../api/errors';
import {
  useCreatePreviewLink,
  usePublishSiteVersion,
  useRollBackSiteVersion,
  useStageSiteVersion,
  type Site,
  type SiteVersion,
} from '../storefront-queries';

/**
 * Putting the site in front of travellers, and taking it back.
 *
 * Publishing is two steps on purpose. Staging freezes the draft as a numbered version; publishing
 * puts a version live. That is what makes rolling back possible at all — a previous version is a
 * snapshot that still exists, not a diff to be undone — and it means the agent can look at exactly
 * what they are about to publish before anyone else does.
 */
export function PublishPanel({ site, versions }: { site: Site; versions: SiteVersion[] }) {
  const stage = useStageSiteVersion();
  const publish = usePublishSiteVersion();
  const rollBack = useRollBackSiteVersion();
  const preview = useCreatePreviewLink();

  const [previewUrl, setPreviewUrl] = useState<string | null>(null);

  const staged = site.staged;
  const live = site.published;
  const problems = site.publishCheck.problems;

  const busy = stage.isPending || publish.isPending || rollBack.isPending;
  const error = stage.error ?? publish.error ?? rollBack.error ?? preview.error;

  async function showPreview(versionId: string | null) {
    const link = await preview.mutateAsync(versionId);
    setPreviewUrl(link.url);
  }

  return (
    <Card className="p-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-foreground">Publishing</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {live
              ? `Travellers are seeing version ${live.versionNumber}.`
              : 'Your website is not live yet.'}
          </p>
        </div>

        {site.siteUrl && live ? (
          <a
            href={site.siteUrl}
            target="_blank"
            rel="noreferrer noopener"
            className="text-sm text-primary underline-offset-4 hover:underline"
          >
            View your live site
          </a>
        ) : null}
      </div>

      {error ? (
        <Alert tone="destructive" className="mt-4">
          {describeError(error).title}
        </Alert>
      ) : null}

      {problems.length > 0 ? (
        <Alert tone="warning" className="mt-4">
          <p className="font-medium">Before you can publish:</p>
          <ul className="mt-1 list-disc space-y-1 pl-5">
            {problems.map((problem) => (
              <li key={problem.code}>{problem.message}</li>
            ))}
          </ul>
        </Alert>
      ) : null}

      <div className="mt-5 flex flex-wrap items-center gap-3">
        <Button
          onClick={() => stage.mutate()}
          disabled={busy || !site.hasUnstagedChanges}
          variant={site.hasUnstagedChanges ? 'primary' : 'outline'}
        >
          {site.hasUnstagedChanges ? 'Save a version of these changes' : 'No changes to save'}
        </Button>

        {staged ? (
          <>
            <Button
              variant="outline"
              onClick={() => void showPreview(staged.id)}
              disabled={preview.isPending}
            >
              Preview version {staged.versionNumber}
            </Button>

            <Button
              onClick={() => publish.mutate(staged.id)}
              disabled={busy || !site.publishCheck.canPublish || staged.isLive}
            >
              {staged.isLive ? 'This version is live' : `Publish version ${staged.versionNumber}`}
            </Button>
          </>
        ) : (
          <Button
            variant="outline"
            onClick={() => void showPreview(null)}
            disabled={preview.isPending}
          >
            Preview the draft
          </Button>
        )}
      </div>

      {previewUrl ? (
        <Alert className="mt-4">
          <p>
            A private link to this version, good for a few hours. Anyone with it can see the page,
            so share it only with people you mean to.
          </p>
          <a
            href={previewUrl}
            target="_blank"
            rel="noreferrer noopener"
            className="mt-1 block break-all text-primary underline-offset-4 hover:underline"
          >
            {previewUrl}
          </a>
        </Alert>
      ) : null}

      {versions.length > 0 ? (
        <div className="mt-6">
          <h3 className="mb-3 text-sm font-semibold text-foreground">History</h3>
          <ul className="divide-y divide-border">
            {versions.map((version) => (
              <li key={version.id} className="flex flex-wrap items-center gap-3 py-3 text-sm">
                <span className="font-medium text-foreground">Version {version.versionNumber}</span>

                {version.isLive ? <Badge tone="success">Live</Badge> : null}
                {!version.isLive && version.publishedAt ? (
                  <Badge tone="neutral">Was live</Badge>
                ) : null}

                <span className="text-muted-foreground">
                  {version.stagedAt ? new Date(version.stagedAt).toLocaleString() : null}
                  {version.stagedBy ? ` · saved by ${version.stagedBy}` : null}
                </span>

                {version.canRollBackTo && !version.isLive ? (
                  <Button
                    size="sm"
                    variant="outline"
                    className="ml-auto"
                    onClick={() => rollBack.mutate(version.id)}
                    disabled={busy}
                  >
                    Put this back live
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </Card>
  );
}
