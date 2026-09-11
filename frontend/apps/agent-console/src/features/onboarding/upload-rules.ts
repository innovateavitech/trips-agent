import type { UploadLimits } from './types';

/**
 * ============================================================================
 *  What the browser checks before a file is sent.
 * ============================================================================
 *
 * The server enforces all of this again — this is for the person, not for
 * safety. On Nigerian mobile data, uploading eight megabytes and *then* being
 * told the limit is five is a minute of someone's life and their airtime.
 *
 * Pure functions, so the rules are tested without a browser or a server.
 */

/** Why a file was refused, written to be shown as-is. Null when it is fine. */
export function rejectionFor(
  file: { name: string; size: number; type: string },
  limits: UploadLimits,
): string | null {
  if (file.size === 0) {
    return 'That file is empty. Check it opens on your device, then try again.';
  }

  if (file.size > limits.maxSizeBytes) {
    return `That file is ${formatBytes(file.size)}. The limit is ${formatBytes(limits.maxSizeBytes)} — try a lower-quality scan or a photo instead of a PDF.`;
  }

  // Extension and content type are checked separately: a browser reports no
  // content type at all for some files, and a renamed file reports the wrong
  // extension. Either alone is a weak signal; together they catch the honest
  // mistakes, which is all this is for.
  const extension = extensionOf(file.name);

  if (extension === null || !limits.allowedExtensions.includes(extension)) {
    return `${describeAccepted(limits)} — that one is ${extension === null ? 'missing a file type' : extension}.`;
  }

  if (file.type.length > 0 && !limits.allowedContentTypes.includes(file.type)) {
    return `${describeAccepted(limits)} — that one is ${file.type}.`;
  }

  return null;
}

/** `.pdf`, lowercase, or null when the name has no extension. */
export function extensionOf(fileName: string): string | null {
  const dot = fileName.lastIndexOf('.');
  return dot > 0 && dot < fileName.length - 1 ? fileName.slice(dot).toLowerCase() : null;
}

export function describeAccepted(limits: UploadLimits): string {
  const list = limits.allowedExtensions.map((e) => e.replace('.', '').toUpperCase());
  return `Accepted formats are ${joinWords(list)}`;
}

/** "1.4 MB". Decimal units, because that is what a file manager shows. */
export function formatBytes(bytes: number): string {
  if (bytes < 1000) return `${bytes} bytes`;
  if (bytes < 1_000_000) return `${(bytes / 1000).toFixed(0)} KB`;
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}

/** "PDF, JPG and PNG" */
export function joinWords(words: string[]): string {
  if (words.length === 0) return '';
  if (words.length === 1) return words[0] as string;
  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1] as string}`;
}

/**
 * A document type as a person reads it.
 *
 * The API sends PascalCase names from the domain. An unknown one still renders
 * legibly rather than shouting `CertificateOfIncorporation` at an agent, so a
 * document type added on the server does not need a console release.
 */
export function describeDocumentType(documentType: string): { label: string; hint: string } {
  const known: Record<string, { label: string; hint: string }> = {
    CertificateOfIncorporation: {
      label: 'Certificate of incorporation',
      hint: 'Your CAC certificate, showing the registered company name and RC number.',
    },
    TaxIdentification: {
      label: 'Tax identification',
      hint: 'Your TIN certificate, or a FIRS document showing the number.',
    },
    ProofOfAddress: {
      label: 'Proof of address',
      hint: 'A utility bill or bank statement from the last three months, showing the business address.',
    },
    DirectorIdentification: {
      label: "Director's identification",
      hint: "A director's NIN slip, international passport or driver's licence.",
    },
    BusinessRegistration: {
      label: 'Business registration',
      hint: 'The registration document for the business.',
    },
  };

  return known[documentType] ?? { label: humanise(documentType), hint: '' };
}

/** `CertificateOfIncorporation` → `Certificate of incorporation`. */
export function humanise(pascalCase: string): string {
  const spaced = pascalCase.replace(/([a-z])([A-Z])/g, '$1 $2').trim();
  return spaced.length === 0
    ? pascalCase
    : spaced[0]!.toUpperCase() + spaced.slice(1).toLowerCase();
}
