import { useState, type FormEvent } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  EmptyState,
  ErrorState,
  Input,
  LoadingState,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import {
  BANK_ACCOUNT_STATUS,
  coolingOff,
  isNuban,
  parsePayoutAmount,
  PAYOUT_STATUS,
  statusCopy,
  type WithdrawableBalance,
} from '../payout-rules';
import {
  useAddBankAccount,
  useBankAccounts,
  useBanks,
  useMakeDefaultAccount,
  usePayouts,
  useRequestPayout,
  useWithdrawableBalance,
  type BankAccount,
} from '../payout-queries';

/**
 * ============================================================================
 *  Build plan F12 — withdrawing the agency's own money to its own bank.
 * ============================================================================
 *
 * Three things on one screen, in the order an agent needs them: how much they
 * can take out and why it is not simply their balance, where it goes, and what
 * happened to the withdrawals they already made.
 */
export function PayoutsPage() {
  const balance = useWithdrawableBalance();
  const accounts = useBankAccounts();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Payouts"
        description="Withdraw your wallet balance to your business bank account. Every withdrawal is checked by our finance team before it is sent."
      />

      {balance.isPending ? (
        <LoadingState label="Loading what you can withdraw" />
      ) : balance.isError ? (
        <ErrorState {...describeError(balance.error)} onRetry={() => void balance.refetch()} />
      ) : (
        <div className="grid gap-6 lg:grid-cols-2">
          <BalanceBreakdown balance={balance.data} />
          <RequestPayoutCard balance={balance.data} accounts={accounts.data ?? []} />
        </div>
      )}

      <BankAccountsCard />
      <PayoutHistoryCard />
    </div>
  );
}

/** The number agents will query most, with the three subtractions behind it. */
export function BalanceBreakdown({ balance }: { balance: WithdrawableBalance }) {
  const money = (minor: number) => formatMoney(minor, balance.currency);

  return (
    <Card>
      <CardHeader>
        <CardDescription>Available to withdraw</CardDescription>
        <CardTitle className="text-3xl">{money(balance.withdrawableMinor)}</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-2 text-sm">
          <dt className="text-muted-foreground">Wallet balance</dt>
          <dd className="text-right tabular-nums">{money(balance.balanceMinor)}</dd>

          <dt className="text-muted-foreground">Held for bookings in progress</dt>
          <dd className="text-right tabular-nums">− {money(balance.reservedMinor)}</dd>

          <dt className="text-muted-foreground">
            Paid in within the last {balance.settlementWindowDays} days, not yet cleared
          </dt>
          <dd className="text-right tabular-nums">− {money(balance.pendingSettlementMinor)}</dd>
        </dl>
        <p className="mt-4 text-xs text-muted-foreground">
          Card payments take a couple of days to reach our bank, so they become withdrawable once
          they clear. Money held for a booking is released if the booking does not go ahead.
        </p>
      </CardContent>
    </Card>
  );
}

function RequestPayoutCard({
  balance,
  accounts,
}: {
  balance: WithdrawableBalance;
  accounts: BankAccount[];
}) {
  const request = useRequestPayout();
  const [amount, setAmount] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const destination = accounts.find((account) => account.isDefault);
  const waiting = coolingOff(destination?.usableFrom, new Date());

  function submit(event: FormEvent) {
    event.preventDefault();
    setDone(null);

    const parsed = parsePayoutAmount(amount, balance);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }

    setError(null);
    request.mutate(
      { amountMinor: parsed.amountMinor, bankAccountId: destination?.id ?? null },
      {
        onSuccess: (result) => {
          setAmount('');
          setDone(
            `Withdrawal ${result.reference} for ${formatMoney(parsed.amountMinor, balance.currency)} is waiting for approval.`,
          );
        },
        onError: (failure) => setError(describeError(failure).detail),
      },
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Request a withdrawal</CardTitle>
        <CardDescription>
          {destination
            ? `To ${destination.bankName} ${destination.maskedNumber} — ${destination.accountNameResolved ?? ''}`
            : 'Add and verify a bank account below first.'}
        </CardDescription>
      </CardHeader>
      <CardContent>
        {waiting ? (
          <Alert tone="info" className="mb-4">
            {waiting}
          </Alert>
        ) : null}
        {done ? (
          <Alert tone="success" className="mb-4">
            {done}
          </Alert>
        ) : null}
        <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
          <Input
            label={`Amount (${balance.currency})`}
            inputMode="decimal"
            value={amount}
            onChange={(event) => setAmount(event.target.value)}
            error={error ?? undefined}
            hint={`Between ${formatMoney(balance.minimumPayoutMinor, balance.currency)} and ${formatMoney(balance.withdrawableMinor, balance.currency)}. Up to ${formatMoney(balance.dailyCapMinor, balance.currency)} a day.`}
          />
          <Button
            type="submit"
            disabled={!destination || request.isPending || !balance.withdrawableMinor}
          >
            {request.isPending ? 'Requesting…' : 'Request withdrawal'}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

function BankAccountsCard() {
  const accounts = useBankAccounts();
  const makeDefault = useMakeDefaultAccount();
  const [adding, setAdding] = useState(false);

  return (
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
        <div>
          <CardTitle>Bank accounts</CardTitle>
          <CardDescription>
            We check every account with your bank and show the name the bank holds, not the one
            typed.
          </CardDescription>
        </div>
        {!adding ? (
          <Button variant="outline" onClick={() => setAdding(true)}>
            Add a bank account
          </Button>
        ) : null}
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {adding ? <AddBankAccountForm onDone={() => setAdding(false)} /> : null}

        {accounts.isPending ? (
          <LoadingState label="Loading bank accounts" />
        ) : accounts.isError ? (
          <ErrorState {...describeError(accounts.error)} onRetry={() => void accounts.refetch()} />
        ) : accounts.data.length === 0 ? (
          <EmptyState title="No bank account yet">
            Add your business account to withdraw your balance.
          </EmptyState>
        ) : (
          <ul className="flex flex-col divide-y divide-border">
            {accounts.data.map((account) => {
              const status = statusCopy(BANK_ACCOUNT_STATUS, account.status);
              const waiting = coolingOff(account.usableFrom, new Date());

              return (
                <li
                  key={account.id}
                  className="flex flex-wrap items-center justify-between gap-3 py-3"
                >
                  <div className="flex min-w-0 flex-col gap-1">
                    <span className="font-medium text-foreground">
                      {account.accountNameResolved ?? account.accountNameProvided}
                    </span>
                    <span className="text-sm text-muted-foreground">
                      {account.bankName} {account.maskedNumber}
                    </span>
                    {status.explanation && account.status !== 'Verified' ? (
                      <span className="text-sm text-muted-foreground">{status.explanation}</span>
                    ) : null}
                    {waiting ? (
                      <span className="text-sm text-muted-foreground">{waiting}</span>
                    ) : null}
                  </div>
                  <div className="flex items-center gap-2">
                    {account.isDefault ? <Badge tone="primary">Default</Badge> : null}
                    <Badge tone={status.tone}>{status.label}</Badge>
                    {!account.isDefault && account.status === 'Verified' ? (
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={makeDefault.isPending}
                        onClick={() => makeDefault.mutate(account.id)}
                      >
                        Make default
                      </Button>
                    ) : null}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function AddBankAccountForm({ onDone }: { onDone: () => void }) {
  const banks = useBanks(true);
  const add = useAddBankAccount();
  const [bankCode, setBankCode] = useState('');
  const [number, setNumber] = useState('');
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [warning, setWarning] = useState<string | null>(null);

  function submit(event: FormEvent) {
    event.preventDefault();

    if (!bankCode) {
      setError('Choose your bank.');
      return;
    }

    if (!isNuban(number)) {
      setError('An account number is 10 digits.');
      return;
    }

    setError(null);
    add.mutate(
      { bankCode, accountNumber: number.trim(), accountName: name.trim() },
      {
        onSuccess: (result) => {
          if (result.nameMatchesBusiness) {
            onDone();
          } else {
            setWarning(
              `Your bank says this account belongs to "${result.accountName}", which does not look like your business name. If that is not right, contact support before withdrawing.`,
            );
          }
        },
        onError: (failure) => setError(describeError(failure).detail),
      },
    );
  }

  if (warning) {
    return (
      <Alert tone="warning" action={<Button onClick={onDone}>Understood</Button>}>
        {warning}
      </Alert>
    );
  }

  return (
    <form
      className="grid gap-4 rounded-lg border border-border p-4 md:grid-cols-3"
      onSubmit={submit}
      noValidate
    >
      <Select
        label="Bank"
        value={bankCode}
        onChange={(event) => setBankCode(event.target.value)}
        disabled={banks.isPending}
      >
        <option value="">{banks.isPending ? 'Loading banks…' : 'Choose your bank'}</option>
        {(banks.data ?? []).map((bank) => (
          <option key={bank.code} value={bank.code}>
            {bank.name}
          </option>
        ))}
      </Select>
      <Input
        label="Account number"
        inputMode="numeric"
        maxLength={10}
        value={number}
        onChange={(event) => setNumber(event.target.value)}
        error={error ?? undefined}
      />
      <Input
        label="Account name (as you know it)"
        value={name}
        onChange={(event) => setName(event.target.value)}
        hint="We use the name your bank gives us."
      />
      <div className="flex gap-2 md:col-span-3">
        <Button type="submit" disabled={add.isPending}>
          {add.isPending ? 'Checking with your bank…' : 'Verify and add'}
        </Button>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

function PayoutHistoryCard() {
  const payouts = usePayouts();

  return (
    <Card>
      <CardHeader>
        <CardTitle>Withdrawals</CardTitle>
      </CardHeader>
      <CardContent>
        {payouts.isPending ? (
          <LoadingState label="Loading withdrawals" />
        ) : payouts.isError ? (
          <ErrorState {...describeError(payouts.error)} onRetry={() => void payouts.refetch()} />
        ) : payouts.data.length === 0 ? (
          <EmptyState title="No withdrawals yet">
            Withdrawals you request appear here with where they are up to.
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Requested</TableHead>
                  <TableHead>Reference</TableHead>
                  <TableHead>To</TableHead>
                  <TableHead className="text-right">Amount</TableHead>
                  <TableHead>Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {payouts.data.map((payout) => {
                  const status = statusCopy(PAYOUT_STATUS, payout.status);
                  const why = payout.rejectionReason ?? payout.failureReason;

                  return (
                    <TableRow key={payout.id}>
                      <TableCell>
                        {new Date(payout.requestedAt).toLocaleDateString('en-NG')}
                      </TableCell>
                      <TableCell className="font-mono text-xs">{payout.reference}</TableCell>
                      <TableCell>
                        {payout.bankName} {payout.maskedNumber}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {formatMoney(payout.amountMinor, payout.currency)}
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-col gap-1">
                          <Badge tone={status.tone}>{status.label}</Badge>
                          {status.explanation ? (
                            <span className="text-xs text-muted-foreground">
                              {status.explanation}
                            </span>
                          ) : null}
                          {why && payout.status !== 'OutcomeUnknown' ? (
                            <span className="text-xs text-muted-foreground">{why}</span>
                          ) : null}
                        </div>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
