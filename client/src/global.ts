/** IIFE entry: exposes window.Advideo for <script src="advideo.min.js"> embeds. */
import { AdvideoPlayer, init, version } from "./index";

const api = { init, version, AdvideoPlayer };
(globalThis as unknown as { Advideo: typeof api }).Advideo = api;
