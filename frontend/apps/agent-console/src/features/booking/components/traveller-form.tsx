import { useState, type FormEvent } from 'react';
import { Alert, Button, Card, Input, Select } from '@trips/ui';
import {
  departureDate,
  needsPassport,
  slotLabels,
  validateTraveller,
  type Problems,
} from '../booking-rules';
import type { BookingDraft, TravellerDetails } from '../types';

/**
 * #53, step one — who is travelling. Type-appropriate fields: a child's or
 * infant's date of birth, because the fare depends on it; passports only when
 * the route leaves Nigeria; the lead traveller's contact details, because that
 * is who the supplier calls when a flight moves.
 */
export function TravellerStep({
  draft,
  initial,
  confirming,
  onContinue,
}: {
  draft: BookingDraft;
  initial: TravellerDetails[];
  confirming: boolean;
  onContinue: (travellers: TravellerDetails[]) => void;
}) {
  const [travellers, setTravellers] = useState(initial);
  const [problems, setProblems] = useState<Problems[]>([]);
  const passport = needsPassport(draft);
  const travelDate = departureDate(draft);
  const labels = slotLabels(travellers.map((traveller) => traveller.type));

  function update(index: number, patch: Partial<TravellerDetails>) {
    setTravellers((current) =>
      current.map((traveller, i) => (i === index ? { ...traveller, ...patch } : traveller)),
    );
    setProblems((current) =>
      current.map((found, i) => {
        if (i !== index) return found;
        const next = { ...found };
        for (const key of Object.keys(patch)) delete next[key];
        return next;
      }),
    );
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    const found = travellers.map((traveller, index) =>
      validateTraveller(traveller, { isLead: index === 0, needsPassport: passport, travelDate }),
    );
    setProblems(found);
    if (found.every((entry) => Object.keys(entry).length === 0)) onContinue(travellers);
  }

  return (
    <form
      onSubmit={submit}
      noValidate
      aria-label="Traveller details"
      className="flex flex-col gap-4"
    >
      {passport ? (
        <Alert tone="info" title="Passports needed">
          This route leaves Nigeria, so the airline needs each traveller&rsquo;s passport. Names
          must match the passport exactly.
        </Alert>
      ) : null}

      {travellers.map((traveller, index) => (
        <TravellerCard
          key={index}
          label={labels[index] ?? `Traveller ${index + 1}`}
          traveller={traveller}
          problems={problems[index] ?? {}}
          isLead={index === 0}
          passport={passport}
          onChange={(patch) => update(index, patch)}
        />
      ))}

      <div className="flex justify-end">
        <Button type="submit" size="lg" loading={confirming}>
          Confirm the price with the supplier
        </Button>
      </div>
    </form>
  );
}

function TravellerCard({
  label,
  traveller,
  problems,
  isLead,
  passport,
  onChange,
}: {
  label: string;
  traveller: TravellerDetails;
  problems: Problems;
  isLead: boolean;
  passport: boolean;
  onChange: (patch: Partial<TravellerDetails>) => void;
}) {
  const titles = traveller.type === 'ADT' ? ['Mr', 'Mrs', 'Ms', 'Dr'] : ['Master', 'Miss'];
  const birthOptional = traveller.type === 'ADT' && !passport;

  return (
    <Card className="flex flex-col gap-4 p-5">
      <h3 className="text-sm font-semibold text-foreground">
        {label}
        {isLead ? (
          <span className="font-normal text-muted-foreground"> · lead traveller</span>
        ) : null}
      </h3>

      <div className="grid items-start gap-3 sm:grid-cols-2 lg:grid-cols-6">
        <div className="lg:col-span-1">
          <Select
            label="Title"
            value={traveller.title}
            onChange={(event) => onChange({ title: event.target.value })}
          >
            <option value="">—</option>
            {titles.map((title) => (
              <option key={title} value={title}>
                {title}
              </option>
            ))}
          </Select>
        </div>
        <div className="lg:col-span-2">
          <Input
            label="First name"
            autoComplete="given-name"
            hint={passport ? 'As on the passport' : undefined}
            value={traveller.firstName}
            onChange={(event) => onChange({ firstName: event.target.value })}
            error={problems['firstName']}
          />
        </div>
        <div className="lg:col-span-3">
          <Input
            label="Last name"
            autoComplete="family-name"
            value={traveller.lastName}
            onChange={(event) => onChange({ lastName: event.target.value })}
            error={problems['lastName']}
          />
        </div>
        <div className="lg:col-span-2">
          <Select
            label="Gender"
            value={traveller.gender}
            onChange={(event) =>
              onChange({ gender: event.target.value as TravellerDetails['gender'] })
            }
          >
            <option value="">—</option>
            <option value="female">Female</option>
            <option value="male">Male</option>
          </Select>
        </div>
        <div className="lg:col-span-2">
          <Input
            type="date"
            label={birthOptional ? 'Date of birth (optional)' : 'Date of birth'}
            value={traveller.dateOfBirth}
            onChange={(event) => onChange({ dateOfBirth: event.target.value })}
            error={problems['dateOfBirth']}
          />
        </div>

        {isLead ? (
          <>
            <div className="lg:col-span-3">
              <Input
                type="email"
                label="Email"
                autoComplete="email"
                value={traveller.email}
                onChange={(event) => onChange({ email: event.target.value })}
                error={problems['email']}
              />
            </div>
            <div className="lg:col-span-3">
              <Input
                type="tel"
                label="Phone"
                autoComplete="tel"
                value={traveller.phone}
                onChange={(event) => onChange({ phone: event.target.value })}
                error={problems['phone']}
              />
            </div>
          </>
        ) : null}

        {passport ? (
          <>
            <div className="lg:col-span-2">
              <Input
                label="Passport number"
                value={traveller.passportNumber}
                onChange={(event) => onChange({ passportNumber: event.target.value })}
                error={problems['passportNumber']}
              />
            </div>
            <div className="lg:col-span-2">
              <Input
                type="date"
                label="Passport expires"
                hint="Most countries want six months left"
                value={traveller.passportExpiry}
                onChange={(event) => onChange({ passportExpiry: event.target.value })}
                error={problems['passportExpiry']}
              />
            </div>
            <div className="lg:col-span-2">
              <Input
                label="Nationality"
                hint="Two letters, like NG"
                maxLength={2}
                value={traveller.nationality}
                onChange={(event) => onChange({ nationality: event.target.value.toUpperCase() })}
                error={problems['nationality']}
              />
            </div>
          </>
        ) : null}
      </div>
    </Card>
  );
}
