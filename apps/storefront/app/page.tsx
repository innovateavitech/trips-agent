import { formatMoney } from '@trips/utils';

export default function Home() {
  return (
    <main>
      <h1>Storefront</h1>
      <p>
        Scaffold only. This becomes each agent&apos;s branded, server-rendered site —
        see issue #60 in docs/BACKLOG.md.
      </p>
      <p>Money helper check: {formatMoney(150000, 'NGN')}</p>
    </main>
  );
}
