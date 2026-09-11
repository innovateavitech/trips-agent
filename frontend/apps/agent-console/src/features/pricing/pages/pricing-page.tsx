import { useState, type ReactNode } from 'react';
import { Alert, Button, Card, ErrorState, Input, LoadingState, Select } from '@trips/ui';
import { ApiError, describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { PriceExplainer } from '../components/price-explainer';
import { RuleForm } from '../components/rule-form';
import { RuleHistory } from '../components/rule-history';
import {
  describeRule,
  endedRules,
  isInForce,
  parseProductId,
  PRODUCT_TYPES,
  productRulesInForce,
  productTypeLabel,
  ruleInForce,
  scopeLabel,
  type MarkupRule,
  type ProductType,
  type RuleSlot,
} from '../pricing-rules';
import { useMarkupRules, usePricingSettings, useRetireRule } from '../pricing-queries';

/**
 * ============================================================================
 *  FRD §2.6 — pricing rules: what the agency adds on top of the net rate.
 * ============================================================================
 *
 * Three layers, most specific first: a rule for one product, a rule for a
 * product type, and a default for everything. The explainer at the top shows
 * which one wins for any sample price, because that is the question agents ask.
 */
export function PricingPage() {
  const settings = usePricingSettings();
  const rules = useMarkupRules();

  if (settings.isPending || rules.isPending) {
    return <LoadingState size="page" label="Loading your pricing rules" />;
  }

  if (settings.isError || rules.isError) {
    const error = settings.error ?? rules.error;
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
              }
        }
        retrying={settings.isFetching || rules.isFetching}
      />
    );
  }

  return <PricingRules currency={settings.data.currency} rules={rules.data} />;
}

function PricingRules({ currency, rules }: { currency: string; rules: MarkupRule[] }) {
  // Which row's form is open: 'global', 'type:Flight', 'product:<id>' or 'new-product'.
  const [editing, setEditing] = useState<string | null>(null);
  const now = new Date();

  const global = ruleInForce(rules, { scope: 'Global' }, now);
  const productRules = productRulesInForce(rules, now);
  const supplierRules = rules.filter((rule) => rule.scope === 'Supplier' && isInForce(rule, now));

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

      <PriceExplainer currency={currency} />

      <RuleSection
        title="Default markup"
        description="Applies to everything you sell, unless a more specific rule below says otherwise."
      >
        <RuleRow
          label="Everything"
          rule={global}
          emptyText="No default. Travellers pay the net price unless another rule applies."
          currency={currency}
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
              rule={ruleInForce(rules, slot, now)}
              emptyText="Uses your default."
              currency={currency}
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
            <NewProductRuleForm currency={currency} onDone={() => setEditing(null)} />
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
              slot={null}
              isEditing={false}
              onEdit={() => undefined}
              onDone={() => undefined}
            />
          ))}
        </RuleSection>
      ) : null}

      <RuleHistory rules={endedRules(rules, now)} currency={currency} />
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
        <RuleForm currency={currency} slot={slot} replacing={rule} onDone={onDone} />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3 p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-0.5">
          <span className="truncate text-sm font-medium text-foreground">{label}</span>
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
function NewProductRuleForm({ currency, onDone }: { currency: string; onDone: () => void }) {
  const [productType, setProductType] = useState<ProductType>('Tour');
  const [productIdText, setProductIdText] = useState('');
  const productId = parseProductId(productIdText);

  return (
    <RuleForm
      currency={currency}
      slot={productId.ok ? { scope: 'Product', productType, productId: productId.value } : null}
      slotProblem={productId.ok ? undefined : productId.error}
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
