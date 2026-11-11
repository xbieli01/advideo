/**
 * IAB/MRC display viewability: at least 50 % of the banner in view for at least 1 continuous second.
 * Also requires the page to be visible (not a background tab).
 */
export function watchViewable(el: Element, onViewable: () => void, doc: Document = document): () => void {
  const IO = (globalThis as { IntersectionObserver?: typeof IntersectionObserver }).IntersectionObserver;
  if (!IO) return () => undefined; // cannot measure -> never report viewable
  let timer: ReturnType<typeof setTimeout> | null = null;
  let inView = false;
  let done = false;

  const arm = () => {
    if (done || timer || !inView || doc.visibilityState === "hidden") return;
    timer = setTimeout(() => {
      timer = null;
      if (inView && doc.visibilityState !== "hidden" && !done) {
        done = true;
        cleanup();
        onViewable();
      }
    }, 1000);
  };
  const disarm = () => {
    if (timer) clearTimeout(timer);
    timer = null;
  };

  const io = new IO(
    (entries) => {
      for (const e of entries) inView = e.isIntersecting && e.intersectionRatio >= 0.5;
      if (inView) arm();
      else disarm();
    },
    { threshold: [0, 0.5, 1] },
  );
  const onVis = () => (doc.visibilityState === "hidden" ? disarm() : arm());
  io.observe(el);
  doc.addEventListener("visibilitychange", onVis);

  function cleanup() {
    disarm();
    io.disconnect();
    doc.removeEventListener("visibilitychange", onVis);
  }
  return cleanup;
}
