import { forwardRef, useState } from 'react';
import { Input, type InputProps } from './input';

export interface PasswordInputProps extends Omit<InputProps, 'type' | 'trailing'> {
  /**
   * Whether the field starts revealed. Almost never wanted — it exists for a
   * "confirm your new password" field where the first one is already visible.
   */
  defaultVisible?: boolean;
}

/**
 * A password field with a reveal toggle.
 *
 * Hiding a password protects against somebody reading over your shoulder, which
 * is a real risk in a shared travel-agency office — but it also makes a typo
 * invisible, and a typo in a password you cannot see is the most common reason
 * people get locked out. Offering both is what current NIST guidance (SP 800-63B
 * §5.1.1.2) asks for: mask by default, and let the person choose to look.
 *
 * `autoComplete` passes straight through, so a password manager still fills and
 * saves the field. Toggling `type` between `password` and `text` keeps the same
 * element, so the manager does not lose track of it mid-fill.
 */
export const PasswordInput = forwardRef<HTMLInputElement, PasswordInputProps>(
  function PasswordInput({ defaultVisible = false, ...props }, ref) {
    const [visible, setVisible] = useState(defaultVisible);
    const action = visible ? 'Hide password' : 'Show password';

    return (
      <Input
        {...props}
        ref={ref}
        type={visible ? 'text' : 'password'}
        trailing={
          <button
            type="button"
            onClick={() => setVisible((shown) => !shown)}
            aria-label={action}
            aria-pressed={visible}
            title={action}
            className="flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {visible ? <EyeOffIcon /> : <EyeIcon />}
          </button>
        }
      />
    );
  },
);

/*
 * Drawn inline rather than pulled from an icon package, because this package has
 * no icon dependency and two glyphs is not a reason to add one — the same choice
 * dropdown-menu.tsx already makes for its checkmark.
 */

function EyeIcon() {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      className="h-4 w-4"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

function EyeOffIcon() {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      className="h-4 w-4"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M10.7 5.1A9.9 9.9 0 0 1 12 5c6.4 0 10 7 10 7a18 18 0 0 1-2.2 3.1" />
      <path d="M6.2 6.2A18 18 0 0 0 2 12s3.6 7 10 7a9.7 9.7 0 0 0 5.1-1.4" />
      <path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" />
      <path d="m3 3 18 18" />
    </svg>
  );
}
