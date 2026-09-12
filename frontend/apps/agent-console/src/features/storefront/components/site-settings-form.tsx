import { useState, type FormEvent } from 'react';
import { Alert, Button, Card, Input, Textarea } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import { useSaveSiteSettings, type SiteSettings } from '../storefront-queries';

/**
 * The site's name, what search engines show, and how travellers reach the agency.
 *
 * The contact details are the agency's branding rather than this one site's: invoices, vouchers and
 * emails read the same record. They are edited here because this is where an agent looks for them,
 * and the form says so rather than letting it be a surprise.
 */
export function SiteSettingsForm({ settings }: { settings: SiteSettings }) {
  const save = useSaveSiteSettings();
  const [saved, setSaved] = useState(false);

  const [form, setForm] = useState({
    name: settings.name,
    seoTitle: settings.seoTitle ?? '',
    seoDescription: settings.seoDescription ?? '',
    flightSearchEnabled: settings.flightSearchEnabled,
    contactEmail: settings.contactEmail ?? '',
    contactPhone: settings.contactPhone ?? '',
    whatsAppNumber: settings.whatsAppNumber ?? '',
    contactAddress: settings.contactAddress ?? '',
  });

  function set<K extends keyof typeof form>(key: K, value: (typeof form)[K]) {
    setSaved(false);
    setForm((current) => ({ ...current, [key]: value }));
  }

  async function submit(event: FormEvent) {
    event.preventDefault();

    await save.mutateAsync({
      name: form.name.trim(),
      seoTitle: blankToNull(form.seoTitle),
      seoDescription: blankToNull(form.seoDescription),
      flightSearchEnabled: form.flightSearchEnabled,
      contactEmail: blankToNull(form.contactEmail),
      contactPhone: blankToNull(form.contactPhone),
      whatsAppNumber: blankToNull(form.whatsAppNumber),
      contactAddress: blankToNull(form.contactAddress),
      socialLinks: settings.socialLinks,
    });

    setSaved(true);
  }

  return (
    <Card className="p-6">
      <h2 className="text-lg font-semibold text-foreground">Your details</h2>
      <p className="mt-1 text-sm text-muted-foreground">
        Changes here go live the next time you publish.
      </p>

      <form onSubmit={submit} className="mt-5 grid gap-5 md:grid-cols-2">
        <Input
          label="Business name"
          hint="Shown in your header, your footer and every page title."
          value={form.name}
          onChange={(event) => set('name', event.target.value)}
          required
          error={fieldError(save.error, 'Name')}
        />

        <Input
          label="Search engine title"
          hint="Leave this empty to use your business name."
          value={form.seoTitle}
          onChange={(event) => set('seoTitle', event.target.value)}
          error={fieldError(save.error, 'SeoTitle')}
        />

        <div className="md:col-span-2">
          <Textarea
            label="Search engine description"
            hint="The sentence under your link in search results. Around 150 characters reads best."
            value={form.seoDescription}
            onChange={(event) => set('seoDescription', event.target.value)}
            rows={2}
            error={fieldError(save.error, 'SeoDescription')}
          />
        </div>

        <Input
          label="Email travellers can write to"
          type="email"
          value={form.contactEmail}
          onChange={(event) => set('contactEmail', event.target.value)}
          error={fieldError(save.error, 'ContactEmail')}
        />

        <Input
          label="Phone number"
          value={form.contactPhone}
          onChange={(event) => set('contactPhone', event.target.value)}
          error={fieldError(save.error, 'ContactPhone')}
        />

        <Input
          label="WhatsApp number"
          hint="Shown as a link that opens a chat."
          value={form.whatsAppNumber}
          onChange={(event) => set('whatsAppNumber', event.target.value)}
          error={fieldError(save.error, 'WhatsAppNumber')}
        />

        <Textarea
          label="Address"
          value={form.contactAddress}
          onChange={(event) => set('contactAddress', event.target.value)}
          rows={2}
          error={fieldError(save.error, 'ContactAddress')}
        />

        <div className="md:col-span-2">
          <label className="flex items-start gap-3">
            <input
              type="checkbox"
              checked={form.flightSearchEnabled}
              onChange={(event) => set('flightSearchEnabled', event.target.checked)}
              className="mt-1 h-4 w-4 rounded border-input text-primary focus-visible:ring-2 focus-visible:ring-ring"
            />
            <span className="text-sm">
              <span className="font-medium text-foreground">Sell flights on my website</span>
              <span className="block text-muted-foreground">
                Travellers search and book flights themselves. You can publish your website with
                this switched on even if you have not listed a tour or a visa yet.
              </span>
            </span>
          </label>
        </div>

        {save.isError ? (
          <div className="md:col-span-2">
            <Alert tone="destructive">{describeError(save.error).title}</Alert>
          </div>
        ) : null}

        <div className="flex items-center gap-3 md:col-span-2">
          <Button type="submit" disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save details'}
          </Button>
          {saved && !save.isPending ? (
            <span className="text-sm text-muted-foreground">Saved.</span>
          ) : null}
        </div>
      </form>
    </Card>
  );
}

/** An empty box means "not set", which the API expects as null rather than an empty string. */
function blankToNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
