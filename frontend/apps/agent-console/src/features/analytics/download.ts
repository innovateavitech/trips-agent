/**
 * Saves a file the server produced.
 *
 * An object URL and a synthetic click, which is the only way a browser lets a
 * page save bytes it already holds. The URL is revoked straight afterwards:
 * an object URL keeps the whole blob alive until the document is unloaded, and
 * an agent exporting twenty reports in a session would otherwise be holding
 * every one of them in memory.
 *
 * Not a plain link to the endpoint, because the download needs the session's
 * bearer token — and because the server logs the export when it serves it, so
 * the request has to carry who is asking.
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);

  try {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.rel = 'noopener';

    document.body.append(link);
    link.click();
    link.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}
