// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { newKey, type DayDraft, type InclusionDraft } from '../catalog-rules';
import { InclusionsEditor } from '../components/inclusions-editor';
import { ItineraryBuilder } from '../components/itinerary-builder';
import { PublishChecklist } from '../components/publish-checklist';
import type { Product } from '../types';

afterEach(cleanup);

function day(title: string): DayDraft {
  return { key: newKey(), title, description: '', meals: [], accommodation: '' };
}

function Itinerary({ initial }: { initial: DayDraft[] }) {
  const [days, setDays] = useState(initial);
  return <ItineraryBuilder days={days} onChange={setDays} />;
}

const titles = () => screen.getAllByLabelText('Title').map((input) => (input as HTMLInputElement).value);

describe('ItineraryBuilder', () => {
  it('adds the next day', () => {
    render(<Itinerary initial={[day('Arrive')]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Add day 2' }));

    expect(screen.getByRole('heading', { name: 'Day 2' })).toBeTruthy();
    expect(titles()).toEqual(['Arrive', '']);
  });

  it('moves a day, and the days renumber', () => {
    render(<Itinerary initial={[day('Arrive'), day('Explore'), day('Leave')]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Move day 3 up' }));

    expect(titles()).toEqual(['Arrive', 'Leave', 'Explore']);
    expect(screen.getByRole('button', { name: 'Move day 1 up' }).hasAttribute('disabled')).toBe(true);
  });

  it('removes a day', () => {
    render(<Itinerary initial={[day('Arrive'), day('Leave')]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Remove day 1' }));

    expect(titles()).toEqual(['Leave']);
  });
});

describe('InclusionsEditor', () => {
  function Inclusions() {
    const [inclusions, setInclusions] = useState<InclusionDraft[]>([]);
    return <InclusionsEditor inclusions={inclusions} onChange={setInclusions} />;
  }

  it('adds a line with Enter, to the list it was typed under', () => {
    render(<Inclusions />);

    const field = screen.getByLabelText('Add something not included');
    fireEvent.change(field, { target: { value: 'International flights' } });
    fireEvent.keyDown(field, { key: 'Enter' });

    expect(within(screen.getByRole('list', { name: 'Not included' })).getByText('International flights')).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Included' })).toBeNull();
  });
});

describe('PublishChecklist', () => {
  const draft: Product = {
    id: 'p1',
    productType: 'Tour',
    title: 'Lagos in a day',
    slug: 'lagos-in-a-day',
    status: 'Draft',
    summary: '',
    description: '',
    destinationCountry: 'NG',
    destinationCity: 'Lagos',
    durationDays: 1,
    currency: 'NGN',
    basePriceMinor: 4_500_000,
    availableFrom: null,
    availableTo: null,
    heroAssetId: null,
    media: [],
    categoryIds: [],
    itinerary: [],
    inclusions: [],
    priceVariants: [],
    visa: null,
    publishedAt: null,
    updatedAt: '2026-09-01T10:00:00Z',
    publishProblems: [
      { field: 'media', message: 'Add at least one image.' },
      { field: 'availableTo', message: 'Say until when it can be booked.' },
    ],
  };

  it('lists what the server says is missing, each linking to where to fix it', () => {
    render(<PublishChecklist product={draft} dirty={false} />);

    expect(screen.getByText('2 things before it can be published')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Images' }).getAttribute('href')).toBe('#section-images');
    expect(screen.getByRole('link', { name: 'Basics' }).getAttribute('href')).toBe('#section-basics');
  });

  it('says it is ready once nothing is missing, and that unsaved changes are not checked yet', () => {
    render(<PublishChecklist product={{ ...draft, publishProblems: [] }} dirty />);

    expect(screen.getByText('Ready to publish')).toBeTruthy();
    expect(screen.getByText(/Save to check your changes/)).toBeTruthy();
  });

  it('shows where a published product lives', () => {
    render(<PublishChecklist product={{ ...draft, status: 'Published', publishProblems: [] }} dirty={false} />);

    expect(screen.getByText('Live on your storefront')).toBeTruthy();
    expect(screen.getByText('/lagos-in-a-day')).toBeTruthy();
  });
});
