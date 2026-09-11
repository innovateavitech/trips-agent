import { Card } from '@trips/ui';
import { describeRule, inheritedRuleLabel, type InheritedRule } from '../pricing-rules';

/**
 * The principal's rules a sub-agent inherits, read-only.
 *
 * Without this section a sub-agent's screen listed only its own rules, and an
 * inherited rule appeared nowhere except as the preview's answer — the rule
 * nobody could see, which is what the preview exists to prevent. It also says
 * the one thing about inheritance that surprises people: any rule of the
 * sub-agent's own, even a default, outranks every rule listed here.
 */
export function InheritedRules({ rules, currency }: { rules: InheritedRule[]; currency: string }) {
  return (
    <section className="flex flex-col gap-3" aria-labelledby="inherited-rules-heading">
      <div className="flex flex-col gap-1">
        <h2
          id="inherited-rules-heading"
          className="text-lg font-semibold tracking-tight text-foreground"
        >
          From your principal agency
        </h2>
        <p className="text-sm text-muted-foreground">
          Set and changed by your principal agency. Each applies only to sales that none of your own
          rules covers: any rule of yours, even your default, comes before all of these.
        </p>
      </div>

      <Card className="divide-y divide-border">
        {rules.length === 0 ? (
          <p className="p-5 text-sm text-muted-foreground">
            Your principal agency has no rules that apply to you.
          </p>
        ) : (
          rules.map((rule) => (
            <div key={rule.id} className="flex min-w-0 flex-col gap-0.5 p-5">
              <span className="truncate text-sm font-medium text-foreground">
                {inheritedRuleLabel(rule)}
              </span>
              <span className="text-sm text-muted-foreground">{describeRule(rule, currency)}</span>
            </div>
          ))
        )}
      </Card>
    </section>
  );
}
