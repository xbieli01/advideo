/** Wire types of the advideo ad-serving API (see orchestrator README / docs/m2-ad-serving.md). */

export type PricingModel = "cpm" | "cpc" | "cpv" | "flat";
export type SlotPosition = "above" | "below" | "left" | "right";
export type AdEventType = "impression" | "viewable" | "click" | "close";

export interface Creative {
  id: string;
  url: string;
  clickUrl: string;
  width: number;
  height: number;
  altText?: string | null;
}

export interface AdSlot {
  id: string;
  position: SlotPosition;
  width: number;
  height: number;
}

export interface AdDecision {
  decisionId: string;
  platformId: string;
  videoId: string;
  t: number;
  segment: { startSec: number; endSec: number };
  ad: {
    campaignId: string;
    advertiserId: string;
    creative: Creative;
    slot: AdSlot;
    showFromSec: number;
    showToSec: number;
    pricingModel: PricingModel;
  };
  matchedOn: { kind: string; term: string; score: number }[];
  tracking: { eventsUrl: string; events: AdEventType[] };
}

/** How the snippet decides whether it may store an anonymous viewer id (needed for frequency capping). */
export type ConsentMode =
  /** Use IAB TCF v2 (`window.__tcfapi`): storage allowed when purpose 1 is consented. */
  | "tcf"
  /** The site's own consent banner calls `setConsent(true|false)`. */
  | "manual"
  /** Never store a viewer id (frequency caps then rely on per-page counters only). */
  | "none";

export interface AdvideoOptions {
  /** Base URL of the ad-serving API, e.g. "https://ads.example.com". */
  apiBase: string;
  /** Platform id configured on the server (customer portal or browser extension). */
  platform: string;
  /** Id of the video in the content-map store. */
  videoId: string;
  /** The HTML5 video element (or a CSS selector). */
  video: HTMLVideoElement | string;
  /**
   * Banner containers around the player, keyed by the platform's slot id
   * (e.g. { "below-player": el, "right-of-player": "#ad-right" }).
   */
  slots: Record<string, HTMLElement | string>;
  consent?: { mode: ConsentMode; granted?: boolean };
  /** ISO country code if the site knows it (otherwise the server may infer it later). */
  geo?: string;
  /** Label shown on every banner (legal requirement to mark advertising). Default "Reklama". */
  adLabel?: string;
  /** Show a close (×) button on banners. Default true. */
  closable?: boolean;
  /** Min. playback seconds between two decision requests. Default 5. */
  minRequestIntervalSec?: number;
  /** Playback seconds to wait after "no ad" before asking again. Default 15. */
  noAdBackoffSec?: number;
  /** Debug hook for integration / analytics. */
  onEvent?: (e: SnippetEvent) => void;
  /** Injected for tests; defaults to window.fetch / navigator.sendBeacon / localStorage. */
  transport?: Partial<Transport>;
  storage?: Pick<Storage, "getItem" | "setItem" | "removeItem">;
}

export type SnippetEvent =
  | { type: "decision"; decision: AdDecision }
  | { type: "no-ad"; t: number; reason: string | null }
  | { type: "rendered"; decision: AdDecision }
  | { type: "removed"; decisionId: string; why: "expired" | "seek" | "closed" | "destroyed" }
  | { type: "tracked"; decisionId: string; event: AdEventType }
  | { type: "error"; error: unknown };

export interface Transport {
  fetch: typeof fetch;
  sendBeacon: (url: string, data: BodyInit) => boolean;
}
