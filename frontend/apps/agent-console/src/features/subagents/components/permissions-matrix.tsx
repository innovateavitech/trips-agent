import { useState } from 'react';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  ErrorState,
  Input,
  LoadingState,
} from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useChangePermission, useSubAgentPermissions } from '../subagents-api';
import type { SubAgentPermission } from '../types';

/**
 * What a sub-agent may do, as a list of switches grouped the way the role
 * editor groups them.
 *
 * Every switch is a *subtraction*: it can take away what the sub-agent's roles
 * already give, and it can never add anything. That is the rule in the API too,
 * so this screen cannot express a permission the server would refuse.
 *
 * `margin.view` is the one that hides net rates and markup, and it is a row
 * here like any other rather than a separate switch — so the two can never
 * disagree about what a sub-agent sees.
 */
export function PermissionsMatrix({
  subAgencyId,
  readOnly,
}: {
  subAgencyId: string;
  readOnly: boolean;
}) {
  const permissions = useSubAgentPermissions(subAgencyId);
  const change = useChangePermission(subAgencyId);
  const [denying, setDenying] = useState<string | null>(null);
  const [reason, setReason] = useState('');

  if (permissions.isPending) {
    return <LoadingState label="Loading permissions" />;
  }

  if (permissions.isError) {
    const problem = describeError(permissions.error);

    return (
      <ErrorState
        title={problem.title}
        detail={problem.detail}
        onRetry={() => void permissions.refetch()}
        retrying={permissions.isFetching}
      />
    );
  }

  const byCategory = new Map<string, SubAgentPermission[]>();

  for (const permission of permissions.data) {
    byCategory.set(permission.category, [
      ...(byCategory.get(permission.category) ?? []),
      permission,
    ]);
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>What they may do</CardTitle>
        <CardDescription>
          Their own roles decide what they can do; here you take things away. You can never give
          them something you do not have yourself.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-6">
        {change.isError ? (
          <Alert tone="destructive" title={describeError(change.error).title}>
            {describeError(change.error).detail}
          </Alert>
        ) : null}

        {[...byCategory].map(([category, rows]) => (
          <section key={category} className="flex flex-col gap-3">
            <h3 className="text-sm font-semibold text-foreground">{category}</h3>

            <ul className="flex flex-col divide-y divide-border">
              {rows.map((permission) => (
                <li key={permission.code} className="flex flex-wrap items-start gap-3 py-3">
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-foreground">{permission.description}</p>
                    <p className="font-mono text-xs text-muted-foreground">{permission.code}</p>
                    {permission.isDenied && permission.reason ? (
                      <p className="mt-1 text-xs text-muted-foreground">
                        Taken away: {permission.reason}
                      </p>
                    ) : null}
                  </div>

                  {readOnly ? null : permission.isDenied ? (
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={change.isPending}
                      onClick={() =>
                        change.mutate({ code: permission.code, deny: false, reason: '' })
                      }
                    >
                      Give back
                    </Button>
                  ) : denying === permission.code ? (
                    <form
                      className="flex w-full flex-wrap items-center gap-2 sm:w-auto"
                      onSubmit={(event) => {
                        event.preventDefault();

                        change.mutate(
                          { code: permission.code, deny: true, reason: reason.trim() },
                          {
                            onSuccess: () => {
                              setDenying(null);
                              setReason('');
                            },
                          },
                        );
                      }}
                    >
                      <Input
                        label={`Why ${permission.description.toLowerCase()} is being taken away`}
                        value={reason}
                        onChange={(event) => setReason(event.target.value)}
                        placeholder="Why are you taking this away?"
                        required
                        autoFocus
                        className="w-full sm:w-64"
                      />
                      <Button type="submit" size="sm" disabled={change.isPending}>
                        Take it away
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => {
                          setDenying(null);
                          setReason('');
                        }}
                      >
                        Cancel
                      </Button>
                    </form>
                  ) : (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        setDenying(permission.code);
                        setReason('');
                      }}
                    >
                      Take away
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          </section>
        ))}
      </CardContent>
    </Card>
  );
}
