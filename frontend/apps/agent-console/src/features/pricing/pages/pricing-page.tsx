import { useState, type ReactNode } from 'react';
import { Alert, Badge, Button, Card, ErrorState, Input, LoadingState, Select } from '@trips/ui';
import { ApiError, describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { InheritedRules } from '../components/inherited-rules';
import { PriceExplainer } from '../components/price-explainer';
import { RuleForm } from '../components/rule-form';
import { RuleHistory } from '../components/rule-history';
import {
  describeRule,
  endedRules,
  isInForce,
  parseProductId,
  pricingCopy,
  PRODUCT_TYPES,
  productRulesInForce,
  productTypeLabel,
  ruleInForce,
  scopeLabel,
  type InheritedRule,
  type MarkupRule,
  type ProductType,
  type RuleSlot,
} from '../pricing-rules';
import {
  useInheritedRules,
  useMarkupRules,
  usePricingSettings,
  useRetireRule,
  type PricingSettings,
} from '../pricing-queries';

/**
 * ============================================================================
 *  FRD §2.6 — pricing rules: what the agency adds on top of the net rate.
 * ============================================================================
 *
 * Three layers, most specific first: a rule for one product, a rule for a
 * product type, and a default for everything. The explainer at the top shows
 * which one wins for any sample price, because that is the question agents ask.
 *
 * A sub-agent also sees its principal's rules, read-only, and is told the one
 * thing that differs for it: its own rules — any of them — come first.
 */
export function PricingPage() {
  const settings = usePricingSettings();
  const rules = useMarkupRules();
  const inherited = useInheritedRules();

  if (settings.isPending || rules.isPending || inherited.isPending) {
    return <LoadingState size="page" label="Loading your pricing rules" />;
  }

  if (settings.isError || rules.isError || inherited.isError) {
    const error = settings.error ?? rules.error ?? inherited.error;
    const problem = describeError(error);
    const forbidden = error instanceof ApiError && error.status === 403;

    return (
      <ErrorState
        title={
          forbidden ? 'Pricing rules are only open to people who can see margins' : problem.title
        }
        detail={
          forbidden
            ? 'Your markup gives away what you pay, so it is kept to the people your agency trusts with it. Ask an owner or manager for access.'
            : problem.detail
        }
        onRetry={
          forbidden
            ? undefined
            : () => {
                void settings.refetch();
                void rules.refetch();
                void inherited.refetch();
              }
        }
        retrying={settings.isFetching || rules.isFetching || inherited.isFetching}
      />
    );
  }

  return <PricingRules settings={settings.data} rules={rules.data} inherited={inherited.data} />;
}

function PricingRules({
  settings,
  rules,
  inherited,
}: {
  settings: PricingSettings;
  rules: MarkupRule[];
  inherited: InheritedRule[];
}) {
  const { currency, hasPrincipal, hasSubAgents } = settings;

  // Which row's form is open: 'global', 'type:Flight', 'product:<id>' or 'new-product'.
  const [editing, setEditing] = useState<string | null>(null);

  // Whether a rule is in force is the server's call (each rule carries its
  // status), never this browser's clock — see `isInForce`.
  const global = ruleInForce(rules, { scope: 'Global' });
  const productRules = productRulesInForce(rules);
  const supplierRules = rules.filter((rule) => rule.scope === 'Supplier' && isInForce(rule));
  const copy = pricingCopy({ hasPrincipal, hasOwnDefault: global !== undefined });

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Pricing rules"
        description="Decide what you add on top of the net rate. Travellers only ever see the final price."
      />

      <Alert tone="info" title="Changes apply from your next search">
        Bookings and quotes you have already made keep the price they were given. A rule is never
        edited in place: saving a change retires the old rule and starts a new one, and the old one
        stays listed under Earlier rules.
      </Alert>

      <PriceExplainer currency={currency} precedence={copy.precedence} />

      <RuleSection title="Default markup" description={copy.defaultDescription}>
        <RuleRow
          label="Everything"
          rule={global}
          emptyText={copy.noDefault}
          currency={currency}
          hasSubAgents={hasSubAgents}
          slot={{ scope: 'Global' }}
          isEditing={editing === 'global'}
          onEdit={() => setEditing('global')}
          onDone={() => setEditing(null)}
        />
      </RuleSection>

      <RuleSection
        title="By product type"
        description="Overrides your default for every product of one type."
      >
        {PRODUCT_TYPES.map((type) => {
          const slot: RuleSlot = { scope: 'ProductType', productType: type.value };
          const key = `type:${type.value}`;

          return (
            <RuleRow
              key={type.value}
              label={type.label}
              rule={ruleInForce(rules, slot)}
              emptyText={copy.noTypeRule}
              currency={currency}
              hasSubAgents={hasSubAgents}
              slot={slot}
              isEditing={editing === key}
              onEdit={() => setEditing(key)}
              onDone={() => setEditing(null)}
            />
          );
        })}
      </RuleSection>

      <RuleSection
        title="Single products"
        description="Overrides everything else for one tour, visa or group departure."
      >
        {productRules.map((rule) => {
          const key = `product:${rule.productId}`;

          return (
            <RuleRow
              key={rule.id}
              label={`${productTypeLabel(rule.productType)} · ${rule.productId ?? ''}`}
              rule={rule}
              emptyText=""
              currency={currency}
              hasSubAgents={hasSubAgents}
              slot={{
                scope: 'Product',
                productType: rule.productType as ProductType,
                productId: rule.productId ?? '',
              }}
              isEditing={editing === key}
              onEdit={() => setEditing(key)}
              onDone={() => setEditing(null)}
            />
          );
        })}

        {editing === 'new-product' ? (
          <div className="p-5">
            <NewProductRuleForm
              currency={currency}
              hasSubAgents={hasSubAgents}
              onDone={() => setEditing(null)}
            />
          </div>
        ) : (
          <div className="flex flex-wrap items-center justify-between gap-3 p-5">
            <p className="text-sm text-muted-foreground">
              {productRules.length === 0
                ? 'No single-product rules. Every product follows its type or your default.'
                : 'Add another product with its own markup.'}
            </p>
            <Button variant="outline" size="sm" onClick={() => setEditing('new-product')}>
              Add a product rule
            </Button>
          </div>
        )}
      </RuleSection>

      {supplierRules.length > 0 ? (
        <RuleSection
          title="By supplier"
          description="Rules for everything bought from one supplier. These rank below product-type rules."
        >
          {supplierRules.map((rule) => (
            <RuleRow
              key={rule.id}
              label={rule.supplierCode ?? scopeLabel(rule)}
              rule={rule}
              emptyText=""
              currency={currency}
              hasSubAgents={hasSubAgents}
              slot={null}
              isEditing={false}
              onEdit={() => undefined}
              onDone={() => undefined}
            />
          ))}
        </RuleSection>
      ) : null}

      {hasPrincipal ? <InheritedRules rules={inherited} currency={currency} /> : null}

      <RuleHistory rules={endedRules(rules)} currency={currency} />
    </div>
  );
}

function RuleSection({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <h2 className="text-lg font-semibold tracking-tight text-foreground">{title}</h2>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>
      <Card className="divide-y divide-border">{children}</Card>
    </section>
  );
}

interface RuleRowProps {
  label: string;
  rule: MarkupRule | undefined;
  emptyText: string;
  currency: string;
  /** A principal with sub-agents sees, on each rule, whether they inherit it. */
  hasSubAgents: boolean;
  /** Null for rules this screen shows but does not edit (supplier rules). */
  slot: RuleSlot | null;
  isEditing: boolean;
  onEdit: () => void;
  onDone: () => void;
}

function RuleRow({
  label,
  rule,
  emptyText,
  currency,
  hasSubAgents,
  slot,
  isEditing,
  onEdit,
  onDone,
}: RuleRowProps) {
  const retire = useRetireRule();

  if (isEditing && slot !== null) {
    return (
      <div className="flex flex-col gap-3 p-5">
        <p className="text-sm font-medium text-foreground">{label}</p>
        <RuleForm
          currency={currency}
          slot={slot}
          replacing={rule}
          hasSubAgents={hasSubAgents}
          onDone={onDone}
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3 p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-0.5">
          <div className="flex min-w-0 flex-wrap items-center gap-2">
            <span className="truncate text-sm font-medium text-foreground">{label}</span>
            {rule && hasSubAgents ? (
              <Badge tone={rule.appliesToSubAgents ? 'info' : 'neutral'}>
                {rule.appliesToSubAgents ? 'Sub-agents inherit this' : 'Kept from sub-agents'}
              </Badge>
            ) : null}
          </div>
          <span className="text-sm text-muted-foreground">
            {rule ? describeRule(rule, currency) : emptyText}
          </span>
        </div>

        <div className="flex flex-wrap gap-2">
          {slot !== null ? (
            <Button variant="outline" size="sm" onClick={onEdit}>
              {rule ? 'Change' : 'Set a markup'}
            </Button>
          ) : null}
          {rule ? (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => retire.mutate(rule.id)}
              loading={retire.isPending}
            >
              Remove
            </Button>
          ) : null}
        </div>
      </div>

      {retire.isError ? (
        <Alert tone="destructive" title={describeError(retire.error).title}>
          {describeError(retire.error).detail}
        </Alert>
      ) : null}
    </div>
  );
}

/**
 * A rule for one product. Products are chosen by ID for now: the catalog they
 * live in (tours, visas, group departures) has no picker yet, so the ID is
 * copied from the product's own page.
 */
function NewProductRuleForm({
  currency,
  hasSubAgents,
  onDone,
}: {
  currency: string;
  hasSubAgents: boolean;
  onDone: () => void;
}) {
  const [productType, setProductType] = useState<ProductType>('Tour');
  const [productIdText, setProductIdText] = useState('');
  const productId = parseProductId(productIdText);

  return (
    <RuleForm
      currency={currency}
      slot={productId.ok ? { scope: 'Product', productType, productId: productId.value } : null}
      slotProblem={productId.ok ? undefined : productId.error}
      hasSubAgents={hasSubAgents}
      onDone={onDone}
    >
      <div className="grid gap-4 sm:grid-cols-3">
        <Select
          label="Product type"
          value={productType}
          onChange={(event) => setProductType(event.target.value as ProductType)}
        >
          {PRODUCT_TYPES.map((type) => (
            <option key={type.value} value={type.value}>
              {type.label}
            </option>
          ))}
        </Select>
        <div className="sm:col-span-2">
          <Input
            label="Product ID"
            value={productIdText}
            onChange={(event) => setProductIdText(event.target.value)}
            hint="Copy it from the product's page."
          />
        </div>
      </div>
    </RuleForm>
  );
}
