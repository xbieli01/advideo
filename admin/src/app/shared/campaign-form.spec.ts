import { Campaign } from '../core/models';
import { campaignToForm, formToCampaign, newCampaignForm } from './campaign-form';

const campaign: Campaign = {
  id: 'tesla-model-y',
  advertiserId: 'tesla',
  name: 'Tesla Model Y',
  status: 'active',
  startAt: '2026-11-01T00:00:00.000Z',
  endAt: null,
  priority: 7,
  bids: [{ model: 'cpm', amount: 12 }, { model: 'cpc', amount: 0.8 }],
  budget: { total: 5000, daily: 200 },
  frequencyCap: { perViewerPerVideo: 2, perViewerPerDay: null },
  exclusivityKey: 'auto',
  targeting: {
    brands: ['tesla'],
    entities: [{ type: 'object', label: 'car' }, { type: 'scene', label: 'road' }],
    keywords: ['ev', 'electric'],
    categories: ['automotive'],
    excludeCategories: [],
    excludeKeywords: ['crash'],
    minScore: 0.4,
    platforms: [],
    includeVideos: [],
    excludeVideos: [],
    geo: ['SK', 'CZ'],
  },
  creatives: [{ id: 'banner', url: 'https://cdn/x.png', clickUrl: 'https://tesla.com', width: 728, height: 90, altText: null }],
};

describe('campaign form mapping', () => {
  it('round-trips a campaign', () => {
    expect(formToCampaign(campaignToForm(campaign))).toEqual(campaign);
  });

  it('parses entities, uppercases geo and drops empty bids and creatives', () => {
    const f = newCampaignForm();
    f.id = ' c1 ';
    f.advertiserId = 'a';
    f.name = 'n';
    f.entities = 'car, scene:beach';
    f.geo = 'sk, at';
    f.bids = [{ model: 'cpm', amount: null }, { model: 'cpv', amount: 0.05 }];
    f.creatives = [{ id: '', url: '', clickUrl: '', width: 0, height: 0, altText: null }];
    const c = formToCampaign(f);
    expect(c.id).toBe('c1');
    expect(c.targeting.entities).toEqual([{ type: 'object', label: 'car' }, { type: 'scene', label: 'beach' }]);
    expect(c.targeting.geo).toEqual(['SK', 'AT']);
    expect(c.bids).toEqual([{ model: 'cpv', amount: 0.05 }]);
    expect(c.creatives).toEqual([]);
    expect(c.exclusivityKey).toBeNull();
    expect(c.startAt).toBeNull();
  });
});
