import { cn } from '../lib/cn';

export interface AvatarProps {
  name: string;
  size?: number;
  className?: string;
  /**
   * `primary` (default): a filled brand-blue circle — the signed-in agent's
   * own avatar in the header. `muted`: a bordered, neutral circle for
   * listing other people or companies (the Figma Travel table's look) — so
   * a row of customers doesn't read as a row of "you".
   */
  tone?: 'primary' | 'muted';
}

const TONE_CLASSES: Record<NonNullable<AvatarProps['tone']>, string> = {
  primary: 'bg-primary text-primary-foreground',
  muted: 'border border-border-subtle bg-muted text-foreground',
};

/**
 * A circular initial badge. No photo storage exists for a person's profile
 * yet, so every avatar is derived from their name rather than an image URL —
 * this is the fallback, not a placeholder for one.
 */
export function Avatar({ name, size = 40, className, tone = 'primary' }: AvatarProps) {
  const initial = name.trim().charAt(0).toUpperCase() || '?';

  return (
    <div
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-full font-medium',
        TONE_CLASSES[tone],
        className,
      )}
      style={{ width: size, height: size, fontSize: size * 0.45 }}
      aria-hidden="true"
    >
      {initial}
    </div>
  );
}
