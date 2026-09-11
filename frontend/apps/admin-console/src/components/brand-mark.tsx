/**
 * The console's name in the corner. Trips' own brand is right here — this is our staff tool, not
 * anything a traveller sees (CLAUDE.md rule 4 is about the storefront, invoices and emails).
 */
export function BrandMark() {
  return (
    <span className="flex items-center gap-2.5">
      <span
        aria-hidden="true"
        className="flex h-8 w-8 items-center justify-center rounded-md bg-primary text-sm font-semibold text-primary-foreground"
      >
        T
      </span>
      <span className="flex flex-col leading-tight">
        <span className="text-sm font-semibold text-foreground">Trips</span>
        <span className="text-xs text-muted-foreground">Back office</span>
      </span>
    </span>
  );
}
