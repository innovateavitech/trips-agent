import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Button, Card, ErrorState, Input, LoadingState } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { LogoUpload } from '../components/logo-upload';
import { useSaveSiteTheme, useSiteTheme } from '../storefront-queries';

/**
 * ============================================================================
 *  Issue 58 — the agency's logo and colours.
 * ============================================================================
 *
 * These are the agency's branding, not this one site's: the same logo and colour go on their
 * invoices, their vouchers and the emails their travellers receive. Changing it here changes it
 * everywhere, and the screen says so, because an agent who expects a website-only change would be
 * surprised by next month's invoice.
 *
 * The colour is refused by the API when white text on it would be hard to read. That is not fussiness
 * — every button on their site puts white text on this colour, and a pale one makes their own shop
 * unreadable to their own customers.
 */
export function DesignPage() {
  const theme = useSiteTheme();
  const save = useSaveSiteTheme();
  const [saved, setSaved] = useState(false);
  const [form, setForm] = useState<{ primaryColor: string; secondaryColor: string } | null>(null);
  const [logoAssetId, setLogoAssetId] = useState<string | null | undefined>(undefined);

  if (theme.isPending) {
    return <LoadingState size="page" label="Loading your design" />;
  }

  if (theme.isError) {
    return (
      <ErrorState title={describeError(theme.error).title} onRetry={() => void theme.refetch()} />
    );
  }

  const current = form ?? {
    primaryColor: theme.data.primaryColor,
    secondaryColor: theme.data.secondaryColor ?? '',
  };

  const chosenLogo = logoAssetId === undefined ? (theme.data.logoAssetId ?? null) : logoAssetId;

  function set(key: 'primaryColor' | 'secondaryColor', value: string) {
    setSaved(false);
    setForm({ ...current, [key]: value });
  }

  async function submit(event: FormEvent) {
    event.preventDefault();

    await save.mutateAsync({
      logoAssetId: chosenLogo,
      primaryColor: current.primaryColor.trim(),
      secondaryColor: current.secondaryColor.trim() === '' ? null : current.secondaryColor.trim(),
    });

    setSaved(true);
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Your design"
        description={
          <>
            Your logo and colours, used on your website and on everything your travellers receive
            from you —{' '}
            <Link to="/website" className="text-primary underline-offset-4 hover:underline">
              back to your website
            </Link>
            .
          </>
        }
      />

      <Card className="p-6">
        <h2 className="text-lg font-semibold text-foreground">Logo</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Shown in your site&apos;s header and on your invoices. Leave it empty to show your
          business name as text instead.
        </p>

        <LogoUpload
          currentPreviewUrl={theme.data.logoPreviewUrl ?? null}
          selectedAssetId={chosenLogo}
          onSelected={(assetId) => {
            setSaved(false);
            setLogoAssetId(assetId);
          }}
        />
      </Card>

      <Card className="p-6">
        <h2 className="text-lg font-semibold text-foreground">Colours</h2>

        <form onSubmit={submit} className="mt-4 grid gap-5 md:grid-cols-2">
          <ColourField
            label="Main colour"
            hint="Buttons, links and your header. White text sits on it, so a dark colour reads best."
            value={current.primaryColor}
            error={fieldError(save.error, 'PrimaryColor')}
            onChange={(value) => set('primaryColor', value)}
          />

          <ColourField
            label="Second colour"
            hint="Optional. Used for quieter highlights."
            value={current.secondaryColor}
            error={fieldError(save.error, 'SecondaryColor')}
            onChange={(value) => set('secondaryColor', value)}
          />

          {save.isError ? (
            <div className="md:col-span-2">
              <Alert tone="destructive">
                {describeError(save.error).title}
                {describeError(save.error).detail ? (
                  <p className="mt-1">{describeError(save.error).detail}</p>
                ) : null}
              </Alert>
            </div>
          ) : null}

          <div className="flex items-center gap-3 md:col-span-2">
            <Button type="submit" disabled={save.isPending}>
              {save.isPending ? 'Saving…' : 'Save design'}
            </Button>
            {saved && !save.isPending ? (
              <span className="text-sm text-muted-foreground">
                Saved. Publish your website to show it to travellers.
              </span>
            ) : null}
          </div>
        </form>
      </Card>
    </div>
  );
}

/**
 * A colour as a hex value, with the browser's own picker beside it.
 *
 * Both controls edit the same value, because agents arrive with a brand colour written down as a hex
 * code as often as they arrive wanting to point at one.
 */
function ColourField({
  label,
  hint,
  value,
  error,
  onChange,
}: {
  label: string;
  hint: string;
  value: string;
  error?: string;
  onChange: (value: string) => void;
}) {
  const isHex = /^#[0-9a-fA-F]{6}$/.test(value);

  return (
    <div className="flex items-end gap-3">
      <div className="flex-1">
        <Input
          label={label}
          hint={hint}
          value={value}
          placeholder="Your brand colour, as a hex code"
          onChange={(event) => onChange(event.target.value)}
          error={error}
        />
      </div>

      {/*
        Empty while the typed value is not a colour yet, so the browser shows its own default rather
        than us naming one — the only file allowed to hold a colour value is the token stylesheet
        (CLAUDE.md rule 7).
      */}
      <input
        type="color"
        aria-label={`${label} picker`}
        value={isHex ? value : ''}
        onChange={(event) => onChange(event.target.value.toUpperCase())}
        className="mb-6 h-10 w-12 shrink-0 cursor-pointer rounded-md border border-input bg-background p-1"
      />
    </div>
  );
}
