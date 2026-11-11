import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { init } from "../src/index";
import { safeUrl } from "../src/renderer";
import type { SnippetEvent } from "../src/types";
import { baseOptions, decision, fakeServer, FakeIO, FakeVideo, flush, memoryStorage, patchBlob } from "./helpers";

let unpatch: () => void;
beforeEach(() => {
  unpatch = patchBlob();
  (globalThis as { IntersectionObserver?: unknown }).IntersectionObserver = FakeIO;
});
afterEach(() => {
  unpatch();
  vi.useRealTimers();
});

const img = () => document.querySelector<HTMLImageElement>("#right img")!;
const loadImg = () => img().dispatchEvent(new Event("load"));

describe("advideo snippet", () => {
  it("requests a decision while playing and renders the banner into the platform slot", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision());
    init(baseOptions(v, s, [], { geo: "SK" }));

    v.play(2);
    await flush();

    const q = s.requests[0]!;
    expect(q.pathname).toBe("/ads/decision");
    expect(Object.fromEntries(q.searchParams)).toMatchObject({ platform: "demo-portal", videoId: "vid", t: "2.00", adsInVideo: "0", geo: "SK" });
    const banner = document.querySelector("#right .advideo-banner")!;
    expect(banner.querySelector(".advideo-label")!.textContent).toBe("Reklama");
    expect(img().src).toBe("https://cdn.example/b.png");
    expect(img().alt).toBe("Tesla");
    const a = banner.querySelector("a")!;
    expect(a.rel).toContain("sponsored");
    expect(a.target).toBe("_blank");
  });

  it("does not request while paused", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    init(baseOptions(v, s));
    v.at(5);
    await flush();
    expect(s.requests).toHaveLength(0);
  });

  it("tracks impression on image load and sends client counters on the next request", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision({ showToSec: 30 }));
    init(baseOptions(v, s));
    v.play(2);
    await flush();
    loadImg();
    expect(s.beacons.map((b) => b.body.type)).toEqual(["impression"]);
    expect(s.beacons[0]!.url).toBe("https://ads.example/ads/events");
    expect(s.beacons[0]!.body.decisionId).toBe("dcs_1");

    v.at(31); // segment over -> banner removed, next request
    await flush();
    expect(document.querySelector("#right .advideo-banner")).toBeNull();
    const q = s.requests[1]!.searchParams;
    expect(q.get("adsInVideo")).toBe("1");
    expect(q.get("lastAdAt")).toBe("2.00");
    expect(q.get("prevCampaign")).toBe("tesla");
  });

  it("reports viewable after 50 % in view for one second", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision());
    init(baseOptions(v, s));
    v.play(1);
    await flush();
    vi.useFakeTimers();
    loadImg();
    FakeIO.last!.fire(0.6);
    vi.advanceTimersByTime(500);
    FakeIO.last!.fire(0.2); // scrolled away before 1 s
    vi.advanceTimersByTime(1000);
    expect(s.beacons.map((b) => b.body.type)).toEqual(["impression"]);
    FakeIO.last!.fire(0.8);
    vi.advanceTimersByTime(1000);
    expect(s.beacons.map((b) => b.body.type)).toEqual(["impression", "viewable"]);
  });

  it("backs off after no-ad and respects the minimum request interval", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    const events: SnippetEvent[] = [];
    init(baseOptions(v, s, events, { noAdBackoffSec: 15, minRequestIntervalSec: 5 }));
    v.play(0);
    await flush();
    expect(events.find((e) => e.type === "no-ad")).toMatchObject({ reason: "No campaign matched this segment." });
    v.at(10);
    await flush();
    expect(s.requests).toHaveLength(1);
    v.at(15);
    await flush();
    expect(s.requests).toHaveLength(2);
  });

  it("seek outside the ad window removes the banner and asks immediately", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision({ showFromSec: 0, showToSec: 30 }), decision({ campaignId: "nike", showFromSec: 120, showToSec: 150 }, "dcs_2"));
    const events: SnippetEvent[] = [];
    init(baseOptions(v, s, events));
    v.play(1);
    await flush();
    v.seek(125);
    await flush();
    expect(events).toContainEqual({ type: "removed", decisionId: "dcs_1", why: "seek" });
    expect(s.requests[1]!.searchParams.get("t")).toBe("125.00");
    expect(document.querySelector<HTMLElement>("#right .advideo-banner")!.dataset.advideoCampaign).toBe("nike");
  });

  it("close button tracks close and suppresses the campaign for this video", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision({ showToSec: 30 }), decision({ showFromSec: 30, showToSec: 60 }, "dcs_2"));
    init(baseOptions(v, s));
    v.play(1);
    await flush();
    loadImg();
    document.querySelector<HTMLButtonElement>(".advideo-close")!.click();
    expect(s.beacons.map((b) => b.body.type)).toEqual(["impression", "close"]);
    expect(document.querySelector(".advideo-banner")).toBeNull();
    v.at(31);
    await flush();
    expect(s.requests).toHaveLength(2);
    expect(document.querySelector(".advideo-banner")).toBeNull(); // same campaign not shown again
  });

  it("click is tracked", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision());
    init(baseOptions(v, s));
    v.play(1);
    await flush();
    const a = document.querySelector<HTMLAnchorElement>("#right a")!;
    a.addEventListener("click", (e) => e.preventDefault()); // jsdom: no navigation
    a.click();
    expect(s.beacons.map((b) => b.body.type)).toContain("click");
  });

  it("falls back to fetch keepalive with text/plain when sendBeacon fails", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.setBeaconOk(false);
    s.replies.push(decision());
    init(baseOptions(v, s));
    v.play(1);
    await flush();
    loadImg();
    expect(s.posts).toHaveLength(1);
    expect(s.posts[0]!.headers["Content-Type"]).toBe("text/plain");
    expect(JSON.parse(s.posts[0]!.body)).toMatchObject({ decisionId: "dcs_1", type: "impression" });
  });

  it("unknown slot id emits an error and renders nothing", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    const events: SnippetEvent[] = [];
    s.replies.push(decision({ slot: { id: "sidebar", position: "left", width: 160, height: 600 } }));
    init(baseOptions(v, s, events));
    v.play(1);
    await flush();
    expect(events.some((e) => e.type === "error")).toBe(true);
    expect(document.querySelector(".advideo-banner")).toBeNull();
  });

  it("setVideo resets per-video counters", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision({ showToSec: 10 }));
    const p = init(baseOptions(v, s));
    v.play(1);
    await flush();
    loadImg();
    p.setVideo("other");
    v.at(2);
    await flush();
    const q = s.requests[1]!.searchParams;
    expect(q.get("videoId")).toBe("other");
    expect(q.get("adsInVideo")).toBe("0");
    expect(q.has("lastAdAt")).toBe(false);
  });

  it("destroy removes banner and stops requests", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    s.replies.push(decision());
    const p = init(baseOptions(v, s));
    v.play(1);
    await flush();
    p.destroy();
    expect(document.querySelector(".advideo-banner")).toBeNull();
    v.at(100);
    await flush();
    expect(s.requests).toHaveLength(1);
  });
});

describe("consent and viewer id", () => {
  it("manual mode: no viewer id until consent, removed again on withdrawal", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    const storage = memoryStorage();
    const p = init(baseOptions(v, s, [], { consent: { mode: "manual", granted: false }, storage, minRequestIntervalSec: 1, noAdBackoffSec: 1 }));
    v.play(0);
    await flush();
    expect(s.requests[0]!.searchParams.has("viewer")).toBe(false);
    expect(storage.map.size).toBe(0);

    p.setConsent(true);
    v.at(2);
    await flush();
    const id = s.requests[1]!.searchParams.get("viewer");
    expect(id).toBeTruthy();
    v.at(4);
    await flush();
    expect(s.requests[2]!.searchParams.get("viewer")).toBe(id); // stable

    p.setConsent(false);
    expect(storage.map.size).toBe(0);
  });

  it("tcf mode: viewer id only with purpose 1 consent", async () => {
    let listener: ((d: unknown, ok: boolean) => void) | null = null;
    (window as unknown as { __tcfapi: unknown }).__tcfapi = (_c: string, _v: number, cb: (d: unknown, ok: boolean) => void) => { listener = cb; };
    const v = new FakeVideo();
    const s = fakeServer();
    init(baseOptions(v, s, [], { consent: { mode: "tcf" }, minRequestIntervalSec: 1, noAdBackoffSec: 1 }));
    v.play(0);
    await flush();
    expect(s.requests[0]!.searchParams.has("viewer")).toBe(false);
    listener!({ gdprApplies: true, eventStatus: "useractioncomplete", purpose: { consents: { "1": true } } }, true);
    v.at(2);
    await flush();
    expect(s.requests[1]!.searchParams.has("viewer")).toBe(true);
    delete (window as unknown as { __tcfapi?: unknown }).__tcfapi;
  });

  it("none mode never stores an id", async () => {
    const v = new FakeVideo();
    const s = fakeServer();
    const p = init(baseOptions(v, s, [], { consent: { mode: "none" } }));
    p.setConsent(true);
    v.play(0);
    await flush();
    expect(s.requests[0]!.searchParams.has("viewer")).toBe(false);
  });
});

describe("safeUrl", () => {
  it("allows only http(s)", () => {
    expect(safeUrl("https://a.example/x")).toBe("https://a.example/x");
    expect(safeUrl("javascript:alert(1)")).toBe("about:blank");
    expect(safeUrl("data:text/html,<b>")).toBe("about:blank");
  });
});
