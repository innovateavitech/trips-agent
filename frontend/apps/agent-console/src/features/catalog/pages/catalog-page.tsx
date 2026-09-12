import { Compass, FileText, Package } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Input,
  LoadingState,
  SegmentedControl,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { useProducts } from '../catalog-api';
import {
  PRODUCT_TYPES,
  STATUS_FILTERS,
  canEditCatalog,
  describePlace,
  filterProducts,
  statusCounts,
} from '../catalog-rules';
import { ProductStatusBadge } from '../components/editor-parts';
import {
  NO_PRODUCT_FILTERS,
  type ProductFilters,
  type ProductSummary,
  type ProductType,
} from '../types';

const updatedFormat = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  timeZone: 'Africa/Lagos',
});

/** issue 162 — every tour, package and visa the agency sells, and where each one stands. */
export function CatalogPage() {
  const user = useCurrentUser();
  const products = useProducts();
  const [filters, setFilters] = useState<ProductFilters>(NO_PRODUCT_FILTERS);
  const canEdit = canEditCatalog(user.roles);

  const all = useMemo(() => products.data ?? [], [products.data]);
  const counts = useMemo(() => statusCounts(all), [all]);
  const visible = useMemo(() => filterProducts(all, filters), [all, filters]);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Catalog"
        description="The tours, packages and visas you sell under your own brand."
        actions={canEdit ? <NewProductLinks /> : null}
      />

      <Card className="flex flex-col gap-4 p-4">
        <SegmentedControl
          label="Filter by status"
          // No counts until the list has loaded: "0" would claim there are no products.
          options={STATUS_FILTERS.map((option) =>
            products.data ? { ...option, count: counts[option.value] } : option,
          )}
          value={filters.status}
          onChange={(status) => setFilters({ ...filters, status })}
        />
        <div className="grid items-start gap-3 sm:grid-cols-3">
          <div className="sm:col-span-2">
            <Input
              type="search"
              label="Search"
              placeholder="Title or destination"
              value={filters.query}
              onChange={(event) => setFilters({ ...filters, query: event.target.value })}
            />
          </div>
          <Select
            label="Type"
            value={filters.type}
            onChange={(event) =>
              setFilters({ ...filters, type: event.target.value as ProductType | 'all' })
            }
          >
            <option value="all">All types</option>
            {PRODUCT_TYPES.map((type) => (
              <option key={type.value} value={type.value}>
                {type.label}
              </option>
            ))}
          </Select>
        </div>
      </Card>

      {products.isPending ? <LoadingState label="Loading your catalog" /> : null}

      {products.isError ? (
        <ErrorState
          {...describeError(products.error)}
          onRetry={() => void products.refetch()}
          retrying={products.isFetching}
        />
      ) : null}

      {products.data && all.length === 0 ? (
        <EmptyState
          icon={<Compass aria-hidden="true" className="h-5 w-5" />}
          title="Nothing in your catalog yet"
          action={canEdit ? <NewProductLinks /> : null}
        >
          Build a tour, a package or a visa listing, then publish it to your storefront.
        </EmptyState>
      ) : null}

      {products.data && all.length > 0 && visible.length === 0 ? (
        <EmptyState
          title="No products match"
          action={
            <Button variant="outline" size="sm" onClick={() => setFilters(NO_PRODUCT_FILTERS)}>
              Clear filters
            </Button>
          }
        >
          Try another status or type, or a shorter search.
        </EmptyState>
      ) : null}

      {visible.length > 0 ? <ProductsTable products={visible} /> : null}
    </div>
  );
}

function NewProductLinks() {
  return (
    <div className="flex flex-wrap gap-2">
      {PRODUCT_TYPES.map((type, index) => (
        <Link
          key={type.value}
          to={`/catalog/new/${type.singular}`}
          className={buttonVariants({ size: 'sm', variant: index === 0 ? undefined : 'outline' })}
        >
          New {type.singular}
        </Link>
      ))}
    </div>
  );
}

function ProductsTable({ products }: { products: ProductSummary[] }) {
  return (
    <Card className="overflow-hidden p-0">
      <div className="overflow-x-auto">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Product</TableHead>
              <TableHead>Status</TableHead>
              <TableHead className="text-right">From</TableHead>
              <TableHead>Updated</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {products.map((product) => (
              <TableRow key={product.id}>
                <TableCell>
                  <div className="flex items-center gap-3">
                    <Thumbnail url={product.heroPreviewUrl} type={product.productType} />
                    <div className="min-w-0">
                      <Link
                        to={`/catalog/${product.id}`}
                        className="font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                      >
                        {product.title || 'Untitled'}
                      </Link>
                      <p className="text-xs text-muted-foreground">
                        {product.productType} · {describePlace(product)}
                      </p>
                    </div>
                  </div>
                </TableCell>
                <TableCell>
                  <ProductStatusBadge status={product.status} />
                  {product.status === 'Draft' && product.publishProblemCount > 0 ? (
                    <p className="mt-1 text-xs text-muted-foreground">
                      {product.publishProblemCount} to fix before publishing
                    </p>
                  ) : null}
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {product.basePriceMinor > 0
                    ? formatMoneyShort(product.basePriceMinor, product.currency)
                    : '—'}
                </TableCell>
                <TableCell className="text-muted-foreground">
                  {updatedFormat.format(new Date(product.updatedAt))}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </Card>
  );
}

function Thumbnail({ url, type }: { url: string | null; type: ProductType }) {
  if (url) return <img src={url} alt="" className="h-10 w-14 shrink-0 rounded object-cover" />;

  const Icon = type === 'Visa' ? FileText : type === 'Package' ? Package : Compass;
  return (
    <span className="flex h-10 w-14 shrink-0 items-center justify-center rounded bg-muted text-muted-foreground">
      <Icon aria-hidden="true" className="h-4 w-4" />
    </span>
  );
}
