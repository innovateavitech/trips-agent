import { History } from 'lucide-react';
import {
  Badge,
  Card,
  EmptyState,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { describeRule, scopeLabel, type MarkupRule } from '../pricing-rules';

const when = (iso: string) =>
  new Date(iso).toLocaleString('en-NG', { dateStyle: 'medium', timeStyle: 'short' });

/**
 * Rules that no longer apply. Shown, not hidden, because they are the proof of
 * the promise at the top of the screen: nothing is rewritten. Every quote and
 * booking names the rule that priced it, and that rule is still here, saying
 * exactly what it said.
 */
export function RuleHistory({ rules, currency }: { rules: MarkupRule[]; currency: string }) {
  return (
    <section className="flex flex-col gap-3" aria-labelledby="rule-history-heading">
      <div className="flex flex-col gap-1">
        <h2
          id="rule-history-heading"
          className="text-lg font-semibold tracking-tight text-foreground"
        >
          Earlier rules
        </h2>
        <p className="text-sm text-muted-foreground">
          Kept for good, so every past price can still be explained.
        </p>
      </div>

      <Card className="overflow-hidden">
        {rules.length === 0 ? (
          <EmptyState
            title="No earlier rules yet"
            icon={<History className="size-5" aria-hidden="true" />}
          >
            When you change or remove a rule, the old one moves here instead of disappearing.
          </EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Applied to</TableHead>
                  <TableHead>Markup</TableHead>
                  <TableHead>From</TableHead>
                  <TableHead>Until</TableHead>
                  <TableHead>
                    <span className="sr-only">Why it ended</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rules.map((rule) => (
                  <TableRow key={rule.id}>
                    <TableCell className="font-medium text-foreground">
                      {scopeLabel(rule)}
                    </TableCell>
                    <TableCell>{describeRule(rule, currency)}</TableCell>
                    <TableCell className="whitespace-nowrap">{when(rule.effectiveFrom)}</TableCell>
                    <TableCell className="whitespace-nowrap">
                      {rule.effectiveTo ? when(rule.effectiveTo) : ''}
                    </TableCell>
                    <TableCell>
                      <Badge tone="neutral">{rule.supersededById ? 'Replaced' : 'Removed'}</Badge>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </Card>
    </section>
  );
}
