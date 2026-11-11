const KEY = "advideo_vid";

type Store = Pick<Storage, "getItem" | "setItem" | "removeItem">;

function randomId(): string {
  const c = (globalThis as { crypto?: Crypto }).crypto;
  if (c?.randomUUID) return c.randomUUID();
  const b = new Uint8Array(16);
  if (c?.getRandomValues) c.getRandomValues(b);
  else for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  return Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
}

/**
 * Anonymous first-party viewer id. Created only with consent and removed when consent is withdrawn.
 * The server stores only an HMAC of it.
 */
export class ViewerId {
  constructor(private readonly store: Store | null) {}

  get(allowed: boolean): string | null {
    if (!this.store) return null;
    try {
      if (!allowed) {
        this.store.removeItem(KEY);
        return null;
      }
      let id = this.store.getItem(KEY);
      if (!id) {
        id = randomId();
        this.store.setItem(KEY, id);
      }
      return id;
    } catch {
      return null; // storage blocked (Safari private mode, iframes) -> anonymous
    }
  }
}
