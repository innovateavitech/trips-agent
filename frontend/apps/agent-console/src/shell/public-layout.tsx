import { Outlet } from 'react-router-dom';

/**
 * The frame for everything before sign-in: sign in, register, verify email,
 * reset password (#49 fills in all but the first).
 *
 * The left panel is the same ink as the console's sidebar, so arriving in the
 * console after signing in feels like walking further into the same room. It
 * hides below `lg`, where the form alone is the page.
 *
 * This is the agent's console, so it carries the Trips name. Nothing a
 * TRAVELLER sees ever does (CLAUDE.md rule 4) — that is the storefront's job.
 */
export function PublicLayout() {
  return (
    <div className="grid min-h-screen bg-background lg:grid-cols-5">
      <aside className="hidden flex-col justify-between bg-sidebar p-10 text-sidebar-foreground lg:col-span-2 lg:flex">
        <div className="flex items-center gap-2.5">
          <span
            aria-hidden="true"
            className="flex h-8 w-8 items-center justify-center rounded-md bg-primary text-sm font-bold text-primary-foreground"
          >
            T
          </span>
          <span className="text-base font-semibold">Trips for agents</span>
        </div>

        <div className="flex max-w-md flex-col gap-4">
          <p className="text-3xl font-semibold leading-tight tracking-tight">
            Sell flights and buses under your own name.
          </p>
          <p className="text-sm leading-relaxed text-sidebar-muted-foreground">
            Book Air Peace, Ibom Air, Arik and United Nigeria at net rates, add your own markup, and
            pay from one prepaid wallet. Your travellers only ever see your brand.
          </p>
        </div>

        <DepartureBoard />
      </aside>

      <div className="flex items-center justify-center px-4 py-10 sm:px-6 lg:col-span-3">
        <div className="w-full max-w-sm">
          <Outlet />
        </div>
      </div>
    </div>
  );
}

const DEPARTURES = [
  { time: '06:45', from: 'LOS', to: 'ABV', carrier: 'Air Peace' },
  { time: '08:10', from: 'PHC', to: 'LOS', carrier: 'Ibom Air' },
  { time: '09:30', from: 'KAN', to: 'ABV', carrier: 'United Nigeria' },
  { time: '11:55', from: 'QOW', to: 'LOS', carrier: 'Arik Air' },
];

/** A few of the morning's domestic departures — the thing agents sell. */
function DepartureBoard() {
  return (
    <table className="w-full max-w-md text-sm tabular-nums">
      <caption className="pb-2 text-left text-xs text-sidebar-muted-foreground">
        Morning departures
      </caption>
      <tbody>
        {DEPARTURES.map((d) => (
          <tr key={`${d.from}-${d.to}`} className="border-t border-sidebar-border">
            <td className="py-2 pr-4 font-medium">{d.time}</td>
            <td className="py-2 pr-4">
              {d.from} <span className="text-sidebar-muted-foreground">to</span> {d.to}
            </td>
            <td className="py-2 text-right text-sidebar-muted-foreground">{d.carrier}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
