/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the orders API lands.
 * ============================================================================
 *
 * A one-page PDF written out by hand, so the stand-in's download links open a
 * real — if plain — document rather than nothing. The real ones are drawn by
 * the Worker in the agency's own branding (#46).
 */
export function samplePdfDataUrl(lines: readonly string[]): string {
  // PDF strings escape their own delimiters; anything outside printable ASCII becomes '?'.
  const text = (line: string) =>
    line.replace(/[\\()]/g, (char) => `\\${char}`).replace(/[^\x20-\x7E]/g, '?');

  const stream = ['BT', '/F1 14 Tf', '72 770 Td', '20 TL']
    .concat(lines.map((line) => `(${text(line)}) Tj T*`))
    .concat('ET')
    .join('\n');

  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`,
  ];

  let pdf = '%PDF-1.4\n';
  const offsets: number[] = [];

  objects.forEach((body, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${body}\nendobj\n`;
  });

  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  pdf += offsets.map((offset) => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;

  return `data:application/pdf;base64,${btoa(pdf)}`;
}
