import { useSearchParams } from 'react-router-dom';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@trips/ui';
import { abandonMockTopUp, settleMockTopUp } from './mock-wallet-api';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when issue #26 lands.
 * ============================================================================
 *
 * Stands in for Paystack's hosted checkout page so the top-up flow can be
 * walked end to end — including the failure and abandonment branches, which are
 * the ones that are otherwise never exercised until a real agent hits them.
 *
 * The real page is on Paystack's origin and takes the card details there. This
 * one takes nothing, and never should: the whole point of a hosted page is that
 * no card number is ever typed into our application.
 */
export function MockGatewayPage() {
  const [params] = useSearchParams();
  const reference = params.get('reference') ?? '';

  function finish(outcome: 'success' | 'failure' | 'abandon') {
    if (outcome === 'abandon') {
      abandonMockTopUp(reference);
    } else {
      settleMockTopUp(reference, outcome);
    }

    // A full-page navigation, matching what a real gateway redirect does.
    window.location.assign(`/wallet/top-up/return?reference=${encodeURIComponent(reference)}`);
  }

  return (
    <div className="flex w-full max-w-md flex-col gap-4">
      <Alert tone="warning" title="This is not a real payment page">
        It stands in for Paystack until the top-up endpoints exist (issue #26). Pick an outcome to
        see how the wallet handles it.
      </Alert>

      <Card>
        <CardHeader>
          <CardTitle>Stand-in payment gateway</CardTitle>
          <CardDescription>Reference {reference || 'missing'}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-2">
          <Button onClick={() => finish('success')}>Pay successfully</Button>
          <Button variant="outline" onClick={() => finish('failure')}>
            Have the payment declined
          </Button>
          <Button variant="ghost" onClick={() => finish('abandon')}>
            Leave without paying
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
