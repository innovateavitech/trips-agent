import { CheckCircle2, CircleAlert } from 'lucide-react';
import { Card } from '@trips/ui';
import { SECTION_LABELS, sectionOf } from '../catalog-rules';
import type { Product } from '../types';

/**
 * #162 — beside the editor: whether the product is live, and if it is a draft,
 * what it still needs. The list is the server's `publishProblems`, never the
 * console's own guess, and each one links to the part of the editor to fix.
 */
export function PublishChecklist({ product, dirty }: { product: Product | null; dirty: boolean }) {
  if (!product) {
    return (
      <Card className="flex flex-col gap-2 p-5">
        <h2 className="text-sm font-semibold text-foreground">Publishing</h2>
        <p className="text-sm text-muted-foreground">
          Save the draft, and this shows what it still needs before it can go on your storefront.
        </p>
      </Card>
    );
  }

  if (product.status === 'Published') {
    return (
      <Card className="flex flex-col gap-2 p-5">
        <h2 className="flex items-center gap-2 text-sm font-semibold text-foreground">
          <CheckCircle2 aria-hidden="true" className="h-4 w-4 text-success-subtle-foreground" />
          Live on your storefront
        </h2>
        <p className="text-sm text-muted-foreground">
          At <span className="font-medium text-foreground">/{product.slug}</span>. Changes you save go
          live straight away.
        </p>
      </Card>
    );
  }

  if (product.status === 'Archived') {
    return (
      <Card className="flex flex-col gap-2 p-5">
        <h2 className="text-sm font-semibold text-foreground">Archived</h2>
        <p className="text-sm text-muted-foreground">
          Not on your storefront. Restore it as a draft to change it or publish it again.
        </p>
      </Card>
    );
  }

  const problems = product.publishProblems;

  return (
    <Card className="flex flex-col gap-3 p-5">
      {problems.length === 0 ? (
        <>
          <h2 className="flex items-center gap-2 text-sm font-semibold text-foreground">
            <CheckCircle2 aria-hidden="true" className="h-4 w-4 text-success-subtle-foreground" />
            Ready to publish
          </h2>
          <p className="text-sm text-muted-foreground">
            It has everything a customer needs to find and buy it.
          </p>
        </>
      ) : (
        <>
          <h2 className="flex items-center gap-2 text-sm font-semibold text-foreground">
            <CircleAlert aria-hidden="true" className="h-4 w-4 text-warning-subtle-foreground" />
            {problems.length === 1
              ? 'One thing before it can be published'
              : `${problems.length} things before it can be published`}
          </h2>
          <ul aria-label="Before publishing" className="flex flex-col gap-2 text-sm">
            {problems.map((problem) => {
              const section = sectionOf(problem);

              return (
                <li key={`${problem.field}:${problem.message}`} className="flex flex-col">
                  <a
                    href={`#section-${section}`}
                    className="font-medium text-primary underline-offset-4 hover:underline"
                  >
                    {SECTION_LABELS[section]}
                  </a>
                  <span className="text-muted-foreground">{problem.message}</span>
                </li>
              );
            })}
          </ul>
        </>
      )}
      {dirty ? (
        <p className="border-t border-border pt-3 text-xs text-muted-foreground">
          Checked against the last save. Save to check your changes, and before publishing.
        </p>
      ) : null}
    </Card>
  );
}
