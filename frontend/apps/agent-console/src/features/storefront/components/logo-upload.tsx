import { useState, type ChangeEvent } from 'react';
import { Alert, Button } from '@trips/ui';
import { api } from '../../../api/client';
import { describeError, unwrap } from '../../../api/errors';

/**
 * Uploading the agency's logo.
 *
 * Three steps, and the middle one does not involve us: we ask the API where to put the file, the
 * browser sends the bytes straight there, and then we tell the API it has arrived. The file never
 * passes through the API, which is what keeps a large upload from tying up a request thread.
 *
 * A logo is not usable the moment it lands. It is scanned for malware and resized first, and only an
 * asset that comes back Ready may be attached to anything. So this polls for that rather than
 * pretending the upload is the end of it — an agent who saw "done" and then found no logo on their
 * site would have no idea why.
 */
export function LogoUpload({
  currentPreviewUrl,
  selectedAssetId,
  onSelected,
}: {
  currentPreviewUrl: string | null;
  selectedAssetId: string | null;
  onSelected: (assetId: string | null) => void;
}) {
  const [state, setState] = useState<State>({ kind: 'idle' });

  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = '';

    if (!file) {
      return;
    }

    setState({ kind: 'uploading' });

    try {
      const ticket = await unwrap(
        api.POST('/api/v1/assets/uploads', {
          body: {
            purpose: 'AgencyLogo',
            fileName: file.name,
            sizeBytes: file.size,
            contentType: file.type,
          },
        }),
      );

      const sent = await fetch(ticket.uploadUrl, {
        method: ticket.method,
        headers: ticket.headers,
        body: file,
      });

      if (!sent.ok) {
        throw new Error('The file could not be sent. Please try again.');
      }

      await unwrap(
        api.POST('/api/v1/assets/{assetId}/complete', {
          params: { path: { assetId: ticket.assetId } },
        }),
      );

      setState({ kind: 'processing' });

      const ready = await waitUntilReady(ticket.assetId);

      if (ready.status !== 'Ready') {
        setState({
          kind: 'failed',
          message:
            ready.failureReason ??
            'That file could not be used. Try a PNG or JPEG under a few megabytes.',
        });
        return;
      }

      onSelected(ready.id);
      setState({ kind: 'idle', previewUrl: ready.links[0]?.url });
    } catch (error) {
      setState({ kind: 'failed', message: describeError(error).title });
    }
  }

  const previewUrl =
    state.kind === 'idle' ? (state.previewUrl ?? currentPreviewUrl) : currentPreviewUrl;
  const busy = state.kind === 'uploading' || state.kind === 'processing';

  return (
    <div className="mt-4 flex flex-wrap items-center gap-5">
      <div className="flex h-20 w-40 items-center justify-center rounded-md border border-border bg-muted">
        {previewUrl && selectedAssetId ? (
          <img src={previewUrl} alt="Your logo" className="max-h-16 max-w-36 object-contain" />
        ) : (
          <span className="text-xs text-muted-foreground">No logo</span>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <label>
          <input
            type="file"
            accept="image/png,image/jpeg,image/webp"
            onChange={(event) => void upload(event)}
            disabled={busy}
            className="block w-full text-sm text-muted-foreground file:mr-3 file:cursor-pointer file:rounded-md file:border file:border-input file:bg-background file:px-4 file:py-2 file:text-sm file:font-medium file:text-foreground hover:file:bg-accent"
          />
        </label>

        {selectedAssetId ? (
          <Button variant="ghost" size="sm" onClick={() => onSelected(null)} disabled={busy}>
            Remove logo
          </Button>
        ) : null}
      </div>

      {state.kind === 'uploading' ? (
        <p className="text-sm text-muted-foreground">Sending your logo…</p>
      ) : null}

      {state.kind === 'processing' ? (
        <p className="text-sm text-muted-foreground">
          Checking and resizing it. This normally takes a few seconds.
        </p>
      ) : null}

      {state.kind === 'failed' ? (
        <Alert tone="destructive" className="w-full">
          {state.message}
        </Alert>
      ) : null}
    </div>
  );
}

type State =
  | { kind: 'idle'; previewUrl?: string }
  | { kind: 'uploading' }
  | { kind: 'processing' }
  | { kind: 'failed'; message: string };

/** How long to wait for the scanner and the resizer before giving up on them. */
const READY_TIMEOUT_MS = 60_000;
const POLL_INTERVAL_MS = 1_500;

async function waitUntilReady(assetId: string) {
  const deadline = Date.now() + READY_TIMEOUT_MS;

  for (;;) {
    const asset = await unwrap(
      api.GET('/api/v1/assets/{assetId}', { params: { path: { assetId } } }),
    );

    // Ready is the only usable end state; Quarantined and Failed are the other two, and both are
    // final. Anything else means it is still moving through the pipeline.
    if (asset.status !== 'Uploaded' && asset.status !== 'Processing') {
      return asset;
    }

    if (Date.now() > deadline) {
      return asset;
    }

    await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
  }
}
