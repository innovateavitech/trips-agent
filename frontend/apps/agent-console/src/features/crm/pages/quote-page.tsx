import { ArrowLeft, Copy, Plus, Trash2 } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  ErrorState,
  Input,
  LoadingState,
  Textarea,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { formatDay, plusDays, todayInLagos } from '../../departures/departure-rules';
import { useLead, useQuote, useSaveQuote, useSendQuote } from '../crm-api';
import {
  buildQuoteRequest,
  draftFromQuote,
  emptyItem,
  emptyQuoteDay,
  emptyQuoteDraft,
  quoteTotalMinor,
  relativeTime,
  type Problems,
  type QuoteDraft,
} from '../crm-rules';
import { QuoteStatusBadge } from '../components/crm-parts';
import type { Quote } from '../types';

/**
 * Build plan F7 — a quote: `/crm/leads/:id/quotes/new` starts one for a lead,
 * `/crm/quotes/:id` opens it. A draft is edited here; once sent it is what the
 * customer has, and it is shown as sent, never edited.
 */
export function QuotePage() {
  const { quoteId, leadId } = useParams();
  return quoteId ? <ExistingQuote id={quoteId} /> : <NewQuote leadId={leadId ?? ''} />;
}

function NewQuote({ leadId }: { leadId: string }) {
  const lead = useLead(leadId);

  if (lead.isPending) return <LoadingState size="page" label="Opening the lead" />;
  if (lead.isError) return <ErrorState {...describeError(lead.error)} />;

  return (
    <QuoteBuilder
      leadId={leadId}
      customerName={lead.data.customer.name}
      quote={null}
      initial={emptyQuoteDraft(lead.data, todayInLagos(), plusDays)}
    />
  );
}

function ExistingQuote({ id }: { id: string }) {
  const quote = useQuote(id);

  if (quote.isPending) return <LoadingState size="page" label="Opening the quote" />;
  if (quote.isError) {
    return (
      <ErrorState
        {...describeError(quote.error)}
        onRetry={() => void quote.refetch()}
        retrying={quote.isFetching}
      />
    );
  }

  return quote.data.status === 'Draft' ? (
    <QuoteBuilder
      key={quote.data.id}
      leadId={quote.data.leadId}
      customerName={quote.data.customer.name}
      quote={quote.data}
      initial={draftFromQuote(quote.data)}
    />
  ) : (
    <SentQuote quote={quote.data} />
  );
}

function QuoteBuilder({
  leadId,
  customerName,
  quote,
  initial,
}: {
  leadId: string;
  customerName: string;
  quote: Quote | null;
  initial: QuoteDraft;
}) {
  const save = useSaveQuote();
  const send = useSendQuote();
  const navigate = useNavigate();
  const [draft, setDraft] = useState(initial);
  const [problems, setProblems] = useState<Problems>({});
  const [confirming, setConfirming] = useState(false);
  const today = todayInLagos();
  const currency = quote?.currency ?? 'NGN';
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, currency);

  const built = buildQuoteRequest(draft, today);
  const total = built.ok ? quoteTotalMinor(built.request.items) : null;

  function update(patch: Partial<QuoteDraft>) {
    setDraft((current) => ({ ...current, ...patch }));
  }

  /** Saves the draft; resolves to the saved quote, or null when the form has problems. */
  async function persist(): Promise<Quote | null> {
    const result = buildQuoteRequest(draft, today);
    if (!result.ok) {
      setProblems(result.errors);
      return null;
    }
    setProblems({});
    return save.mutateAsync({ leadId, quoteId: quote?.id ?? null, request: result.request });
  }

  async function onSave(event: FormEvent) {
    event.preventDefault();
    const saved = await persist().catch(() => null);
    if (saved && !quote) navigate(`/crm/quotes/${saved.id}`, { replace: true });
  }

  async function onSend() {
    const saved = await persist().catch(() => null);
    setConfirming(false);
    if (!saved) return;
    send.mutate(saved.id, {
      onSuccess: () => navigate(`/crm/quotes/${saved.id}`, { replace: true }),
    });
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to={`/crm/leads/${leadId}`}
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          {customerName}
        </Link>
      </div>

      <PageHeader
        title={quote ? `${quote.quoteNumber} · draft` : 'New quote'}
        description={`For ${customerName}. Nothing reaches them until you send it.`}
      />

      {save.isError ? (
        <ErrorState title="We could not save it" detail={describeError(save.error).detail} />
      ) : null}
      {send.isError ? (
        <ErrorState title="We could not send it" detail={describeError(send.error).detail} />
      ) : null}

      <form onSubmit={onSave} noValidate aria-label="Quote" className="flex flex-col gap-6">
        <Card className="flex flex-col gap-4 p-5">
          <div className="grid items-start gap-3 sm:grid-cols-3">
            <div className="sm:col-span-2">
              <Input
                label="Title"
                value={draft.title}
                onChange={(event) => update({ title: event.target.value })}
                error={problems['title']}
              />
            </div>
            <Input
              type="date"
              label="Valid until"
              value={draft.validUntil}
              onChange={(event) => update({ validUntil: event.target.value })}
              error={problems['validUntil']}
            />
          </div>
        </Card>

        <Card className="flex flex-col gap-4 p-5">
          <h2 className="text-base font-semibold text-foreground">What it includes</h2>
          {problems['items'] ? (
            <p className="text-sm text-destructive">{problems['items']}</p>
          ) : null}
          <ol aria-label="Quote items" className="flex flex-col gap-3">
            {draft.items.map((item, index) => {
              const change = (patch: Partial<typeof item>) =>
                update({
                  items: draft.items.map((row, i) => (i === index ? { ...row, ...patch } : row)),
                });

              return (
                <li
                  key={item.key}
                  className="flex flex-wrap items-end gap-3 rounded-lg border border-border p-3"
                >
                  <div className="min-w-0 flex-1 basis-64">
                    <Input
                      label={`Item ${index + 1}`}
                      placeholder="Four nights at a beach hotel, double room"
                      value={item.description}
                      onChange={(event) => change({ description: event.target.value })}
                      error={problems[`items.${index}.description`]}
                    />
                  </div>
                  <div className="w-20">
                    <Input
                      label="Qty"
                      inputMode="numeric"
                      value={item.quantity}
                      onChange={(event) => change({ quantity: event.target.value })}
                      error={problems[`items.${index}.quantity`]}
                    />
                  </div>
                  <div className="w-44">
                    <Input
                      label={`Price each (${currency})`}
                      inputMode="decimal"
                      value={item.unitPrice}
                      onChange={(event) => change({ unitPrice: event.target.value })}
                      error={problems[`items.${index}.unitPrice`]}
                    />
                  </div>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={`Remove item ${index + 1}`}
                    onClick={() => update({ items: draft.items.filter((_, i) => i !== index) })}
                  >
                    <Trash2 aria-hidden="true" className="h-4 w-4" />
                  </Button>
                </li>
              );
            })}
          </ol>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => update({ items: [...draft.items, emptyItem()] })}
            >
              <Plus aria-hidden="true" className="h-4 w-4" />
              Add an item
            </Button>
            <p className="text-sm text-muted-foreground">
              Total{' '}
              <span className="text-lg font-semibold tabular-nums text-foreground">
                {total === null ? '—' : money(total)}
              </span>
            </p>
          </div>
        </Card>

        <Card className="flex flex-col gap-4 p-5">
          <h2 className="text-base font-semibold text-foreground">Day by day (optional)</h2>
          {draft.itinerary.map((day, index) => {
            const change = (patch: Partial<typeof day>) =>
              update({
                itinerary: draft.itinerary.map((row, i) =>
                  i === index ? { ...row, ...patch } : row,
                ),
              });

            return (
              <div
                key={day.key}
                className="flex flex-col gap-2 rounded-lg border border-border p-3"
              >
                <div className="flex items-end gap-2">
                  <div className="min-w-0 flex-1">
                    <Input
                      label={`Day ${index + 1}`}
                      value={day.title}
                      onChange={(event) => change({ title: event.target.value })}
                    />
                  </div>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={`Remove day ${index + 1}`}
                    onClick={() =>
                      update({ itinerary: draft.itinerary.filter((_, i) => i !== index) })
                    }
                  >
                    <Trash2 aria-hidden="true" className="h-4 w-4" />
                  </Button>
                </div>
                <Textarea
                  label="What happens"
                  rows={2}
                  value={day.description}
                  onChange={(event) => change({ description: event.target.value })}
                />
              </div>
            );
          })}
          <div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => update({ itinerary: [...draft.itinerary, emptyQuoteDay()] })}
            >
              <Plus aria-hidden="true" className="h-4 w-4" />
              Add a day
            </Button>
          </div>
          <Textarea
            label="Notes for the customer"
            hint="What is held and what is not, what happens next"
            rows={3}
            value={draft.notes}
            onChange={(event) => update({ notes: event.target.value })}
          />
        </Card>

        <div className="flex flex-wrap justify-end gap-2">
          <Button type="submit" variant="outline" loading={save.isPending && !confirming}>
            Save draft
          </Button>
          <Button type="button" onClick={() => setConfirming(true)} disabled={send.isPending}>
            Send to {customerName.split(' ')[0]}
          </Button>
        </div>
      </form>

      <Dialog open={confirming} onOpenChange={(open) => (open ? undefined : setConfirming(false))}>
        <DialogContent>
          <DialogTitle>Send this quote to {customerName}?</DialogTitle>
          <DialogDescription>
            They get a link on your website where they can read it and accept it. Once sent, it
            cannot be changed: new terms mean a new quote.
          </DialogDescription>
          {total !== null ? (
            <p className="text-2xl font-semibold tabular-nums text-foreground">{money(total)}</p>
          ) : null}
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)}>
              Not yet
            </Button>
            <Button onClick={() => void onSend()} loading={save.isPending || send.isPending}>
              Send quote
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function SentQuote({ quote }: { quote: Quote }) {
  const [copied, setCopied] = useState(false);
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, quote.currency);

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to={`/crm/leads/${quote.leadId}`}
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          {quote.customer.name}
        </Link>
      </div>

      <PageHeader
        title={`${quote.quoteNumber} · ${quote.title}`}
        description={`For ${quote.customer.name} · valid until ${formatDay(quote.validUntil)}`}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <QuoteStatusBadge status={quote.status} />
            <Link
              to={`/crm/leads/${quote.leadId}/quotes/new`}
              className={buttonVariants({ variant: 'outline', size: 'sm' })}
            >
              Make a new quote
            </Link>
          </div>
        }
      />

      {quote.publicUrl ? (
        <Alert tone="info" title="The customer's link">
          <span className="flex flex-wrap items-center gap-2">
            <span className="break-all font-medium">{quote.publicUrl}</span>
            <Button
              variant="outline"
              size="sm"
              onClick={() => {
                void navigator.clipboard?.writeText(quote.publicUrl ?? '');
                setCopied(true);
              }}
            >
              <Copy aria-hidden="true" className="h-4 w-4" />
              {copied ? 'Copied' : 'Copy'}
            </Button>
          </span>
        </Alert>
      ) : null}

      <Card className="flex flex-col gap-3 p-5">
        <ul className="flex flex-col divide-y divide-border">
          {quote.items.map((item, index) => (
            <li key={index} className="flex flex-wrap justify-between gap-2 py-2 text-sm">
              <span className="text-foreground">
                {item.description}
                {item.quantity > 1 ? (
                  <span className="text-muted-foreground"> × {item.quantity}</span>
                ) : null}
              </span>
              <span className="tabular-nums text-foreground">
                {money(item.quantity * item.unitPriceMinor)}
              </span>
            </li>
          ))}
        </ul>
        <p className="flex justify-between border-t border-border pt-3 text-sm">
          <span className="text-muted-foreground">Total</span>
          <span className="text-lg font-semibold tabular-nums text-foreground">
            {money(quote.totalMinor)}
          </span>
        </p>
      </Card>

      <p className="text-sm text-muted-foreground">
        {[
          quote.sentAt ? `Sent ${relativeTime(quote.sentAt)}` : null,
          quote.viewedAt ? `opened ${relativeTime(quote.viewedAt)}` : 'not opened yet',
          quote.respondedAt
            ? `${quote.status.toLowerCase()} ${relativeTime(quote.respondedAt)}`
            : null,
        ]
          .filter(Boolean)
          .join(' · ')}
      </p>
    </div>
  );
}
