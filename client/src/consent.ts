import type { ConsentMode } from "./types";

type TcfData = { gdprApplies?: boolean; purpose?: { consents?: Record<string, boolean> }; eventStatus?: string };
type TcfApi = (cmd: string, version: number, cb: (data: TcfData, ok: boolean) => void) => void;

/**
 * Tracks whether the snippet may store an anonymous viewer id on the device
 * (ePrivacy: storage needs consent, TCF purpose 1).
 */
export class ConsentState {
  private granted: boolean;
  private listeners: ((granted: boolean) => void)[] = [];

  constructor(private readonly mode: ConsentMode, initial = false, win: Window = window) {
    this.granted = mode === "manual" ? initial : false;
    if (mode === "tcf") this.listenTcf(win);
  }

  get allowed(): boolean {
    return this.mode !== "none" && this.granted;
  }

  set(granted: boolean): void {
    if (this.mode === "none" || granted === this.granted) return;
    this.granted = granted;
    for (const l of this.listeners) l(granted);
  }

  onChange(l: (granted: boolean) => void): void {
    this.listeners.push(l);
  }

  private listenTcf(win: Window): void {
    const api = (win as unknown as { __tcfapi?: TcfApi }).__tcfapi;
    if (typeof api !== "function") return; // no CMP on page -> no consent
    api("addEventListener", 2, (data, ok) => {
      if (!ok) return;
      if (data.eventStatus && data.eventStatus !== "tcloaded" && data.eventStatus !== "useractioncomplete") return;
      const purpose1 = data.gdprApplies === false || data.purpose?.consents?.["1"] === true;
      this.set(purpose1);
    });
  }
}
