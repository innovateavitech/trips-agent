/**
 * Renders an agent's text as text, never as markup.
 *
 * Descriptions, intros and page copy are typed by an agent into the console and shown on their own
 * public domain to their own customers. If any of it were ever treated as HTML, an agent — or
 * anything they pasted in without reading — could put a script in front of their travellers. React
 * escapes what it renders, so the safety here is in what this component does *not* do: there is no
 * `dangerouslySetInnerHTML` on the storefront, and there must never be one.
 *
 * Blank lines separate paragraphs, which is the only formatting the console's plain-text fields
 * offer.
 */
export function PlainText({ text, className }: { text: string; className?: string }) {
  const paragraphs = text
    .split(/\n\s*\n/)
    .map((paragraph) => paragraph.trim())
    .filter((paragraph) => paragraph.length > 0);

  if (paragraphs.length === 0) {
    return null;
  }

  return (
    <div className={className}>
      {paragraphs.map((paragraph, index) => (
        // Paragraphs have no id of their own and never reorder — their position is their identity.
        <p key={index} className="whitespace-pre-line text-base leading-relaxed text-foreground">
          {paragraph}
        </p>
      ))}
    </div>
  );
}
