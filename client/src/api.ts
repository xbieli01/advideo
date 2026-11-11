import type { AdDecision, AdEventType, Transport } from "./types";

export interface DecisionQuery {
  platform: string;
  videoId: string;
  t: number;
  viewer?: string | null;
  geo?: string;
  lastAdAt?: number | null;
  adsInVideo: number;
  prevCampaign?: string | null;
}

export type DecisionResult = { ad: AdDecision } | { ad: null; reason: string | null };

/** Thin HTTP client for /ads/decision and /ads/events. */
export class ApiClient {
  private readonly base: string;

  constructor(apiBase: string, private readonly transport: Transport) {
    this.base = apiBase.replace(/\/+$/, "");
  }

  async decide(q: DecisionQuery, signal?: AbortSignal): Promise<DecisionResult> {
    const p = new URLSearchParams({
      platform: q.platform,
      videoId: q.videoId,
      t: q.t.toFixed(2),
      adsInVideo: String(q.adsInVideo),
    });
    if (q.viewer) p.set("viewer", q.viewer);
    if (q.geo) p.set("geo", q.geo);
    if (q.lastAdAt != null) p.set("lastAdAt", q.lastAdAt.toFixed(2));
    if (q.prevCampaign) p.set("prevCampaign", q.prevCampaign);

    const res = await this.transport.fetch(`${this.base}/ads/decision?${p.toString()}`, {
      method: "GET",
      credentials: "omit",
      signal,
    });
    if (res.status === 200) return { ad: (await res.json()) as AdDecision };
    if (res.status === 204) return { ad: null, reason: res.headers.get("X-Advideo-NoAd-Reason") };
    throw new Error(`advideo: decision failed with HTTP ${res.status}`);
  }

  /**
   * Fire-and-forget event. Uses navigator.sendBeacon with a text/plain body so it survives page unload
   * and stays a CORS-simple request; falls back to fetch(keepalive).
   */
  track(eventsUrl: string, decisionId: string, type: AdEventType, t: number): void {
    const url = eventsUrl.startsWith("http") ? eventsUrl : `${this.base}${eventsUrl}`;
    const body = JSON.stringify({ decisionId, type, t: Math.round(t * 100) / 100 });
    let sent = false;
    try {
      sent = this.transport.sendBeacon(url, new Blob([body], { type: "text/plain" }));
    } catch {
      sent = false;
    }
    if (!sent) {
      void this.transport
        .fetch(url, { method: "POST", body, headers: { "Content-Type": "text/plain" }, keepalive: true, credentials: "omit" })
        .catch(() => undefined);
    }
  }
}
