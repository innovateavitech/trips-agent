import type { ButtonHTMLAttributes, ReactNode } from 'react';

type Variant = 'primary' | 'secondary' | 'danger';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  children: ReactNode;
}

/**
 * Placeholder so the workspace wiring is provably correct — every app imports
 * this to prove the shared package resolves. Replaced by the real design
 * system in issue #48.
 */
export function Button({ variant = 'primary', children, ...rest }: ButtonProps) {
  return (
    <button data-variant={variant} {...rest}>
      {children}
    </button>
  );
}
