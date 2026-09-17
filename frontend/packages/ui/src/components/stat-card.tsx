import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

export interface StatCardProps {
  label: string;
  /** The figure itself, in the display face — e.g. "₦50,450,356". */
  value: ReactNode;
  /** A trailing fractional part shown smaller and muted, in the numeric face — e.g. ".02". */
  valueSuffix?: ReactNode;
  caption?: ReactNode;
  /** Small icon-button(s) in the top-right corner — a top-up, a link to detail. */
  actions?: ReactNode;
  /** A progress bar or other detail rendered below the figure. */
  children?: ReactNode;
  className?: string;
}

/**
 * A headline figure on its own card: a wallet balance, a period's earnings.
 * Deliberately not built on `Card` — this surface carries no border (the
 * page canvas vs. card-white contrast does that job) and a larger radius
 * than the app's ordinary bordered `Card`, which stays as-is everywhere else.
 */
export function StatCard({
  label,
  value,
  valueSuffix,
  caption,
  actions,
  children,
  className,
}: StatCardProps) {
  return (
    <div className={cn('rounded-[2rem] bg-card p-6', className)}>
      <div className="flex items-start justify-between gap-4">
        <div className="flex flex-col gap-1.5">
          <p className="font-numeric text-sm text-muted-foreground">{label}</p>
          <p className="font-numeric text-3xl font-semibold leading-none tracking-tight text-foreground">
            {value}
            {valueSuffix !== undefined ? (
              <span className="text-xl text-muted-foreground">{valueSuffix}</span>
            ) : null}
          </p>
          {caption ? <p className="text-sm text-muted-foreground">{caption}</p> : null}
        </div>
        {actions ? <div className="flex items-center gap-2">{actions}</div> : null}
      </div>
      {children ? <div className="mt-6">{children}</div> : null}
    </div>
  );
}
