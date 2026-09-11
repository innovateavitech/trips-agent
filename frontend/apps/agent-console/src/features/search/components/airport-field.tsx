import { useId, useMemo, useState, type KeyboardEvent } from 'react';
import { cx } from '../class-names';
import { AIRPORTS, findAirport } from '../reference-data';
import { matchAirports } from '../search-rules';

export interface AirportFieldProps {
  label: string;
  /** The chosen IATA code, or `''`. */
  value: string;
  onChange: (code: string) => void;
  error?: string | undefined;
}

/**
 * An airport picker that finds by code, city or airport name — "LOS", "lagos"
 * and "murtala" all find Lagos.
 *
 * The ARIA combobox pattern: focus stays in the text box while the arrow keys
 * move a highlight through the list, so a screen reader hears each airport and
 * Enter picks it. Agents who know their codes can type "ABV" and Tab on — an
 * exact code counts as a choice.
 */
export function AirportField({ label, value, onChange, error }: AirportFieldProps) {
  const inputId = useId();
  const listId = `${inputId}-options`;
  const errorId = `${inputId}-error`;
  const selected = findAirport(value);

  // Null while the agent is not typing: the box then shows the chosen airport.
  const [query, setQuery] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);

  const matches = useMemo(() => matchAirports(query ?? '', AIRPORTS), [query]);
  const activeMatch = open ? matches[active] : undefined;

  function choose(code: string) {
    onChange(code);
    setQuery(null);
    setOpen(false);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        setOpen(true);
        setActive((index) => Math.min(index + 1, Math.max(matches.length - 1, 0)));
        break;
      case 'ArrowUp':
        event.preventDefault();
        setActive((index) => Math.max(index - 1, 0));
        break;
      case 'Enter':
        if (open && activeMatch) {
          event.preventDefault();
          choose(activeMatch.code);
        }
        break;
      case 'Escape':
        if (open) {
          event.preventDefault();
          setOpen(false);
          setQuery(null);
        }
        break;
    }
  }

  function onBlur() {
    const exact = query ? findAirport(query.trim().toUpperCase()) : undefined;
    if (exact) onChange(exact.code);
    setQuery(null);
    setOpen(false);
  }

  const text = query ?? (selected ? `${selected.city} (${selected.code})` : '');

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={inputId} className="text-sm font-medium text-foreground">
        {label}
      </label>

      <div className="relative">
        <input
          id={inputId}
          type="text"
          role="combobox"
          autoComplete="off"
          spellCheck={false}
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={activeMatch ? `${listId}-${activeMatch.code}` : undefined}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          placeholder="City or airport"
          value={text}
          onChange={(event) => {
            setQuery(event.target.value);
            setActive(0);
            setOpen(true);
          }}
          onFocus={(event) => {
            event.target.select();
            setOpen(true);
          }}
          onKeyDown={onKeyDown}
          onBlur={onBlur}
          className={cx(
            'h-10 w-full rounded-md border bg-background px-3 py-2 text-sm placeholder:text-muted-foreground',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
            error ? 'border-destructive' : 'border-input',
          )}
        />

        {open ? (
          <div className="absolute left-0 right-0 top-full z-30 mt-1 overflow-hidden rounded-md border border-border bg-popover text-popover-foreground shadow-lg">
            {matches.length === 0 ? (
              <p className="px-3 py-3 text-sm text-muted-foreground">
                No airport matches “{query}”.
              </p>
            ) : (
              <ul
                id={listId}
                role="listbox"
                aria-label={label}
                className="max-h-72 overflow-auto p-1"
              >
                {matches.map((airport, index) => (
                  <li
                    key={airport.code}
                    id={`${listId}-${airport.code}`}
                    role="option"
                    aria-selected={index === active}
                    // mousedown, not click: it fires before the input's blur, which would
                    // otherwise close the list out from under the pointer.
                    onMouseDown={(event) => {
                      event.preventDefault();
                      choose(airport.code);
                    }}
                    onMouseEnter={() => setActive(index)}
                    className={cx(
                      'flex cursor-pointer items-center gap-3 rounded-sm px-2 py-2 text-sm',
                      index === active && 'bg-accent text-accent-foreground',
                    )}
                  >
                    <span className="w-9 shrink-0 text-xs font-semibold tracking-wide text-primary">
                      {airport.code}
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium">{airport.city}</span>
                      <span className="block truncate text-xs text-muted-foreground">
                        {airport.name}
                      </span>
                    </span>
                    <span className="text-xs text-muted-foreground">{airport.country}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        ) : null}
      </div>

      {error ? (
        <p id={errorId} role="alert" className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}
