import type { ReactNode } from 'react';
import { Badge, Card, CardContent, CardHeader, CardTitle } from '@trips/ui';
import { countryName, formatBasisPoints, formatDateTime, formatMoney } from '../../../lib/format';
import { agencyStatusDisplay } from '../agency-rules';
import type { AgencyProfile } from '../types';

/**
 * The facts about one agency, in three cards: who they are, what they hold, and who works there.
 *
 * A definition list rather than a table: these are attributes of one thing, and a screen reader
 * reads a `dl` as the label-and-value pairs they are.
 */
export function AgencyFacts({ profile }: { profile: AgencyProfile }) {
  const status = agencyStatusDisplay(profile.status);

  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Business</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
            <Fact label="Legal name">{profile.legalName}</Fact>
            <Fact label="Trading as">{profile.tradingName ?? '—'}</Fact>
            <Fact label="Storefront slug">
              <code className="rounded-sm bg-muted px-1 py-0.5 text-xs">{profile.slug}</code>
            </Fact>
            <Fact label="Country">{countryName(profile.countryCode)}</Fact>
            <Fact label="Tax ID">{profile.taxId ?? 'Not collected'}</Fact>
            <Fact label="VAT rate">{formatBasisPoints(profile.vatRateBasisPoints)}</Fact>
            <Fact label="Timezone">{profile.timezone}</Fact>
            <Fact label="Type">
              {profile.type === 'SubAgent'
                ? `Sub-agent of ${profile.parentAgencyName ?? 'a principal'}`
                : 'Principal'}
            </Fact>
          </dl>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Standing and trading</CardTitle>
        </CardHeader>
        <CardContent>
          <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
            <Fact label="Status">
              <Badge tone={status.tone}>{status.label}</Badge>
            </Fact>
            <Fact label="Storefront">{profile.storefrontIsLive ? 'Live' : 'Offline'}</Fact>
            <Fact label="Signed up">{formatDateTime(profile.createdAt)}</Fact>
            <Fact label="Verified">{formatDateTime(profile.verifiedAt)}</Fact>
            <Fact label="Wallet balance">
              {profile.walletBalanceMinor === null
                ? 'No wallet until KYB is approved'
                : formatMoney(profile.walletBalanceMinor, profile.baseCurrency)}
            </Fact>
            <Fact label="Held against bookings">
              {profile.walletReservedMinor === null
                ? '—'
                : formatMoney(profile.walletReservedMinor, profile.baseCurrency)}
            </Fact>
            <Fact label="Orders placed">{profile.orderCount}</Fact>
            <Fact label="Gross sales">
              {formatMoney(profile.grossSalesMinor, profile.baseCurrency)}
            </Fact>
          </dl>
        </CardContent>
      </Card>
    </div>
  );
}

/** The people who can sign in at this agency, and what each of them may do there. */
export function AgencyStaff({ profile }: { profile: AgencyProfile }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Staff ({profile.users.length})</CardTitle>
      </CardHeader>
      <CardContent>
        {profile.users.length === 0 ? (
          <p className="text-sm text-muted-foreground">Nobody can sign in at this agency yet.</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border">
            {profile.users.map((user) => (
              <li key={user.id} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 py-2.5">
                <span className="font-medium text-foreground">{user.fullName}</span>
                <span className="text-sm text-muted-foreground">{user.email}</span>
                <span className="ml-auto flex items-center gap-2">
                  {user.roles.map((role) => (
                    <Badge key={role}>{role}</Badge>
                  ))}
                  <span className="text-xs text-muted-foreground">
                    Last in {formatDateTime(user.lastLoginAt)}
                  </span>
                </span>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="text-sm text-foreground">{children}</dd>
    </div>
  );
}
