import { Alert, buttonVariants } from '@trips/ui';
import { Link } from 'react-router-dom';
import type { KybStatus } from '../types';

export interface ReviewBannerProps {
  status: KybStatus;
}

/**
 * What is happening, in one sentence, at the top of the screen.
 *
 * Every state says what happens next. "Pending review" with nothing else is the
 * kind of screen that generates support tickets: the agent cannot tell whether
 * they still have something to do, or how long to leave it before asking.
 */
export function ReviewBanner({ status }: ReviewBannerProps) {
  if (status.agencyStatus === 'Verified') {
    return (
      <Alert
        tone="success"
        title="Your business is verified"
        action={
          <Link to="/wallet" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            Add funds
          </Link>
        }
      >
        You can add funds and issue tickets. Nothing further is needed here.
      </Alert>
    );
  }

  if (status.status === 'Rejected' || status.agencyStatus === 'Rejected') {
    return (
      <Alert tone="destructive" title="We could not verify your business yet">
        {status.rejectionReason !== null && status.rejectionReason.length > 0 ? (
          <>
            <span className="font-medium">Reviewer's note:</span> {status.rejectionReason}
          </>
        ) : (
          'One or more documents could not be accepted.'
        )}{' '}
        Replace the document below and submit again — you do not start over.
      </Alert>
    );
  }

  if (status.status === 'Submitted' || status.status === 'UnderReview') {
    return (
      <Alert tone="info" title="With Trips for review">
        Your documents are being checked. Most reviews finish within one working day, and this page
        updates on its own — there is nothing else for you to do. If a document needs changing we
        will say exactly which one and why.
      </Alert>
    );
  }

  return (
    <Alert tone="warning" title="Verification is not finished">
      {status.walletFundingBlockedReason ??
        'Upload the documents below so Trips can verify your business.'}
    </Alert>
  );
}
