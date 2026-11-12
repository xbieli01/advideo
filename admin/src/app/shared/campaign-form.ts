import { Campaign, CampaignStatus, Creative, PricingModel } from '../core/models';
import { fromList, isoToLocalInput, localInputToIso, toList, toNumberOrNull } from './util';

/** Flat, form-friendly view of a Campaign (lists as comma separated text, dates as local inputs). */
export interface CampaignForm {
  id: string;
  advertiserId: string;
  name: string;
  status: CampaignStatus;
  startAt: string;
  endAt: string;
  priority: number;
  bids: { model: PricingModel; amount: number | null }[];
  budgetTotal: number | null;
  budgetDaily: number | null;
  capPerViewerPerVideo: number | null;
  capPerViewerPerDay: number | null;
  exclusivityKey: string;
  brands: string;
  entities: string; // "type:label, type:label"
  keywords: string;
  categories: string;
  excludeCategories: string;
  excludeKeywords: string;
  minScore: number;
  platforms: string;
  includeVideos: string;
  excludeVideos: string;
  geo: string;
  creatives: Creative[];
}

export function newCampaignForm(): CampaignForm {
  return {
    id: '',
    advertiserId: '',
    name: '',
    status: 'draft',
    startAt: '',
    endAt: '',
    priority: 5,
    bids: [{ model: 'cpm', amount: null }],
    budgetTotal: null,
    budgetDaily: null,
    capPerViewerPerVideo: null,
    capPerViewerPerDay: null,
    exclusivityKey: '',
    brands: '',
    entities: '',
    keywords: '',
    categories: '',
    excludeCategories: '',
    excludeKeywords: '',
    minScore: 0,
    platforms: '',
    includeVideos: '',
    excludeVideos: '',
    geo: '',
    creatives: [],
  };
}

export function campaignToForm(c: Campaign): CampaignForm {
  const t = c.targeting;
  return {
    id: c.id,
    advertiserId: c.advertiserId,
    name: c.name,
    status: c.status,
    startAt: isoToLocalInput(c.startAt),
    endAt: isoToLocalInput(c.endAt),
    priority: c.priority,
    bids: (c.bids ?? []).map((b) => ({ model: b.model, amount: b.amount })),
    budgetTotal: c.budget?.total ?? null,
    budgetDaily: c.budget?.daily ?? null,
    capPerViewerPerVideo: c.frequencyCap?.perViewerPerVideo ?? null,
    capPerViewerPerDay: c.frequencyCap?.perViewerPerDay ?? null,
    exclusivityKey: c.exclusivityKey ?? '',
    brands: fromList(t?.brands),
    entities: (t?.entities ?? []).map((e) => `${e.type}:${e.label}`).join(', '),
    keywords: fromList(t?.keywords),
    categories: fromList(t?.categories),
    excludeCategories: fromList(t?.excludeCategories),
    excludeKeywords: fromList(t?.excludeKeywords),
    minScore: t?.minScore ?? 0,
    platforms: fromList(t?.platforms),
    includeVideos: fromList(t?.includeVideos),
    excludeVideos: fromList(t?.excludeVideos),
    geo: fromList(t?.geo),
    creatives: (c.creatives ?? []).map((cr) => ({ ...cr })),
  };
}

export function formToCampaign(f: CampaignForm): Campaign {
  return {
    id: f.id.trim(),
    advertiserId: f.advertiserId.trim(),
    name: f.name.trim(),
    status: f.status,
    startAt: localInputToIso(f.startAt),
    endAt: localInputToIso(f.endAt),
    priority: Number(f.priority) || 5,
    bids: f.bids
      .filter((b) => toNumberOrNull(b.amount) !== null)
      .map((b) => ({ model: b.model, amount: Number(b.amount) })),
    budget: { total: toNumberOrNull(f.budgetTotal), daily: toNumberOrNull(f.budgetDaily) },
    frequencyCap: {
      perViewerPerVideo: toNumberOrNull(f.capPerViewerPerVideo),
      perViewerPerDay: toNumberOrNull(f.capPerViewerPerDay),
    },
    exclusivityKey: f.exclusivityKey.trim() || null,
    targeting: {
      brands: toList(f.brands),
      entities: toList(f.entities).map(parseEntity),
      keywords: toList(f.keywords),
      categories: toList(f.categories),
      excludeCategories: toList(f.excludeCategories),
      excludeKeywords: toList(f.excludeKeywords),
      minScore: Number(f.minScore) || 0,
      platforms: toList(f.platforms),
      includeVideos: toList(f.includeVideos),
      excludeVideos: toList(f.excludeVideos),
      geo: toList(f.geo).map((g) => g.toUpperCase()),
    },
    creatives: f.creatives
      .filter((c) => c.id.trim() && c.url.trim())
      .map((c) => ({
        id: c.id.trim(),
        url: c.url.trim(),
        clickUrl: c.clickUrl.trim(),
        width: Number(c.width) || 0,
        height: Number(c.height) || 0,
        altText: c.altText?.trim() || null,
      })),
  };
}

/** "object:car" -> {type:"object", label:"car"}; without a type the whole text is the label. */
function parseEntity(s: string): { type: string; label: string } {
  const i = s.indexOf(':');
  return i > 0 ? { type: s.slice(0, i).trim(), label: s.slice(i + 1).trim() } : { type: 'object', label: s };
}
