import { ArrowLeftRight, Plus, Search } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { Button, Input, SegmentedControl, Select } from '@trips/ui';
import { BUS_TERMINALS, terminalsByCity } from '../reference-data';
import { MAX_BUS_PASSENGERS, todayIn, validateBusCriteria, type Problems } from '../search-rules';
import type { BusSearchCriteria } from '../types';

const TRIP_TYPES = [
  { value: 'one_way', label: 'One way' },
  { value: 'round_trip', label: 'Return' },
] as const;

const GROUPS = terminalsByCity();

/**
 * The bus search form. Terminals rather than cities, because in Lagos alone
 * Jibowu and Ajah are two hours apart in traffic — the traveller needs to know
 * which one they are going to.
 */
export function BusSearchForm({
  initial,
  onSearch,
  searching,
}: {
  initial: BusSearchCriteria;
  onSearch: (criteria: BusSearchCriteria) => void;
  searching: boolean;
}) {
  const [tripType, setTripType] = useState(initial.tripType);
  const [from, setFrom] = useState(initial.departureTerminalId);
  const [to, setTo] = useState(initial.arrivalTerminalId);
  const [date, setDate] = useState(initial.date);
  const [returnDate, setReturnDate] = useState(initial.returnDate ?? '');
  const [passengers, setPassengers] = useState(initial.passengers);
  const [problems, setProblems] = useState<Problems>({});
  const today = todayIn();

  function clear(key: string) {
    setProblems((current) => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    const criteria: BusSearchCriteria = {
      tripType,
      departureTerminalId: from,
      arrivalTerminalId: to,
      date,
      returnDate: tripType === 'round_trip' ? returnDate : null,
      passengers,
    };
    const found = validateBusCriteria(criteria, today, BUS_TERMINALS);
    setProblems(found);
    if (Object.keys(found).length === 0) onSearch(criteria);
  }

  return (
    <form onSubmit={submit} noValidate aria-label="Search buses" className="flex flex-col gap-5">
      <SegmentedControl
        label="Trip type"
        className="self-start"
        options={TRIP_TYPES}
        value={tripType}
        onChange={(next) => {
          setTripType(next);
          setProblems({});
        }}
      />

      <div className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-12">
        <div className="lg:col-span-3">
          <TerminalSelect
            label="From"
            value={from}
            onChange={(id) => {
              setFrom(id);
              clear('from');
              clear('to');
            }}
            error={problems['from']}
          />
        </div>
        <div className="hidden justify-center lg:col-span-1 lg:flex lg:pt-7">
          <Button
            type="button"
            variant="ghost"
            size="icon"
            aria-label="Swap from and to"
            onClick={() => {
              setFrom(to);
              setTo(from);
            }}
          >
            <ArrowLeftRight className="h-4 w-4" aria-hidden="true" />
          </Button>
        </div>
        <div className="lg:col-span-3">
          <TerminalSelect
            label="To"
            value={to}
            onChange={(id) => {
              setTo(id);
              clear('to');
            }}
            error={problems['to']}
          />
        </div>
        <div className="lg:col-span-2">
          <Input
            type="date"
            label="Travel date"
            min={today}
            value={date}
            onChange={(event) => {
              setDate(event.target.value);
              clear('date');
            }}
            error={problems['date']}
          />
        </div>
        <div className="lg:col-span-3">
          {tripType === 'round_trip' ? (
            <Input
              type="date"
              label="Return"
              min={date || today}
              value={returnDate}
              onChange={(event) => {
                setReturnDate(event.target.value);
                clear('returnDate');
              }}
              error={problems['returnDate']}
            />
          ) : (
            <div className="sm:pt-7">
              <Button
                type="button"
                variant="outline"
                fullWidth
                onClick={() => setTripType('round_trip')}
              >
                <Plus className="h-4 w-4" aria-hidden="true" />
                Add a return
              </Button>
            </div>
          )}
        </div>
      </div>

      <div className="grid items-start gap-3 sm:grid-cols-3 lg:grid-cols-12">
        <div className="lg:col-span-2">
          <Select
            label="Passengers"
            value={passengers}
            onChange={(event) => {
              setPassengers(Number(event.target.value));
              clear('passengers');
            }}
            error={problems['passengers']}
          >
            {Array.from({ length: MAX_BUS_PASSENGERS }, (_, index) => index + 1).map((count) => (
              <option key={count} value={count}>
                {count}
              </option>
            ))}
          </Select>
        </div>
        <div className="sm:col-span-2 lg:col-span-10 lg:flex lg:justify-end lg:pt-7">
          <Button type="submit" size="lg" loading={searching} className="w-full lg:w-auto">
            <Search className="h-4 w-4" aria-hidden="true" />
            Search buses
          </Button>
        </div>
      </div>
    </form>
  );
}

function TerminalSelect({
  label,
  value,
  onChange,
  error,
}: {
  label: string;
  value: string;
  onChange: (id: string) => void;
  error: string | undefined;
}) {
  return (
    <Select
      label={label}
      value={value}
      onChange={(event) => onChange(event.target.value)}
      error={error}
    >
      <option value="">Choose a terminal</option>
      {GROUPS.map((group) => (
        <optgroup key={group.city} label={group.city}>
          {group.terminals.map((terminal) => (
            <option key={terminal.id} value={terminal.id}>
              {group.city} — {terminal.name}
            </option>
          ))}
        </optgroup>
      ))}
    </Select>
  );
}
