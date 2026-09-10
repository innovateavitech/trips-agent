import { useState } from 'react';
import { Button } from '@trips/ui';
import type { Currency, StatementFilters } from '../types';
import { statementFileName, toStatementCsv } from '../statement-csv';
import { useWalletApi } from '../wallet-api';

/**
 * Downloads the statement as CSV.
 *
 * It fetches every matching row rather than exporting the page on screen. An
 * agent downloading a statement is reconciling against their own books, and
 * silently handing them 20 of 64 rows would be the kind of error they only
 * catch after the numbers have failed to add up twice.
 */
export function DownloadStatementButton({
  filters,
  currency,
  disabled,
}: {
  filters: StatementFilters;
  currency: Currency;
  disabled?: boolean;
}) {
  const api = useWalletApi();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function download() {
    setBusy(true);
    setFailed(false);

    try {
      const rows = await api.getStatementForExport(filters);
      const blob = new Blob([toStatementCsv(rows, currency)], {
        type: 'text/csv;charset=utf-8',
      });

      // The anchor-and-revoke dance is the standard way to save a generated
      // file. The object URL is released on the next tick because the download
      // has to have started before the browser stops being able to read it.
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = statementFileName(filters);
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <Button variant="outline" size="sm" loading={busy} disabled={disabled} onClick={download}>
        Download CSV
      </Button>
      {failed ? (
        <p role="alert" className="text-xs text-destructive">
          The download failed. Please try again.
        </p>
      ) : null}
    </div>
  );
}
