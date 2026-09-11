import { ImageIcon, ImagePlus } from 'lucide-react';
import { useState } from 'react';
import { Alert, Badge, Button, Input, buttonVariants } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { cx } from '../../search/class-names';
import { useUploadImage } from '../catalog-api';
import type { ProductMedia } from '../types';

/**
 * #162 — the product's images. The cover is the one the storefront leads with:
 * the chosen one, or the first. Uploads go through the asset pipeline, which
 * scans every file before it can be shown.
 */
export function ImagePicker({
  media,
  heroAssetId,
  onAdd,
  onRemove,
  onCaption,
  onCover,
}: {
  media: ProductMedia[];
  heroAssetId: string | null;
  onAdd: (image: ProductMedia) => void;
  onRemove: (assetId: string) => void;
  onCaption: (assetId: string, caption: string) => void;
  onCover: (assetId: string) => void;
}) {
  const upload = useUploadImage();
  const [failure, setFailure] = useState<{ title: string; detail: string } | null>(null);
  const cover = media.some((item) => item.assetId === heroAssetId) ? heroAssetId : media[0]?.assetId;

  async function addFiles(files: File[]) {
    setFailure(null);
    for (const file of files) {
      try {
        const image = await upload.mutateAsync(file);
        onAdd({ assetId: image.assetId, previewUrl: image.previewUrl, caption: '' });
      } catch (error) {
        const { title, detail } = describeError(error);
        setFailure({ title: `${file.name}: ${title}`, detail: detail ?? '' });
      }
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {media.length > 0 ? (
        <ul aria-label="Images" className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {media.map((item, index) => {
            const isCover = item.assetId === cover;

            return (
              <li key={item.assetId} className="flex flex-col gap-2">
                <div className="relative aspect-video overflow-hidden rounded-md border border-border bg-muted">
                  {item.previewUrl ? (
                    <img
                      src={item.previewUrl}
                      alt={item.caption || `Image ${index + 1}`}
                      className="h-full w-full object-cover"
                    />
                  ) : (
                    <div className="flex h-full w-full flex-col items-center justify-center gap-1 text-muted-foreground">
                      <ImageIcon className="h-6 w-6" aria-hidden="true" />
                      <span className="text-xs">Stored image</span>
                    </div>
                  )}
                  {isCover ? (
                    <span className="absolute left-2 top-2">
                      <Badge tone="primary">Cover</Badge>
                    </span>
                  ) : null}
                </div>
                <Input
                  label={`Caption for image ${index + 1}`}
                  value={item.caption}
                  onChange={(event) => onCaption(item.assetId, event.target.value)}
                />
                <div className="flex flex-wrap gap-1">
                  {isCover ? null : (
                    <Button type="button" variant="ghost" size="sm" onClick={() => onCover(item.assetId)}>
                      Make cover
                    </Button>
                  )}
                  <Button type="button" variant="ghost" size="sm" onClick={() => onRemove(item.assetId)}>
                    Remove
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-sm text-muted-foreground">
          No images yet. A product needs at least one before it can be published.
        </p>
      )}

      {failure ? (
        <Alert tone="warning" title={failure.title}>
          {failure.detail}
        </Alert>
      ) : null}

      <div>
        {/* A real file input under a label: the keyboard and screen readers get the browser's own control. */}
        <label
          className={cx(
            buttonVariants({ variant: 'outline', size: 'sm' }),
            'cursor-pointer focus-within:ring-2 focus-within:ring-ring',
            upload.isPending && 'pointer-events-none opacity-60',
          )}
        >
          <ImagePlus className="h-4 w-4" aria-hidden="true" />
          {upload.isPending ? 'Uploading…' : 'Add images'}
          <input
            type="file"
            accept="image/jpeg,image/png,image/webp"
            multiple
            className="sr-only"
            disabled={upload.isPending}
            onChange={(event) => {
              const files = Array.from(event.target.files ?? []);
              event.target.value = '';
              void addFiles(files);
            }}
          />
        </label>
        <p className="mt-2 text-xs text-muted-foreground">JPEG, PNG or WebP, up to 10 MB each.</p>
      </div>
    </div>
  );
}
