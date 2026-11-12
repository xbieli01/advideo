import { AdSlot, PlatformConfig, PlatformKind, PricingModel } from '../core/models';
import { fromList, toList, toNumberOrNull } from './util';

export interface PlatformForm {
  id: string;
  name: string;
  kind: PlatformKind;
  enabledModels: Record<PricingModel, boolean>;
  currency: string;
  defaultCtr: number;
  defaultViewableRate: number;
  flatSponsorshipEcpm: number;
  slots: AdSlot[];
  minIntervalSec: number;
  maxPerVideo: number;
  maxPerViewerPerDay: number | null;
  excludeCategories: string;
  excludeKeywords: string;
  relevanceThreshold: number;
  wBrand: number;
  wEntity: number;
  wKeyword: number;
  wCategory: number;
}

export function newPlatformForm(): PlatformForm {
  return {
    id: '',
    name: '',
    kind: 'portal',
    enabledModels: { cpm: true, cpc: false, cpv: false, flat: false },
    currency: 'EUR',
    defaultCtr: 0.002,
    defaultViewableRate: 0.6,
    flatSponsorshipEcpm: 1000,
    slots: [{ id: 'below-player', position: 'below', width: 728, height: 90 }],
    minIntervalSec: 60,
    maxPerVideo: 3,
    maxPerViewerPerDay: null,
    excludeCategories: '',
    excludeKeywords: '',
    relevanceThreshold: 0.3,
    wBrand: 1,
    wEntity: 0.8,
    wKeyword: 0.6,
    wCategory: 0.4,
  };
}

export function platformToForm(p: PlatformConfig): PlatformForm {
  const d = newPlatformForm();
  const models = p.pricing?.enabledModels ?? ['cpm'];
  const w = p.matching?.weights;
  return {
    id: p.id,
    name: p.name,
    kind: p.kind ?? 'portal',
    enabledModels: { cpm: models.includes('cpm'), cpc: models.includes('cpc'), cpv: models.includes('cpv'), flat: models.includes('flat') },
    currency: p.pricing?.currency ?? d.currency,
    defaultCtr: p.pricing?.defaultCtr ?? d.defaultCtr,
    defaultViewableRate: p.pricing?.defaultViewableRate ?? d.defaultViewableRate,
    flatSponsorshipEcpm: p.pricing?.flatSponsorshipEcpm ?? d.flatSponsorshipEcpm,
    slots: (p.slots ?? []).map((s) => ({ ...s })),
    minIntervalSec: p.frequency?.minIntervalSec ?? d.minIntervalSec,
    maxPerVideo: p.frequency?.maxPerVideo ?? d.maxPerVideo,
    maxPerViewerPerDay: p.frequency?.maxPerViewerPerDay ?? null,
    excludeCategories: fromList(p.brandSafety?.excludeCategories),
    excludeKeywords: fromList(p.brandSafety?.excludeKeywords),
    relevanceThreshold: p.matching?.relevanceThreshold ?? d.relevanceThreshold,
    wBrand: w?.brand ?? d.wBrand,
    wEntity: w?.entity ?? d.wEntity,
    wKeyword: w?.keyword ?? d.wKeyword,
    wCategory: w?.category ?? d.wCategory,
  };
}

export function formToPlatform(f: PlatformForm): PlatformConfig {
  const models = (Object.keys(f.enabledModels) as PricingModel[]).filter((m) => f.enabledModels[m]);
  return {
    id: f.id.trim(),
    name: f.name.trim(),
    kind: f.kind,
    pricing: {
      enabledModels: models.length ? models : ['cpm'],
      currency: f.currency.trim().toUpperCase() || 'EUR',
      defaultCtr: Number(f.defaultCtr),
      defaultViewableRate: Number(f.defaultViewableRate),
      flatSponsorshipEcpm: Number(f.flatSponsorshipEcpm),
    },
    slots: f.slots
      .filter((s) => s.id.trim())
      .map((s) => ({ id: s.id.trim(), position: s.position, width: Number(s.width), height: Number(s.height) })),
    frequency: {
      minIntervalSec: Number(f.minIntervalSec),
      maxPerVideo: Number(f.maxPerVideo),
      maxPerViewerPerDay: toNumberOrNull(f.maxPerViewerPerDay),
    },
    brandSafety: { excludeCategories: toList(f.excludeCategories), excludeKeywords: toList(f.excludeKeywords) },
    matching: {
      relevanceThreshold: Number(f.relevanceThreshold),
      weights: { brand: Number(f.wBrand), entity: Number(f.wEntity), keyword: Number(f.wKeyword), category: Number(f.wCategory) },
    },
  };
}
