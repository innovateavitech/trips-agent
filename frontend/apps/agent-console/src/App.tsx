import { Button, Input } from '@trips/ui';
import { formatMoney } from '@trips/utils';

/**
 * Placeholder shell. Replaced by the real app in issue #48.
 * Everything below uses design tokens — no hard-coded colours anywhere.
 */
export function App() {
  return (
    <main className="mx-auto flex max-w-md flex-col gap-6 p-8">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Trips Agent Console
        </h1>
        <p className="text-sm text-muted-foreground">
          Scaffold only — see docs/BACKLOG.md for what gets built here.
        </p>
      </header>

      <section className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4">
        <p className="text-sm text-card-foreground">
          Wallet balance: <span className="font-medium">{formatMoney(150000, 'NGN')}</span>
        </p>
        <Input label="Top-up amount" placeholder="0.00" hint="Minimum ₦1,000" />
        <div className="flex gap-2">
          <Button>Top up</Button>
          <Button variant="outline">Cancel</Button>
        </div>
      </section>
    </main>
  );
}
