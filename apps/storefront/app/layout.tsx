import type { ReactNode } from 'react';
import './globals.css';

/**
 * Root layout. In production this resolves the agent from the Host header and
 * renders THEIR branding — never ours. See CLAUDE.md rule 4 and issue #60.
 */
export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" className="font-sans">
      <body>{children}</body>
    </html>
  );
}
