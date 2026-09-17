import { X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import {
  Alert,
  Button,
  Input,
  SegmentedControl,
  Sheet,
  SheetClose,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useCreateCustomer } from '../crm-api';
import type { Problems } from '../crm-rules';
import {
  CUSTOMER_KINDS,
  EMPTY_CUSTOMER_FORM,
  buildCustomerRequest,
  type CustomerForm,
} from '../customer-form';
import type { Customer } from '../types';

/**
 * "Add customer", from the Customers screen: a panel from the right, in the
 * look of the Figma "Travellers profile" panel (node 188:3297) — a titled
 * header with a close button, the fields, and one full-width save at the foot.
 *
 * A customer needs a name and an email or a phone number. On save the panel
 * closes and `onCreated` gets the new record, so the caller can open it.
 */
export function AddCustomerSheet({
  open,
  onOpenChange,
  onCreated,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated: (customer: Customer) => void;
}) {
  const create = useCreateCustomer();
  const [form, setForm] = useState<CustomerForm>(EMPTY_CUSTOMER_FORM);
  const [problems, setProblems] = useState<Problems>({});

  function change(open: boolean) {
    if (!open) {
      // Closing throws the draft away: the next "Add customer" starts clean.
      setForm(EMPTY_CUSTOMER_FORM);
      setProblems({});
      create.reset();
    }
    onOpenChange(open);
  }

  const field = (name: 'name' | 'email' | 'phone') => ({
    value: form[name],
    onChange: (event: { target: { value: string } }) =>
      setForm((current) => ({ ...current, [name]: event.target.value })),
    error: problems[name],
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    const built = buildCustomerRequest(form);
    if (!built.ok) {
      setProblems(built.errors);
      return;
    }
    setProblems({});
    create.mutate(built.request, {
      onSuccess: (customer) => {
        change(false);
        onCreated(customer);
      },
    });
  }

  const business = form.kind === 'business';

  return (
    <Sheet open={open} onOpenChange={change}>
      <SheetContent
        side="right"
        className="w-full max-w-full bg-card sm:w-[32.5rem] sm:rounded-l-[2rem]"
      >
        <form onSubmit={submit} noValidate className="flex h-full flex-col">
          <header className="flex h-[4.5rem] shrink-0 items-center justify-between gap-4 border-b border-border-subtle px-6">
            <SheetTitle className="text-lg font-medium text-foreground">Add customer</SheetTitle>
            <SheetClose
              className="inline-flex size-8 items-center justify-center rounded-full text-foreground hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              aria-label="Close"
            >
              <X aria-hidden="true" className="h-5 w-5" />
            </SheetClose>
          </header>

          <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-6">
            <SheetDescription className="text-sm text-muted-foreground">
              Someone you sell travel to. Their bookings, invoices and travellers collect on this
              record.
            </SheetDescription>

            <div className="flex flex-col gap-2">
              <p aria-hidden="true" className="text-sm font-medium text-foreground">
                Customer type
              </p>
              <SegmentedControl
                label="Customer type"
                appearance="track"
                options={CUSTOMER_KINDS}
                value={form.kind}
                onChange={(kind) => setForm((current) => ({ ...current, kind }))}
                className="self-start"
              />
            </div>

            <Input
              label={business ? 'Business name' : 'Full name'}
              placeholder={business ? 'Harbour Point Logistics' : 'Adaeze Okafor'}
              autoComplete={business ? 'organization' : 'name'}
              autoFocus
              {...field('name')}
            />

            <div className="flex flex-col gap-4 border-t border-border-subtle pt-6">
              <div className="flex flex-col gap-1">
                <h3 className="text-base font-medium text-foreground">Contact</h3>
                <p className="text-sm text-muted-foreground">
                  An email or a phone number — both if you have them.
                </p>
              </div>
              <Input
                type="email"
                label="Email"
                placeholder={business ? 'travel@company.com' : 'name@email.com'}
                autoComplete="email"
                {...field('email')}
              />
              <Input
                type="tel"
                label="Phone number"
                placeholder="+234 803 000 0000"
                autoComplete="tel"
                {...field('phone')}
              />
            </div>

            {create.isError ? (
              <Alert tone="destructive" title={describeError(create.error).title}>
                {describeError(create.error).detail}
              </Alert>
            ) : null}
          </div>

          <footer className="shrink-0 border-t border-border-subtle px-6 py-6">
            <Button type="submit" size="lg" radius="lg" fullWidth loading={create.isPending}>
              Save customer
            </Button>
          </footer>
        </form>
      </SheetContent>
    </Sheet>
  );
}
