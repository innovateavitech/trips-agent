import { ArrowLeftRight, Plus, Search, X } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { Button, Input, SegmentedControl, Select } from '@trips/ui';
import {
  CABIN_LABELS,
  legField,
  MAX_MULTI_CITY_LEGS,
  MAX_SEATED_PASSENGERS,
  todayIn,
  validateFlightCriteria,
  type Problems,
} from '../search-rules';
import type { CabinClass, FlightLeg, FlightSearchCriteria, Passengers, TripType } from '../types';
import { AirportField } from './airport-field';

const TRIP_TYPES = [
  { value: 'round_trip', label: 'Return' },
  { value: 'one_way', label: 'One way' },
  { value: 'multi_city', label: 'Multi-city' },
] as const;

const CABINS = Object.keys(CABIN_LABELS) as CabinClass[];

const EMPTY_LEG: FlightLeg = { origin: '', destination: '', date: '' };

function range(from: number, to: number): number[] {
  return Array.from({ length: to - from + 1 }, (_, index) => from + index);
}

/**
 * The flight search form. It checks the search before sending it — a past date
 * or more infants than adults would only come back from the airline as an
 * error twenty seconds later — and says what is wrong beside the field.
 */
export function FlightSearchForm({
  initial,
  onSearch,
  searching,
}: {
  initial: FlightSearchCriteria;
  onSearch: (criteria: FlightSearchCriteria) => void;
  searching: boolean;
}) {
  const [tripType, setTripType] = useState<TripType>(initial.tripType);
  const [legs, setLegs] = useState<FlightLeg[]>(
    initial.tripType === 'multi_city' ? initial.legs : [initial.legs[0] ?? EMPTY_LEG],
  );
  const [returnDate, setReturnDate] = useState(
    initial.tripType === 'round_trip' ? (initial.legs[1]?.date ?? '') : '',
  );
  const [passengers, setPassengers] = useState<Passengers>(initial.passengers);
  const [cabin, setCabin] = useState<CabinClass>(initial.cabin);
  const [problems, setProblems] = useState<Problems>({});
  const today = todayIn();

  function clear(...keys: string[]) {
    setProblems((current) => {
      const next = { ...current };
      for (const key of keys) delete next[key];
      return next;
    });
  }

  function updateLeg(index: number, patch: Partial<FlightLeg>) {
    setLegs((current) => current.map((leg, i) => (i === index ? { ...leg, ...patch } : leg)));
    clear(...Object.keys(patch).map((field) => legField(index, field as keyof FlightLeg)));
  }

  function changeTripType(next: TripType) {
    setTripType(next);
    setProblems({});
    if (next === 'multi_city' && legs.length < 2) {
      // The next flight usually leaves from where the last one landed.
      const first = legs[0] ?? EMPTY_LEG;
      setLegs([first, { origin: first.destination, destination: '', date: '' }]);
    }
  }

  function swap() {
    const first = legs[0] ?? EMPTY_LEG;
    updateLeg(0, { origin: first.destination, destination: first.origin });
  }

  function addLeg() {
    setLegs((current) => {
      const last = current[current.length - 1] ?? EMPTY_LEG;
      return [...current, { origin: last.destination, destination: '', date: '' }];
    });
  }

  function removeLeg(index: number) {
    setLegs((current) => current.filter((_, i) => i !== index));
    setProblems({});
  }

  function criteria(): FlightSearchCriteria {
    const first = legs[0] ?? EMPTY_LEG;
    const chosen =
      tripType === 'one_way'
        ? [first]
        : tripType === 'round_trip'
          ? [first, { origin: first.destination, destination: first.origin, date: returnDate }]
          : legs;
    return { tripType, legs: chosen, passengers, cabin };
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    const next = criteria();
    const found = validateFlightCriteria(next, today);
    setProblems(found);
    if (Object.keys(found).length === 0) onSearch(next);
  }

  const first = legs[0] ?? EMPTY_LEG;

  return (
    <form onSubmit={submit} noValidate aria-label="Search flights" className="flex flex-col gap-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <SegmentedControl
          label="Trip type"
          options={TRIP_TYPES}
          value={tripType}
          onChange={changeTripType}
        />
        <div className="w-full sm:w-52">
          <Select
            label="Cabin"
            labelHidden
            value={cabin}
            onChange={(event) => setCabin(event.target.value as CabinClass)}
          >
            {CABINS.map((option) => (
              <option key={option} value={option}>
                {CABIN_LABELS[option]}
              </option>
            ))}
          </Select>
        </div>
      </div>

      {tripType === 'multi_city' ? (
        <div className="flex flex-col gap-3">
          <ol className="flex flex-col gap-4">
            {legs.map((leg, index) => (
              <li key={index} className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-12">
                <div className="lg:col-span-4">
                  <AirportField
                    label={`Flight ${index + 1} from`}
                    value={leg.origin}
                    onChange={(origin) => updateLeg(index, { origin })}
                    error={problems[legField(index, 'origin')]}
                  />
                </div>
                <div className="lg:col-span-4">
                  <AirportField
                    label="To"
                    value={leg.destination}
                    onChange={(destination) => updateLeg(index, { destination })}
                    error={problems[legField(index, 'destination')]}
                  />
                </div>
                <div className="lg:col-span-3">
                  <Input
                    type="date"
                    label="Date"
                    min={legs[index - 1]?.date || today}
                    value={leg.date}
                    onChange={(event) => updateLeg(index, { date: event.target.value })}
                    error={problems[legField(index, 'date')]}
                  />
                </div>
                <div className="flex lg:col-span-1 lg:pt-7">
                  {legs.length > 2 ? (
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      aria-label={`Remove flight ${index + 1}`}
                      onClick={() => removeLeg(index)}
                    >
                      <X className="h-4 w-4" aria-hidden="true" />
                    </Button>
                  ) : null}
                </div>
              </li>
            ))}
          </ol>
          {legs.length < MAX_MULTI_CITY_LEGS ? (
            <div>
              <Button type="button" variant="outline" size="sm" onClick={addLeg}>
                <Plus className="h-4 w-4" aria-hidden="true" />
                Add another flight
              </Button>
            </div>
          ) : null}
        </div>
      ) : (
        <div className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-12">
          <div className="lg:col-span-3">
            <AirportField
              label="From"
              value={first.origin}
              onChange={(origin) => updateLeg(0, { origin })}
              error={problems[legField(0, 'origin')]}
            />
          </div>
          <div className="hidden justify-center lg:col-span-1 lg:flex lg:pt-7">
            <Button
              type="button"
              variant="ghost"
              size="icon"
              aria-label="Swap from and to"
              onClick={swap}
            >
              <ArrowLeftRight className="h-4 w-4" aria-hidden="true" />
            </Button>
          </div>
          <div className="lg:col-span-3">
            <AirportField
              label="To"
              value={first.destination}
              onChange={(destination) => updateLeg(0, { destination })}
              error={problems[legField(0, 'destination')]}
            />
          </div>
          <div className="lg:col-span-2">
            <Input
              type="date"
              label="Depart"
              min={today}
              value={first.date}
              onChange={(event) => updateLeg(0, { date: event.target.value })}
              error={problems[legField(0, 'date')]}
            />
          </div>
          <div className="lg:col-span-3">
            {tripType === 'round_trip' ? (
              <Input
                type="date"
                label="Return"
                min={first.date || today}
                value={returnDate}
                onChange={(event) => {
                  setReturnDate(event.target.value);
                  clear(legField(1, 'date'));
                }}
                error={problems[legField(1, 'date')]}
              />
            ) : (
              <div className="sm:pt-7">
                <Button
                  type="button"
                  variant="outline"
                  fullWidth
                  onClick={() => changeTripType('round_trip')}
                >
                  <Plus className="h-4 w-4" aria-hidden="true" />
                  Add a return
                </Button>
              </div>
            )}
          </div>
        </div>
      )}

      <div className="grid items-start gap-3 sm:grid-cols-3 lg:grid-cols-12">
        <div className="lg:col-span-2">
          <Select
            label="Adults"
            hint="12 and over"
            value={passengers.adults}
            onChange={(event) => {
              setPassengers({ ...passengers, adults: Number(event.target.value) });
              clear('passengers');
            }}
          >
            {range(1, MAX_SEATED_PASSENGERS).map((count) => (
              <option key={count} value={count}>
                {count}
              </option>
            ))}
          </Select>
        </div>
        <div className="lg:col-span-2">
          <Select
            label="Children"
            hint="2 to 11"
            value={passengers.children}
            onChange={(event) => {
              setPassengers({ ...passengers, children: Number(event.target.value) });
              clear('passengers');
            }}
          >
            {range(0, MAX_SEATED_PASSENGERS - 1).map((count) => (
              <option key={count} value={count}>
                {count}
              </option>
            ))}
          </Select>
        </div>
        <div className="lg:col-span-2">
          <Select
            label="Infants"
            hint="Under 2, on a lap"
            value={passengers.infants}
            onChange={(event) => {
              setPassengers({ ...passengers, infants: Number(event.target.value) });
              clear('passengers');
            }}
          >
            {range(0, 4).map((count) => (
              <option key={count} value={count}>
                {count}
              </option>
            ))}
          </Select>
        </div>
        <div className="sm:col-span-3 lg:col-span-6 lg:flex lg:justify-end lg:pt-7">
          <Button type="submit" size="lg" loading={searching} className="w-full lg:w-auto">
            <Search className="h-4 w-4" aria-hidden="true" />
            Search flights
          </Button>
        </div>
      </div>

      {problems['passengers'] ? (
        <p role="alert" className="text-sm text-destructive">
          {problems['passengers']}
        </p>
      ) : null}
    </form>
  );
}
