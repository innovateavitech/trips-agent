import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, Card, ErrorState, Input, Textarea } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useCreateLead } from '../crm-api';
import type { Problems } from '../crm-rules';
import { EMPTY_LEAD_FORM, buildLeadRequest, type LeadForm } from '../lead-form';

/**
 * Build plan F7 — a lead the team keys in: a phone call, a walk-in. The
 * customer record is found by email or phone, or made from this; nobody
 * creates a customer first.
 */
export function NewLeadPage() {
  const create = useCreateLead();
  const navigate = useNavigate();
  const [form, setForm] = useState<LeadForm>(EMPTY_LEAD_FORM);
  const [problems, setProblems] = useState<Problems>({});

  const field = (name: keyof LeadForm) => ({
    value: form[name],
    onChange: (event: { target: { value: string } }) =>
      setForm((current) => ({ ...current, [name]: event.target.value })),
    error: problems[name],
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    const built = buildLeadRequest(form);
    if (!built.ok) {
      setProblems(built.errors);
      return;
    }
    setProblems({});
    create.mutate(built.request, {
      onSuccess: (lead) => navigate(`/crm/leads/${lead.id}`, { replace: true }),
    });
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="New lead"
        description="Someone who called, wrote or walked in about a trip."
      />

      {create.isError ? (
        <ErrorState title="We could not add it" detail={describeError(create.error).detail} />
      ) : null}

      <form onSubmit={submit} noValidate aria-label="New lead" className="flex flex-col gap-6">
        <Card className="flex flex-col gap-4 p-5">
          <h2 className="text-base font-semibold text-foreground">Who</h2>
          <div className="grid items-start gap-3 sm:grid-cols-3">
            <Input label="Name" autoComplete="off" {...field('name')} />
            <Input type="email" label="Email" autoComplete="off" {...field('email')} />
            <Input type="tel" label="Phone" autoComplete="off" {...field('phone')} />
          </div>
        </Card>

        <Card className="flex flex-col gap-4 p-5">
          <h2 className="text-base font-semibold text-foreground">The trip</h2>
          <Input
            label="Where to"
            placeholder="Dubai, a Kenyan safari, Obudu…"
            {...field('destination')}
          />
          <div className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <Input type="date" label="Leaving" {...field('travelFrom')} />
            <Input type="date" label="Back" {...field('travelTo')} />
            <Input label="Adults" inputMode="numeric" {...field('adults')} />
            <Input label="Children" inputMode="numeric" {...field('children')} />
            <Input label="Budget from (NGN)" inputMode="decimal" {...field('budgetMin')} />
            <Input label="Budget up to (NGN)" inputMode="decimal" {...field('budgetMax')} />
          </div>
          <Textarea label="What they asked for" rows={4} {...field('message')} />
        </Card>

        <div className="flex justify-end">
          <Button type="submit" size="lg" loading={create.isPending}>
            Add lead
          </Button>
        </div>
      </form>
    </div>
  );
}
