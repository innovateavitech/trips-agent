import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { ArrowLeftIcon } from './icons';

/** The content column every screen sits in, so page edges line up from one screen to the next. */
export function Page({ children, wide }: { children: ReactNode; wide?: boolean }) {
  return (
    <div
      className={`mx-auto flex w-full flex-col gap-6 px-4 py-6 sm:px-6 lg:px-8 ${
        wide ? 'max-w-7xl' : 'max-w-6xl'
      }`}
    >
      {children}
    </div>
  );
}

export function PageHeader({
  title,
  description,
  actions,
  children,
}: {
  title: string;
  description?: ReactNode;
  actions?: ReactNode;
  /** Rendered under the title — status badges, for example. */
  children?: ReactNode;
}) {
  return (
    <header className="flex flex-wrap items-start justify-between gap-4">
      <div className="flex min-w-0 flex-col gap-2">
        <h1 className="text-balance text-2xl font-semibold tracking-tight text-foreground">
          {title}
        </h1>
        {description ? (
          <p className="max-w-2xl text-sm text-muted-foreground">{description}</p>
        ) : null}
        {children}
      </div>
      {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
    </header>
  );
}

export function BackLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link
      to={to}
      className="inline-flex w-fit items-center gap-1.5 rounded-sm text-sm text-muted-foreground transition-colors hover:text-foreground"
    >
      <ArrowLeftIcon />
      {children}
    </Link>
  );
}
