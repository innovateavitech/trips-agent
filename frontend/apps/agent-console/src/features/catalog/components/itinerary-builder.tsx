import { Plus } from 'lucide-react';
import { Button, Input, Textarea } from '@trips/ui';
import { MEALS, emptyDay, moveItem, removeAt, replaceAt, type DayDraft } from '../catalog-rules';
import { RowControls } from './editor-parts';

/**
 * #162 — the day-by-day itinerary. Days are numbered by position, so moving one
 * renumbers the rest; the numbers sent to the server are always 1, 2, 3…
 */
export function ItineraryBuilder({
  days,
  onChange,
}: {
  days: DayDraft[];
  onChange: (days: DayDraft[]) => void;
}) {
  return (
    <div className="flex flex-col gap-4">
      {days.length === 0 ? (
        <p className="text-sm text-muted-foreground">No days yet. Add the first one.</p>
      ) : (
        <ol aria-label="Itinerary days" className="flex flex-col gap-4">
          {days.map((day, index) => {
            const change = (patch: Partial<DayDraft>) => onChange(replaceAt(days, index, patch));

            return (
              <li key={day.key} className="flex flex-col gap-3 rounded-lg border border-border p-4">
                <div className="flex items-center justify-between gap-2">
                  <h3 className="text-sm font-semibold text-foreground">Day {index + 1}</h3>
                  <RowControls
                    label={`day ${index + 1}`}
                    index={index}
                    count={days.length}
                    onMove={(direction) => onChange(moveItem(days, index, direction))}
                    onRemove={() => onChange(removeAt(days, index))}
                  />
                </div>
                <Input
                  label="Title"
                  placeholder="Arrive in Zanzibar"
                  value={day.title}
                  onChange={(event) => change({ title: event.target.value })}
                />
                <Textarea
                  label="What happens"
                  rows={3}
                  value={day.description}
                  onChange={(event) => change({ description: event.target.value })}
                />
                <div className="grid items-start gap-3 sm:grid-cols-2">
                  <fieldset className="flex flex-col gap-2">
                    <legend className="mb-2 text-sm font-medium text-foreground">Meals included</legend>
                    <div className="flex flex-wrap gap-4">
                      {MEALS.map((meal) => (
                        <label key={meal} className="flex items-center gap-2 text-sm text-foreground">
                          <input
                            type="checkbox"
                            className="h-4 w-4 accent-primary"
                            checked={day.meals.includes(meal)}
                            onChange={(event) =>
                              change({
                                meals: event.target.checked
                                  ? [...day.meals, meal]
                                  : day.meals.filter((included) => included !== meal),
                              })
                            }
                          />
                          {meal}
                        </label>
                      ))}
                    </div>
                  </fieldset>
                  <Input
                    label="Where they sleep"
                    hint="Leave empty on the last day"
                    value={day.accommodation}
                    onChange={(event) => change({ accommodation: event.target.value })}
                  />
                </div>
              </li>
            );
          })}
        </ol>
      )}
      <div>
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...days, emptyDay()])}>
          <Plus className="h-4 w-4" aria-hidden="true" />
          Add day {days.length + 1}
        </Button>
      </div>
    </div>
  );
}
