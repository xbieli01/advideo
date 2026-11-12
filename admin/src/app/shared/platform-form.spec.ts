import { formToPlatform, newPlatformForm, platformToForm } from './platform-form';

describe('platform form mapping', () => {
  it('round-trips the default form', () => {
    const f = newPlatformForm();
    f.id = 'joj';
    f.name = 'JOJ';
    expect(platformToForm(formToPlatform(f))).toEqual(f);
  });

  it('falls back to CPM when no model is enabled and normalizes currency', () => {
    const f = newPlatformForm();
    f.id = 'p';
    f.name = 'P';
    f.enabledModels = { cpm: false, cpc: false, cpv: false, flat: false };
    f.currency = ' czk ';
    f.excludeKeywords = 'gore, war';
    const p = formToPlatform(f);
    expect(p.pricing.enabledModels).toEqual(['cpm']);
    expect(p.pricing.currency).toBe('CZK');
    expect(p.brandSafety.excludeKeywords).toEqual(['gore', 'war']);
    expect(p.frequency.maxPerViewerPerDay).toBeNull();
  });
});
