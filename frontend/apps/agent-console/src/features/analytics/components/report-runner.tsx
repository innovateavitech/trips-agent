import { useState } from 'react';
import { Alert, Button, Card, Input, Select } from '@trips/ui';
import { daysCovered, willBeQueued } from '../analytics-rules';
import type { AnalyticsWindow, ReportDefinition } from '../types';

/**
 * Pick a report, pick a window, run it.
 *
 * The one thing this screen must get right is telling the agent, before they
 * press the button, whether they are going to get a file or an email. The rule
 * is the server's (`ReportScopeRules`); `willBeQueued` restates it here purely
 * so the button can say what will happen.
 */
export function ReportRunner({
  definitions,
  window,
  onWindowChange,
  onRun,
  isRunning,
  error,
}: {
  definitions: ReportDefinition[];
  window: AnalyticsWindow;
  onWindowChange: (window: AnalyticsWindow) => void;
  onRun: (definitionCode: string) => void;
  isRunning: boolean;
  error: string | null;
}) {
  const [code, setCode] = useState(definitions[0]?.code ?? '');

  const selected = definitions.find((definition) => definition.code === code) ?? definitions[0];
  const queued = selected ? willBeQueued(selected, window) : false;
  const days = daysCovered(window);

  if (definitions.length === 0) {
    return (
      <Alert tone="info" title="No reports are available to this account">
        Running a report needs the report.view permission. Ask an owner or a manager at your agency.
      </Alert>
    );
  }

  return (
    <Card className="flex flex-col gap-4 p-5">
      <div className="grid gap-4 md:grid-cols-[2fr_1fr_1fr]">
        <Select label="Report" value={code} onChange={(event) => setCode(event.target.value)}>
          {definitions.map((definition) => (
            <option key={definition.code} value={definition.code}>
              {definition.name}
            </option>
          ))}
        </Select>

        <Input
          label="From"
          type="date"
          value={window.from}
          max={window.to}
          onChange={(event) => onWindowChange({ ...window, from: event.target.value })}
        />

        <Input
          label="To"
          type="date"
          value={window.to}
          min={window.from}
          onChange={(event) => onWindowChange({ ...window, to: event.target.value })}
        />
      </div>

      {selected ? <p className="text-sm text-muted-foreground">{selected.description}</p> : null}

      {error ? (
        <Alert tone="destructive" title="That report could not be run">
          {error}
        </Alert>
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {days === 0
            ? 'Choose a window that ends on or after it starts.'
            : queued
              ? `${days} days — this one is produced in the background, and we will email you when it is ready.`
              : `${days} days — this one downloads straight away.`}
        </p>

        <Button onClick={() => onRun(code)} disabled={isRunning || days === 0}>
          {isRunning ? 'Working…' : queued ? 'Queue report' : 'Download CSV'}
        </Button>
      </div>
    </Card>
  );
}
