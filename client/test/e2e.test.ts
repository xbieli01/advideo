/**
 * End-to-end against a running orchestrator (skipped unless ADVIDEO_E2E_API is set):
 *   ADVIDEO_E2E_API=http://localhost:5219 ADVIDEO_E2E_VIDEO=golden-mock npx vitest run test/e2e.test.ts
 * Requires the golden map stored under the video id and the sample seed loaded.
 */
import { describe, expect, it } from "vitest";
import { init } from "../src/index";
import type { SnippetEvent } from "../src/types";
import { FakeVideo, memoryStorage } from "./helpers";

const api = process.env.ADVIDEO_E2E_API;
const videoId = process.env.ADVIDEO_E2E_VIDEO ?? "golden-mock";
const adminKey = process.env.ADVIDEO_E2E_KEY ?? "dev-admin-key"; // Development appsettings key
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe.skipIf(!api)("e2e against real ad server", () => {
  it("decision -> banner -> impression -> stats", async () => {
    document.body.innerHTML = `<div id="below"></div><div id="right"></div>`;
    const v = new FakeVideo();
    const events: SnippetEvent[] = [];
    init({
      apiBase: api!,
      platform: "demo-portal",
      videoId,
      video: v as unknown as HTMLVideoElement,
      slots: { "below-player": "#below", "right-of-player": "#right" },
      consent: { mode: "manual", granted: true },
      storage: memoryStorage(),
      transport: { sendBeacon: () => false }, // node has no beacon -> fetch keepalive path
      onEvent: (e) => events.push(e),
    });

    v.play(3);
    await wait(500);
    const banner = document.querySelector<HTMLElement>(".advideo-banner");
    expect(banner?.dataset.advideoCampaign).toBe("tesla-model-y");
    banner!.querySelector("img")!.dispatchEvent(new Event("load"));
    await wait(500);

    const stats = await (await fetch(`${api}/campaigns/tesla-model-y/stats`, { headers: { "X-Api-Key": adminKey } })).json();
    expect(stats.impressions).toBeGreaterThanOrEqual(1);

    // Tesla segment ends at 6 s. Nike matches 6–12 s, but the platform's 60 s minimum interval
    // (server-side counter from the impression above) must block it for this viewer.
    v.at(9);
    await wait(500);
    expect(document.querySelector(".advideo-banner")).toBeNull();
    const noAd = events.filter((e) => e.type === "no-ad").pop() as Extract<SnippetEvent, { type: "no-ad" }>;
    expect(noAd.reason).toContain("Minimum interval");
    console.log(events.map((e) => e.type).join(" > "));
  });
});
