import { describe, expect, it } from 'vitest';
import {
  describeDocumentType,
  extensionOf,
  formatBytes,
  humanise,
  joinWords,
  rejectionFor,
} from '../upload-rules';
import type { UploadLimits } from '../types';

const limits: UploadLimits = {
  maxSizeBytes: 5_000_000,
  allowedContentTypes: ['application/pdf', 'image/jpeg', 'image/png'],
  allowedExtensions: ['.pdf', '.jpg', '.jpeg', '.png'],
  requiredDocumentTypes: ['CertificateOfIncorporation', 'TaxIdentification'],
};

const file = (over: Partial<{ name: string; size: number; type: string }> = {}) => ({
  name: 'cac.pdf',
  size: 1_200_000,
  type: 'application/pdf',
  ...over,
});

describe('what the browser refuses before uploading', () => {
  it('accepts a file inside every limit', () => {
    expect(rejectionFor(file(), limits)).toBeNull();
  });

  it('refuses an empty file, which is usually a failed scan', () => {
    expect(rejectionFor(file({ size: 0 }), limits)).toContain('empty');
  });

  it('refuses a file over the limit, and says how big it was', () => {
    const rejection = rejectionFor(file({ size: 8_000_000 }), limits);

    // The size and the limit both appear, so the agent knows how much to save.
    expect(rejection).toContain('8.0 MB');
    expect(rejection).toContain('5.0 MB');
  });

  it('refuses a format that is not accepted', () => {
    expect(rejectionFor(file({ name: 'cac.docx', type: '' }), limits)).toContain('PDF');
  });

  it('refuses a file with no extension at all', () => {
    expect(rejectionFor(file({ name: 'scan', type: '' }), limits)).toContain('missing a file type');
  });

  it('refuses a renamed file whose content type is wrong', () => {
    // Renaming report.docx to report.pdf passes the extension check; the
    // browser still reports what it really is.
    const rejection = rejectionFor(
      file({
        name: 'report.pdf',
        type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      }),
      limits,
    );

    expect(rejection).not.toBeNull();
  });

  it('accepts a file the browser gives no content type for', () => {
    // Some Android browsers send an empty type. The extension is the only
    // signal left, and the server checks the bytes regardless.
    expect(rejectionFor(file({ name: 'cac.pdf', type: '' }), limits)).toBeNull();
  });

  it('is case-insensitive about the extension', () => {
    expect(rejectionFor(file({ name: 'CAC.PDF' }), limits)).toBeNull();
  });
});

describe('how things are worded', () => {
  it('formats sizes the way a file manager does', () => {
    expect(formatBytes(512)).toBe('512 bytes');
    expect(formatBytes(120_000)).toBe('120 KB');
    expect(formatBytes(5_000_000)).toBe('5.0 MB');
  });

  it('reads a list as a sentence', () => {
    expect(joinWords(['PDF'])).toBe('PDF');
    expect(joinWords(['PDF', 'JPG'])).toBe('PDF and JPG');
    expect(joinWords(['PDF', 'JPG', 'PNG'])).toBe('PDF, JPG and PNG');
  });

  it('names the documents an agent recognises', () => {
    expect(describeDocumentType('CertificateOfIncorporation').label).toBe(
      'Certificate of incorporation',
    );
    expect(describeDocumentType('CertificateOfIncorporation').hint).toContain('CAC');
  });

  it('still renders a document type the console has never heard of', () => {
    // A type added on the server must not ship a console release with it.
    expect(describeDocumentType('MemorandumOfAssociation').label).toBe('Memorandum of association');
  });

  it('reads an extension off a name', () => {
    expect(extensionOf('cac.final.PDF')).toBe('.pdf');
    expect(extensionOf('noextension')).toBeNull();
    expect(extensionOf('.hidden')).toBeNull();
  });

  it('humanises a pascal-case name', () => {
    expect(humanise('TaxIdentification')).toBe('Tax identification');
  });
});
