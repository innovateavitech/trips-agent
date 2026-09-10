import { formatMoney } from '@trips/utils';

export default function Home() {
  return (
    <main className="mx-auto flex max-w-2xl flex-col gap-4 p-8">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">Storefront</h1>
      <p className="text-muted-foreground">
        Scaffold only. This becomes each agent&apos;s branded, server-rendered site — see issue
        #60 in docs/BACKLOG.md.
      </p>
      <p className="text-sm text-foreground">
        Example fare: <span className="font-medium">{formatMoney(84451900, 'NGN')}</span>
      </p>
    </main>
  );
}
