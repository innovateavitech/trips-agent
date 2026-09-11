import { useState } from 'react';
import {
  Alert,
  Badge,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  ErrorState,
  Input,
  Select,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { describeError } from '../../../api/errors';
import {
  describeRule,
  formatPercent,
  parseAmount,
  parseProductId,
  PRODUCT_TYPES,
  scopeLabel,
  type ProductType,
} from '../pricing-rules';
import { usePricePreview, type PreviewInput } from '../pricing-queries';
import { useDebouncedValue } from '../use-debounced-value';

/**
 * ============================================================================
 *  "Which rule wins?" — the part of this screen that prevents support tickets.
 * ============================================================================
 *
 * Precedence is what agents most often get wrong: they set 10% on flights, see
 * 15% on a fare, and forget the single-product rule they wrote in March. So the
 * answer comes from the server's real pricing (the same code that prices a
 * search), never from arithmetic in the browser that could drift from it.
 */
export function PriceExplainer({ currency }: { currency: string }) {
  const [productType, setProductType] = useState<ProductType>('Flight');
  const [netText, setNetText] = useState('100000');
  const [productIdText, setProductIdText] = useState('');

  const debouncedNet = useDebouncedValue(netText, 300);
  const debouncedProductId = useDebouncedValue(productIdText, 300);

  const net = parseAmount(debouncedNet);
  const productId = debouncedProductId.trim() === '' ? null : parseProductId(debouncedProductId);

  const input: PreviewInput | null =
    net.ok && (productId === null || productId.ok)
      ? {
          productType,
          productId: productId?.ok ? productId.value : null,
          netAmountMinor: net.value,
        }
      : null;

  const preview = usePricePreview(input);
  const result = preview.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Which rule wins?</CardTitle>
        <CardDescription>
          Type a net price and see what a traveller would pay, and which of your rules decides it.
          The most specific rule always wins: a single product, then a product type, then your
          default.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-5">
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
          <Input
            label="Sample net price"
            inputMode="decimal"
            value={netText}
            onChange={(event) => setNetText(event.target.value)}
            error={netText === debouncedNet && !net.ok ? net.error : undefined}
            hint={`What you pay, in ${currency}.`}
          />
          <Input
            label="Product ID (optional)"
            value={productIdText}
            onChange={(event) => setProductIdText(event.target.value)}
            error={
              productIdText === debouncedProductId && productId !== null && !productId.ok
                ? productId.error
                : undefined
            }
            hint="To test a single-product rule."
          />
        </div>

        {preview.isError ? (
          <ErrorState
            title={describeError(preview.error).title}
            detail={describeError(preview.error).detail}
            onRetry={() => void preview.refetch()}
            retrying={preview.isFetching}
          />
        ) : null}

        {result ? (
          <div
            className="flex flex-col gap-4 rounded-lg border border-border bg-muted p-4"
            aria-live="polite"
            aria-busy={preview.isFetching}
          >
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <span className="text-sm text-muted-foreground">Traveller pays</span>
              <span className="text-2xl font-semibold tabular-nums text-foreground">
                {formatMoney(result.grossAmountMinor, currency)}
              </span>
            </div>

            {result.winningRule ? (
              <div className="flex flex-col gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm text-muted-foreground">Decided by</span>
                  <Badge tone="primary">{scopeLabel(result.winningRule)}</Badge>
                  {result.winningRule.inherited ? (
                    <Badge tone="info">From your principal agency</Badge>
                  ) : null}
                </div>
                <p className="text-sm text-foreground">
                  {describeRule(result.winningRule, currency)}
                </p>
              </div>
            ) : (
              <Alert tone="warning" title="No rule applies">
                The traveller would pay the net price with nothing added. Set a default markup below
                so every sale earns something.
              </Alert>
            )}

            <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1.5 text-sm">
              <dt className="text-muted-foreground">Net price</dt>
              <dd className="text-right tabular-nums text-foreground">
                {formatMoney(result.netAmountMinor, currency)}
              </dd>
              <dt className="text-muted-foreground">Your markup</dt>
              <dd className="text-right tabular-nums text-foreground">
                {formatMoney(result.markupAmountMinor, currency)}
              </dd>
              <dt className="text-muted-foreground">
                VAT at {formatPercent(result.vatRateBasisPoints)}% on your markup
              </dt>
              <dd className="text-right tabular-nums text-foreground">
                {formatMoney(result.taxAmountMinor, currency)}
              </dd>
              {result.platformFeeBasisPoints > 0 ? (
                <>
                  <dt className="text-muted-foreground">
                    Platform fee at {formatPercent(result.platformFeeBasisPoints)}%, from your
                    markup
                  </dt>
                  <dd className="text-right tabular-nums text-foreground">
                    −{formatMoney(result.platformFeeMinor, currency)}
                  </dd>
                </>
              ) : null}
              <dt className="border-t border-border pt-1.5 font-medium text-foreground">
                You keep
              </dt>
              <dd className="border-t border-border pt-1.5 text-right font-medium tabular-nums text-foreground">
                {formatMoney(result.agentMarginMinor, currency)}
              </dd>
            </dl>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}
