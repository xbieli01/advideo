import { ApiClient } from "./api";
import { ConsentState } from "./consent";
import { renderBanner } from "./renderer";
import type { AdDecision, AdEventType, AdvideoOptions, SnippetEvent, Transport } from "./types";
import { ViewerId } from "./viewer";
import { watchViewable } from "./viewability";

interface Shown {
  decision: AdDecision;
  el: HTMLElement;
  impressed: boolean;
  stopViewable: () => void;
}

/**
 * One snippet instance per player. Follows playback time, asks the ad server for the ad matching the
 * current content segment, renders it into the platform's banner slot and reports delivery events.
 */
export class AdvideoPlayer {
  private readonly api: ApiClient;
  private readonly consent: ConsentState;
  private readonly viewer: ViewerId;
  private readonly video: HTMLVideoElement;
  private readonly slots = new Map<string, HTMLElement>();
  private readonly opts: Required<Pick<AdvideoOptions, "adLabel" | "closable" | "minRequestIntervalSec" | "noAdBackoffSec">>;

  private videoId: string;
  private shown: Shown | null = null;
  private inflight: AbortController | null = null;
  private nextRequestAt = 0;
  private adsInVideo = 0;
  private lastAdAt: number | null = null;
  private prevCampaign: string | null = null;
  private readonly closedCampaigns = new Set<string>();
  private destroyed = false;
  private readonly unlisten: (() => void)[] = [];

  constructor(private readonly options: AdvideoOptions) {
    const doc = typeof options.video === "string" ? document : options.video.ownerDocument;
    this.video = resolve<HTMLVideoElement>(options.video, doc, "video");
    for (const [id, el] of Object.entries(options.slots)) this.slots.set(id, resolve<HTMLElement>(el, doc, `slot "${id}"`));

    const transport: Transport = {
      fetch: options.transport?.fetch ?? ((...a) => fetch(...a)),
      sendBeacon: options.transport?.sendBeacon ?? ((u, d) => navigator.sendBeacon(u, d)),
    };
    this.api = new ApiClient(options.apiBase, transport);
    this.consent = new ConsentState(options.consent?.mode ?? "manual", options.consent?.granted ?? false);
    this.viewer = new ViewerId(options.storage ?? safeLocalStorage());
    this.videoId = options.videoId;
    this.opts = {
      adLabel: options.adLabel ?? "Reklama",
      closable: options.closable ?? true,
      minRequestIntervalSec: options.minRequestIntervalSec ?? 5,
      noAdBackoffSec: options.noAdBackoffSec ?? 15,
    };

    this.on("timeupdate", () => this.tick());
    this.on("playing", () => this.tick());
    this.on("seeked", () => this.onSeek());
    this.on("ended", () => this.remove("expired"));
    this.consent.onChange((granted) => {
      if (!granted) this.viewer.get(false); // withdraw -> delete stored id
    });
  }

  /** Consent from the site's banner (consent mode "manual"). */
  setConsent(granted: boolean): void {
    this.consent.set(granted);
  }

  /** Switch to another video in the same player (SPA navigation); resets per-video counters. */
  setVideo(videoId: string): void {
    this.remove("destroyed");
    this.abort();
    this.videoId = videoId;
    this.adsInVideo = 0;
    this.lastAdAt = null;
    this.prevCampaign = null;
    this.closedCampaigns.clear();
    this.nextRequestAt = 0;
  }

  destroy(): void {
    this.destroyed = true;
    this.abort();
    this.remove("destroyed");
    for (const u of this.unlisten) u();
  }

  // ------------------------------------------------------------------ playback

  /** Exposed for tests / custom players that do not fire media events. */
  tick(): void {
    if (this.destroyed) return;
    const t = this.video.currentTime;
    const s = this.shown;
    if (s) {
      if (t >= s.decision.ad.showToSec) {
        this.remove("expired");
        this.nextRequestAt = Math.min(this.nextRequestAt, t);
      } else {
        return; // keep the current banner for its segment
      }
    }
    if (this.video.paused || this.inflight || t < this.nextRequestAt) return;
    void this.request(t);
  }

  private onSeek(): void {
    const t = this.video.currentTime;
    const s = this.shown;
    if (s && (t < s.decision.ad.showFromSec - 0.5 || t >= s.decision.ad.showToSec)) this.remove("seek");
    if (!this.shown) {
      this.abort();
      this.nextRequestAt = t; // new position -> ask right away
      this.tick();
    }
  }

  private async request(t: number): Promise<void> {
    const ctrl = new AbortController();
    this.inflight = ctrl;
    this.nextRequestAt = t + this.opts.minRequestIntervalSec;
    try {
      const r = await this.api.decide(
        {
          platform: this.options.platform,
          videoId: this.videoId,
          t,
          viewer: this.viewer.get(this.consent.allowed),
          geo: this.options.geo,
          lastAdAt: this.lastAdAt,
          adsInVideo: this.adsInVideo,
          prevCampaign: this.prevCampaign,
        },
        ctrl.signal,
      );
      if (ctrl.signal.aborted || this.destroyed) return;
      if (!r.ad) {
        this.nextRequestAt = t + this.opts.noAdBackoffSec;
        this.emit({ type: "no-ad", t, reason: r.reason });
        return;
      }
      const d = r.ad;
      this.emit({ type: "decision", decision: d });
      const now = this.video.currentTime;
      const slot = this.slots.get(d.ad.slot.id);
      if (this.closedCampaigns.has(d.ad.campaignId) || now >= d.ad.showToSec || !slot) {
        if (!slot) this.emit({ type: "error", error: new Error(`advideo: no container for slot "${d.ad.slot.id}"`) });
        this.nextRequestAt = Math.max(this.nextRequestAt, d.ad.showToSec);
        return;
      }
      this.show(d, slot, now);
    } catch (error) {
      if (!ctrl.signal.aborted) {
        this.nextRequestAt = t + this.opts.noAdBackoffSec;
        this.emit({ type: "error", error });
      }
    } finally {
      if (this.inflight === ctrl) this.inflight = null;
    }
  }

  // ------------------------------------------------------------------ banner

  private show(d: AdDecision, container: HTMLElement, t: number): void {
    const s: Shown = { decision: d, el: null as unknown as HTMLElement, impressed: false, stopViewable: () => undefined };
    s.el = renderBanner(container, d, {
      adLabel: this.opts.adLabel,
      closable: this.opts.closable,
      onLoad: () => this.impression(s, t),
      onClick: () => this.track(d, "click"),
      onClose: () => {
        this.track(d, "close");
        this.closedCampaigns.add(d.ad.campaignId);
        this.remove("closed");
      },
    });
    this.shown = s;
    this.emit({ type: "rendered", decision: d });
  }

  private impression(s: Shown, t: number): void {
    if (s.impressed || this.shown !== s) return;
    s.impressed = true;
    this.adsInVideo++;
    this.lastAdAt = t;
    this.prevCampaign = s.decision.ad.campaignId;
    this.track(s.decision, "impression");
    s.stopViewable = watchViewable(s.el, () => this.track(s.decision, "viewable"), s.el.ownerDocument);
  }

  private remove(why: "expired" | "seek" | "closed" | "destroyed"): void {
    const s = this.shown;
    if (!s) return;
    this.shown = null;
    s.stopViewable();
    s.el.remove();
    this.emit({ type: "removed", decisionId: s.decision.decisionId, why });
  }

  private track(d: AdDecision, event: AdEventType): void {
    this.api.track(d.tracking.eventsUrl, d.decisionId, event, this.video.currentTime);
    this.emit({ type: "tracked", decisionId: d.decisionId, event });
  }

  // ------------------------------------------------------------------ util

  private abort(): void {
    this.inflight?.abort();
    this.inflight = null;
  }

  private on(type: string, fn: () => void): void {
    this.video.addEventListener(type, fn);
    this.unlisten.push(() => this.video.removeEventListener(type, fn));
  }

  private emit(e: SnippetEvent): void {
    try {
      this.options.onEvent?.(e);
    } catch {
      /* never let host callbacks break the player */
    }
  }
}

function resolve<T extends Element>(x: T | string, doc: Document, what: string): T {
  if (typeof x !== "string") return x;
  const el = doc.querySelector<T>(x);
  if (!el) throw new Error(`advideo: ${what} not found for selector "${x}"`);
  return el;
}

function safeLocalStorage(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}
