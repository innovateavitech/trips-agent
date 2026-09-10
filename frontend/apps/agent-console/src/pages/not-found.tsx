import { Link } from 'react-router-dom';
import { buttonVariants, EmptyState } from '@trips/ui';

export function NotFoundPage() {
  return (
    <EmptyState
      title="Page not found"
      description="That link may be out of date, or the screen has not been built yet."
      action={
        /* `buttonVariants` on the Link, not a Button wrapping it — an <a> inside
           a <button> is invalid HTML and breaks keyboard navigation. */
        <Link to="/" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
          Back to dashboard
        </Link>
      }
    />
  );
}
