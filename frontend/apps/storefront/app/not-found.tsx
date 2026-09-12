import Link from 'next/link';

/**
 * A page that does not exist on a site that does.
 *
 * Deliberately plain and unbranded in its wording: it is rendered inside the agency's own header and
 * footer, so the surrounding page already carries their name. It says nothing about us.
 */
export default function NotFound() {
  return (
    <section className="mx-auto flex max-w-2xl flex-col items-center gap-4 px-4 py-24 text-center sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">Page not found</h1>
      <p className="text-base text-muted-foreground">
        The page you were looking for is not here. It may have moved, or the link may be out of
        date.
      </p>
      <Link
        href="/"
        className="mt-2 inline-flex items-center justify-center rounded-md bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover"
      >
        Back to the home page
      </Link>
    </section>
  );
}
