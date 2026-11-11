import { AdvideoPlayer } from "./snippet";
import type { AdvideoOptions } from "./types";

export type * from "./types";
export { AdvideoPlayer };

/** Attach advideo to a player. Returns the instance (setConsent / setVideo / destroy). */
export function init(options: AdvideoOptions): AdvideoPlayer {
  return new AdvideoPlayer(options);
}

export const version = "0.1.0";
