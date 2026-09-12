import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Badge, Button, Card, ErrorState, Input, LoadingState } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { DomainCard } from '../components/domain-card';
import { useAddSiteDomain, useSiteDomains } from '../storefront-queries';

/**
 * ============================================================================
 *  Issue 59 — the addresses the agency's website answers on.
 * ============================================================================
 *
 * Every agency gets a free address the moment they make a site, so their shop is reachable on day
 * one. Connecting their own domain is the slow part: it needs two records at whoever they bought
 * the name from, and those take minutes to hours to spread.
 *
 * Which is why this screen is mostly about waiting well. It shows exactly what to create, spelled
 * the way a registrar's form asks for it, what we last looked for and what we found instead, and a
 * button to look again now — because the commonest question at this point is "has it worked yet?"
 * and the worst answer is silence.
 */
export function AddressesPage() {
  const domains = useSiteDomains();
  const add = useAddSiteDomain();
  const [hostname, setHostname] = useState('');

  if (domains.isPending) {
    return <LoadingState size="page" label="Loading your web addresses" />;
  }

  if (domains.isError) {
    return (
      <ErrorState
        title={describeError(domains.error).title}
        onRetry={() => void domains.refetch()}
      />
    );
  }

  const free = domains.data.filter((domain) => domain.type === 'Subdomain');
  const custom = domains.data.filter((domain) => domain.type === 'Custom');

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Your web address"
        description={
          <>
            Where travellers find your website —{' '}
            <Link to="/website" className="text-primary underline-offset-4 hover:underline">
              back to your website
            </Link>
            .
          </>
        }
      />

      {free.map((domain) => (
        <DomainCard key={domain.id} domain={domain} />
      ))}

      <Card className="p-6">
        <h2 className="text-lg font-semibold text-foreground">Use your own domain</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          If you own a domain name — <span className="font-medium">yourbusiness.com</span> — you can
          point it at your website. You will need to sign in wherever you bought it.
        </p>

        {add.isError ? (
          <Alert tone="destructive" className="mt-4">
            {describeError(add.error).title}
            {describeError(add.error).detail ? (
              <p className="mt-1">{describeError(add.error).detail}</p>
            ) : null}
          </Alert>
        ) : null}

        <form
          className="mt-4 flex flex-wrap items-end gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            if (hostname.trim().length === 0) return;
            add.mutate(hostname.trim(), { onSuccess: () => setHostname('') });
          }}
        >
          <div className="min-w-64 flex-1">
            <Input
              label="Your domain name"
              hint="Pasting the whole address is fine — we will take the domain out of it."
              value={hostname}
              onChange={(event) => setHostname(event.target.value)}
              error={fieldError(add.error, 'Hostname')}
            />
          </div>

          <Button type="submit" disabled={add.isPending}>
            {add.isPending ? 'Adding…' : 'Add this domain'}
          </Button>
        </form>
      </Card>

      {custom.map((domain) => (
        <DomainCard key={domain.id} domain={domain} />
      ))}

      {custom.length === 0 ? null : (
        <p className="text-sm text-muted-foreground">
          Your main address is the one search engines are pointed at. The others still work and send
          visitors to the same website.
        </p>
      )}

      {domains.data.some((domain) => domain.needsReview) ? (
        <Alert tone="warning">
          <Badge tone="warning">Being checked</Badge>
          <p className="mt-2">
            One of your addresses looks like a well-known brand name, so somebody is looking at it
            before it goes live. This normally takes a working day. If it is your own business name,
            nothing further is needed from you.
          </p>
        </Alert>
      ) : null}
    </div>
  );
}
