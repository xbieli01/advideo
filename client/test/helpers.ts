import type { AdDecision, AdvideoOptions, SnippetEvent } from "../src/types";

/** Minimal stand-in for HTMLVideoElement (jsdom does not implement media playback). */
export class FakeVideo extends EventTarget {
  currentTime = 0;
  paused = true;
  ownerDocument = document;
  play(t = this.currentTime) {
    this.currentTime = t;
    this.paused = false;
    this.dispatchEvent(new Event("playing"));
  }
  at(t: number) {
    this.currentTime = t;
    this.dispatchEvent(new Event("timeupdate"));
  }
  seek(t: number) {
    this.currentTime = t;
    this.dispatchEvent(new Event("seeked"));
  }
}

export function decision(over: Partial<AdDecision["ad"]> = {}, id = "dcs_1"): AdDecision {
  return {
    decisionId: id,
    platformId: "demo-portal",
    videoId: "vid",
    t: 0,
    segment: { startSec: 0, endSec: 30 },
    ad: {
      campaignId: "tesla",
      advertiserId: "tesla",
      creative: { id: "cr", url: "https://cdn.example/b.png", clickUrl: "https://tesla.example/", width: 300, height: 250, altText: "Tesla" },
      slot: { id: "right", position: "right", width: 300, height: 250 },
      showFromSec: 0,
      showToSec: 30,
      pricingModel: "cpm",
      ...over,
    },
    matchedOn: [{ kind: "brand", term: "Tesla", score: 0.9 }],
    tracking: { eventsUrl: "/ads/events", events: ["impression", "viewable", "click", "close"] },
  };
}

export type Reply = AdDecision | { noAd: string };

/** Fake server: queue of replies for /ads/decision, records requests and beacons. */
export function fakeServer() {
  const replies: Reply[] = [];
  const requests: URL[] = [];
  const beacons: { url: string; body: { decisionId: string; type: string; t: number } }[] = [];
  const posts: { url: string; body: string; headers: Record<string, string> }[] = [];
  let beaconOk = true;

  const fetchFn = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    if (init?.method === "POST") {
      posts.push({ url: url.href, body: String(init.body), headers: init.headers as Record<string, string> });
      return new Response(null, { status: 202 });
    }
    requests.push(url);
    const r = replies.shift() ?? { noAd: "No campaign matched this segment." };
    if ("noAd" in r) return new Response(null, { status: 204, headers: { "X-Advideo-NoAd-Reason": r.noAd } });
    return new Response(JSON.stringify(r), { status: 200, headers: { "Content-Type": "application/json" } });
  }) as typeof fetch;

  const sendBeacon = (url: string, data: BodyInit) => {
    if (!beaconOk) return false;
    const blob = data as Blob;
    // jsdom Blob -> read synchronously via FileReaderSync is unavailable; keep text in a side channel
    beacons.push({ url, body: JSON.parse((blob as unknown as { __text: string }).__text ?? "{}") });
    return true;
  };

  return {
    replies, requests, beacons, posts,
    setBeaconOk: (ok: boolean) => { beaconOk = ok; },
    transport: { fetch: fetchFn, sendBeacon },
  };
}

/** Make Blob remember its text so sendBeacon payloads can be asserted synchronously. */
export function patchBlob() {
  const Orig = globalThis.Blob;
  class TextBlob extends Orig {
    __text: string;
    constructor(parts: BlobPart[] = [], opts?: BlobPropertyBag) {
      super(parts, opts);
      this.__text = parts.map(String).join("");
    }
  }
  globalThis.Blob = TextBlob as unknown as typeof Blob;
  return () => { globalThis.Blob = Orig; };
}

/** Controllable IntersectionObserver. */
export class FakeIO {
  static last: FakeIO | null = null;
  constructor(public cb: IntersectionObserverCallback) { FakeIO.last = this; }
  observe() {}
  disconnect() {}
  unobserve() {}
  takeRecords() { return []; }
  fire(ratio: number) {
    this.cb([{ isIntersecting: ratio > 0, intersectionRatio: ratio } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
  }
}

export const flush = () => new Promise<void>((r) => setTimeout(r, 0));

export function memoryStorage() {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => void m.set(k, v),
    removeItem: (k: string) => void m.delete(k),
    map: m,
  };
}

export function baseOptions(video: FakeVideo, server: ReturnType<typeof fakeServer>, events: SnippetEvent[] = [], extra: Partial<AdvideoOptions> = {}): AdvideoOptions {
  document.body.innerHTML = `<div id="right"></div><div id="below"></div>`;
  return {
    apiBase: "https://ads.example/",
    platform: "demo-portal",
    videoId: "vid",
    video: video as unknown as HTMLVideoElement,
    slots: { right: "#right", "below-player": "#below" },
    transport: server.transport,
    storage: memoryStorage(),
    onEvent: (e) => events.push(e),
    ...extra,
  };
}
