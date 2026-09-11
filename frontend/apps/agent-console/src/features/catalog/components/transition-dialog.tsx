import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
} from '@trips/ui';
import type { Product, ProductTransition } from '../types';

interface Copy {
  title: (product: Product) => string;
  body: string;
  confirm: string;
  destructive?: boolean;
}

const COPY: Record<ProductTransition | 'restore', Copy> = {
  publish: {
    title: (product) => `Publish ${product.title || 'this product'}?`,
    body: 'It goes on your storefront straight away, where customers can find and buy it.',
    confirm: 'Publish',
  },
  unpublish: {
    title: () => 'Take it off your storefront?',
    body: 'Customers can no longer find or buy it. It becomes a draft you can change and publish again.',
    confirm: 'Unpublish',
  },
  archive: {
    title: () => 'Archive this product?',
    body: 'It comes off your storefront and moves to Archived. You can restore it as a draft whenever you need it again.',
    confirm: 'Archive',
    destructive: true,
  },
  restore: {
    title: () => 'Restore it as a draft?',
    body: 'It stays off your storefront until you publish it again.',
    confirm: 'Restore',
  },
};

/**
 * Every change to what customers can see goes through one confirmation, saying
 * what will happen in plain words. Closing it any way cancels.
 */
export function TransitionDialog({
  action,
  product,
  pending,
  onConfirm,
  onClose,
}: {
  action: ProductTransition | null;
  product: Product | null;
  pending: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const key = action === 'unpublish' && product?.status === 'Archived' ? 'restore' : action;
  const copy = key ? COPY[key] : null;

  return (
    <Dialog
      open={action !== null && product !== null}
      onOpenChange={(open) => (open ? undefined : onClose())}
    >
      {copy && product ? (
        <DialogContent>
          <DialogTitle>{copy.title(product)}</DialogTitle>
          <DialogDescription>{copy.body}</DialogDescription>
          <DialogFooter>
            <Button variant="outline" onClick={onClose} disabled={pending}>
              Not now
            </Button>
            <Button
              variant={copy.destructive ? 'destructive' : undefined}
              onClick={onConfirm}
              loading={pending}
            >
              {copy.confirm}
            </Button>
          </DialogFooter>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}
