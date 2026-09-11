import type { LucideIcon } from 'lucide-react';
import { Link } from 'react-router-dom';
import { Badge, Card, buttonVariants } from '@trips/ui';
import { PageHeader } from './page-header';

/**
 * Holds a screen's place until its issue lands.
 *
 * Every M1 screen already has its route and its sidebar entry, so the six
 * screens built after the shell (#49–#55) each start by replacing ONE of these
 * with the real page, inside their own feature folder — no edits to the router
 * or the shell, and no merge conflicts between them.
 *
 * It says plainly what will be here, so a demo walks through the whole console
 * rather than into blank pages.
 */
export function PlannedScreen({
  title,
  description,
  issue,
  icon: Icon,
  capabilities,
  frame = 'app',
}: {
  title: string;
  description: string;
  /** The GitHub issue that builds this screen. */
  issue: number;
  icon: LucideIcon;
  /** What the agent will be able to do here, in their words. */
  capabilities: string[];
  /** `public` for pre-sign-in screens, which have no page header or shell. */
  frame?: 'app' | 'public';
}) {
  const body = (
    <Card className="flex flex-col gap-5 p-6 sm:p-8">
      <div className="flex items-start gap-4">
        <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary-subtle text-primary">
          <Icon aria-hidden="true" className="h-5 w-5" />
        </span>
        <div className="flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="text-base font-semibold text-foreground">This screen is being built</p>
            <Badge tone="info">Issue #{issue}</Badge>
          </div>
          <p className="text-sm text-muted-foreground">
            The route and navigation are in place. Here is what it will let you do:
          </p>
        </div>
      </div>
      <ul className="flex flex-col gap-2 text-sm text-foreground sm:pl-14">
        {capabilities.map((capability) => (
          <li key={capability} className="flex gap-2">
            <span
              aria-hidden="true"
              className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-primary"
            />
            {capability}
          </li>
        ))}
      </ul>
    </Card>
  );

  if (frame === 'public') {
    return (
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">{title}</h1>
          <p className="text-sm text-muted-foreground">{description}</p>
        </div>
        {body}
        <Link to="/sign-in" className={buttonVariants({ variant: 'outline' })}>
          Back to sign in
        </Link>
      </div>
    );
  }

  return (
    <>
      <PageHeader title={title} description={description} />
      {body}
    </>
  );
}
