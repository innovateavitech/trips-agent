import { Button } from '@trips/ui';
import { formatMoney } from '@trips/utils';

/**
 * Placeholder shell. Replaced by the real app in issue #48 (agent console).
 * The imports below exist to prove the workspace wiring resolves.
 */
export function App() {
  return (
    <main>
      <h1>Trips Admin Console</h1>
      <p>Scaffold only — see docs/BACKLOG.md for what gets built here.</p>
      <p>Money helper check: {formatMoney(150000, 'NGN')}</p>
      <Button>Placeholder</Button>
    </main>
  );
}
