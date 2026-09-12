import type { Product, PublishProblem } from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the product API lands (issue 161).
 * ============================================================================
 *
 * The stand-in for the server's publish rules (issue 160), so the demo behaves like
 * the real thing. The console itself never decides whether a product can be
 * published: it shows the `publishProblems` the server sends.
 */
export function findPublishProblems(
  product: Omit<Product, 'publishProblems'>,
  today: string,
): PublishProblem[] {
  const problems: PublishProblem[] = [];

  if (!product.title.trim()) {
    problems.push({ field: 'title', message: 'Give it a title.' });
  }
  if (product.basePriceMinor <= 0) {
    problems.push({ field: 'basePriceMinor', message: 'Set the price it sells from.' });
  }
  if (product.media.length === 0) {
    problems.push({ field: 'media', message: 'Add at least one image.' });
  }

  if (product.productType === 'Visa') {
    if (!product.visa) {
      problems.push({ field: 'visa', message: 'Fill in the visa details.' });
    } else if (!product.visa.documents.some((document) => document.isMandatory)) {
      problems.push({
        field: 'visa.documents',
        message: 'List at least one document the applicant must provide.',
      });
    }
  } else if (!product.availableTo) {
    problems.push({ field: 'availableTo', message: 'Say until when it can be booked.' });
  } else if (product.availableTo < today) {
    problems.push({
      field: 'availableTo',
      message: 'The booking window has ended. Move the end date.',
    });
  } else if (product.availableFrom && product.availableFrom > product.availableTo) {
    problems.push({ field: 'availableFrom', message: 'The booking window starts after it ends.' });
  }

  return problems;
}
