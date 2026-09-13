import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { getSite } from '../../lib/api';
import { EnquiryForm } from '../../components/enquiry-form';

/**
 * "Tell us where you would like to go." The traveller's enquiry lands in the agency's CRM as a new
 * lead (issue 62), and the agency answers with a quote.
 */

export const metadata: Metadata = {
  title: 'Plan your trip',
  description: 'Tell us where you would like to go and we will put some options together for you.',
  alternates: { canonical: '/enquire' },
};

export default async function EnquirePage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const [site, query] = await Promise.all([getSite(), searchParams]);

  if (!site) {
    notFound();
  }

  const destination = typeof query.destination === 'string' ? query.destination : undefined;

  return (
    <div className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">Plan your trip</h1>
      <p className="mt-2 text-base text-muted-foreground">
        Tell us what you have in mind and we will come back to you with some options and prices.
      </p>

      <div className="mt-8">
        <EnquiryForm destination={destination} />
      </div>
    </div>
  );
}
