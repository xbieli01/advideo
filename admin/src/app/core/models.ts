// Mirrors the advideo API contracts (System.Text.Json, camelCase, enums as camelCase strings).

export type PricingModel = 'cpm' | 'cpc' | 'cpv' | 'flat';
export type CampaignStatus = 'draft' | 'active' | 'paused' | 'ended';
export type PlatformKind = 'portal' | 'browserExtension';
export type SlotPosition = 'above' | 'below' | 'left' | 'right';

export const PRICING_MODELS: PricingModel[] = ['cpm', 'cpc', 'cpv', 'flat'];
export const CAMPAIGN_STATUSES: CampaignStatus[] = ['draft', 'active', 'paused', 'ended'];
export const SLOT_POSITIONS: SlotPosition[] = ['above', 'below', 'left', 'right'];

export interface AuthMe {
  name: string;
  roles: string[];
}

export interface AdSlot {
  id: string;
  position: SlotPosition;
  width: number;
  height: number;
}

export interface PlatformConfig {
  id: string;
  name: string;
  kind: PlatformKind;
  pricing: {
    enabledModels: PricingModel[];
    defaultCtr: number;
    defaultViewableRate: number;
    flatSponsorshipEcpm: number;
    currency: string;
  };
  slots: AdSlot[];
  frequency: { minIntervalSec: number; maxPerVideo: number; maxPerViewerPerDay: number | null };
  brandSafety: { excludeCategories: string[]; excludeKeywords: string[] };
  matching: {
    relevanceThreshold: number;
    weights: { brand: number; entity: number; keyword: number; category: number };
  };
}

export interface Bid {
  model: PricingModel;
  amount: number;
}

export interface EntityTarget {
  type: string;
  label: string;
}

export interface Targeting {
  brands: string[];
  entities: EntityTarget[];
  keywords: string[];
  categories: string[];
  excludeCategories: string[];
  excludeKeywords: string[];
  minScore: number;
  platforms: string[];
  includeVideos: string[];
  excludeVideos: string[];
  geo: string[];
}

export interface Creative {
  id: string;
  url: string;
  clickUrl: string;
  width: number;
  height: number;
  altText: string | null;
}

export interface Campaign {
  id: string;
  advertiserId: string;
  name: string;
  status: CampaignStatus;
  startAt: string | null;
  endAt: string | null;
  priority: number;
  bids: Bid[];
  budget: { total: number | null; daily: number | null };
  frequencyCap: { perViewerPerVideo: number | null; perViewerPerDay: number | null };
  exclusivityKey: string | null;
  targeting: Targeting;
  creatives: Creative[];
}

export interface CampaignStats {
  campaignId: string;
  impressions: number;
  viewables: number;
  clicks: number;
  closes: number;
  ctr: number;
  spentTotal: number;
  spentToday: number;
}

export interface VideoJob {
  videoId: string;
  videoUri: string;
  analyzer: string;
  track: string | null;
  status: string; // Queued | Running | Succeeded | Failed (PascalCase from the API)
  mapKey: string | null;
  error: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RegisterVideoRequest {
  videoId: string;
  videoUri: string;
  analyzer?: string | null;
  track?: string | null;
}

export interface MatchHit {
  kind: string;
  term: string;
  score: number;
  contribution: number;
}

export interface ExplainCandidate {
  campaignId: string;
  relevance: number;
  ecpm: number;
  rank: number;
  matchedOn: MatchHit[];
  creativeId: string | null;
  slotId: string | null;
  rejectedBecause: string | null;
}

export interface ExplainResponse {
  platformId: string;
  videoId: string;
  t: number;
  segment: { startSec: number; endSec: number } | null;
  winnerCampaignId: string | null;
  noAdReason: string | null;
  candidates: ExplainCandidate[];
}
