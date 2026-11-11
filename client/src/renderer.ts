import type { AdDecision } from "./types";

export interface RenderOptions {
  adLabel: string;
  closable: boolean;
  onClick: () => void;
  onClose: () => void;
  onLoad: () => void;
}

const STYLE_ID = "advideo-style";
const CSS = `
.advideo-banner{position:relative;display:inline-block;max-width:100%;line-height:0;font-family:system-ui,sans-serif}
.advideo-banner a{display:block}
.advideo-banner img{display:block;max-width:100%;height:auto;border:0}
.advideo-label{position:absolute;top:0;left:0;padding:1px 6px;background:rgba(0,0,0,.6);color:#fff;font-size:11px;line-height:16px;border-bottom-right-radius:4px;pointer-events:none}
.advideo-close{position:absolute;top:0;right:0;width:20px;height:20px;border:0;padding:0;background:rgba(0,0,0,.6);color:#fff;font-size:14px;line-height:20px;cursor:pointer;border-bottom-left-radius:4px}
.advideo-close:focus-visible{outline:2px solid #fff;outline-offset:-2px}
`;

function ensureStyle(doc: Document): void {
  if (doc.getElementById(STYLE_ID)) return;
  const s = doc.createElement("style");
  s.id = STYLE_ID;
  s.textContent = CSS;
  doc.head.appendChild(s);
}

/** Renders the banner into the slot container; returns the banner element. Uses no innerHTML (XSS-safe). */
export function renderBanner(container: HTMLElement, d: AdDecision, o: RenderOptions): HTMLElement {
  const doc = container.ownerDocument;
  ensureStyle(doc);
  const c = d.ad.creative;

  const wrap = doc.createElement("div");
  wrap.className = "advideo-banner";
  wrap.dataset.advideoDecision = d.decisionId;
  wrap.dataset.advideoCampaign = d.ad.campaignId;

  const a = doc.createElement("a");
  a.href = safeUrl(c.clickUrl);
  a.target = "_blank";
  a.rel = "noopener noreferrer sponsored";
  a.addEventListener("click", () => o.onClick());

  const img = doc.createElement("img");
  img.src = safeUrl(c.url);
  img.width = c.width;
  img.height = c.height;
  img.alt = c.altText ?? o.adLabel;
  img.decoding = "async";
  img.addEventListener("load", () => o.onLoad(), { once: true });
  a.appendChild(img);

  const label = doc.createElement("span");
  label.className = "advideo-label";
  label.textContent = o.adLabel;

  wrap.append(a, label);

  if (o.closable) {
    const btn = doc.createElement("button");
    btn.type = "button";
    btn.className = "advideo-close";
    btn.setAttribute("aria-label", "Zavrieť reklamu");
    btn.textContent = "×";
    btn.addEventListener("click", (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      o.onClose();
    });
    wrap.appendChild(btn);
  }

  container.replaceChildren(wrap);
  return wrap;
}

/** Only http(s) URLs from the ad server are allowed (blocks javascript:, data: …). */
export function safeUrl(u: string): string {
  try {
    const url = new URL(u, "https://invalid.local");
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : "about:blank";
  } catch {
    return "about:blank";
  }
}
