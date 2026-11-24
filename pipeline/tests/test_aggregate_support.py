from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.models import Detection


def det(term, score, cats=(), kind="action"):
    return Detection(term=term, score=score, kind=kind, category=cats[0] if cats else None, categories=list(cats))


def test_single_weak_frame_is_dropped_but_repeated_or_strong_is_kept():
    timed = [
        (0.0, 0.0, det("running", 0.43, ["sport"])),          # one weak frame -> noise
        (1.0, 1.0, det("cup", 0.6, ["beverage", "food"], "object")),
        (2.0, 2.0, det("cup", 0.7, ["beverage", "food"], "object")),  # seen twice -> kept
        (3.0, 3.0, det("cooking", 0.95, ["food"])),            # once but strong -> kept
    ]
    seg = aggregate_segments(timed, 6.0, window_sec=6.0, min_support=2, strong_score=0.8)[0]
    terms = {k["term"] for k in seg.keywords}
    assert terms == {"cup", "cooking"}
    assert seg.categories == ["beverage", "food"]


def test_all_categories_of_a_term_are_used():
    seg = aggregate_segments([(0.0, 0.0, det("bus", 0.9, ["automotive", "transport"], "object"))], 6.0)[0]
    assert seg.categories == ["automotive", "transport"]


def test_range_detections_bypass_support_filter():
    audio = Detection(term="Coca-Cola", score=0.5, kind="audio", entity_type="brand", entity_label="Coca-Cola")
    seg = aggregate_segments([(1.0, 4.0, audio)], 6.0, min_support=3)[0]
    assert seg.entities and seg.entities[0]["label"] == "Coca-Cola"


def test_min_support_one_keeps_everything():
    seg = aggregate_segments([(0.0, 0.0, det("running", 0.2, ["sport"]))], 6.0, min_support=1)[0]
    assert seg.categories == ["sport"]


def test_pipeline_does_not_close_instances_it_did_not_create():
    from advideo_pipeline.analyzers import MockAnalyzer
    from advideo_pipeline.pipeline import analyze_video

    closed = []

    class Tracked(MockAnalyzer):
        def close(self):
            closed.append(True)

    a = Tracked()
    analyze_video("x.mp4", analyzer=a, duration_sec=6.0)
    assert closed == []


def test_ocr_text_brand_seen_once_is_kept():
    brand = Detection(term="Coca-Cola", score=0.5, kind="text", entity_type="brand", entity_label="Coca-Cola",
                      category="beverage", categories=["beverage"])
    seg = aggregate_segments([(2.0, 2.0, brand)], 6.0, min_support=2)[0]
    assert seg.entities[0]["label"] == "Coca-Cola" and seg.categories == ["beverage"]


def test_ocr_interval_resets_for_a_new_video():
    import numpy as np

    from advideo_pipeline.analyzers.ocr import OcrBrandAnalyzer
    from advideo_pipeline.models import Frame

    calls = []
    a = OcrBrandAnalyzer.__new__(OcrBrandAnalyzer)  # skip model loading
    a.min_confidence, a.interval_sec, a.max_side, a._next_t, a._last_t = 0.4, 2.0, 0, 0.0, -1.0

    class Reader:
        def readtext(self, img, **kw):
            calls.append(kw)
            return []

    a._reader = Reader()
    img = np.zeros((10, 10, 3), dtype=np.uint8)
    for t in (0, 1, 2, 3, 4, 0, 1, 2):  # second video starts at t=0
        a.analyze(Frame(time_sec=float(t), image=img))
    assert len(calls) == 3 + 2


def _ocr_with_fake_reader(static_diff=4.0):
    from advideo_pipeline.analyzers.ocr import OcrBrandAnalyzer

    a = OcrBrandAnalyzer.__new__(OcrBrandAnalyzer)
    a.min_confidence, a.interval_sec, a.max_side, a._next_t, a._last_t = 0.4, 2.0, 0, 0.0, -1.0
    a.detect_scale, a.static_diff, a._last_thumb, a._last_result = 0.6, static_diff, None, []
    a.stats = {"ocr": 0, "reused": 0}
    calls = []

    class Reader:
        def readtext(self, img, **kw):
            calls.append(kw)
            return [(None, "Tesla Model 3", 0.9)]

    a._reader = Reader()
    return a, calls


def test_ocr_reuses_result_when_frame_is_static():
    import numpy as np

    from advideo_pipeline.models import Frame

    a, calls = _ocr_with_fake_reader()
    still = np.full((90, 160, 3), 120, dtype=np.uint8)
    moved = still.copy()
    moved[:, :80] = 10  # half the picture changed
    outs = [a.analyze(Frame(time_sec=float(t), image=im)) for t, im in ((0, still), (2, still), (4, moved), (6, moved))]
    assert len(calls) == 2 and a.stats == {"ocr": 2, "reused": 2}
    assert calls[0] == {"mag_ratio": 0.6}
    assert all(any(d.entity_label == "Tesla" for d in o) for o in outs)  # reused result still yields the brand


def test_ocr_reuse_can_be_disabled():
    import numpy as np

    from advideo_pipeline.models import Frame

    a, calls = _ocr_with_fake_reader(static_diff=0)
    img = np.zeros((90, 160, 3), dtype=np.uint8)
    for t in (0, 2, 4):
        a.analyze(Frame(time_sec=float(t), image=img))
    assert len(calls) == 3


def test_logo_needs_three_frames_unless_near_certain():
    def logo(t, s):
        return (t, t, Detection(term="Nike", score=s, kind="logo", entity_type="brand", entity_label="Nike",
                                category="footwear", categories=["footwear", "sport"]))
    two = aggregate_segments([logo(0.0, 0.9), logo(1.0, 0.9)], 6.0, min_support=2)[0]
    assert two.entities == []                       # 2 frames are enough for objects, not for logos
    three = aggregate_segments([logo(0.0, 0.8), logo(1.0, 0.8), logo(2.0, 0.8)], 6.0, min_support=2)[0]
    assert three.entities[0]["label"] == "Nike"
    certain = aggregate_segments([logo(0.0, 0.99)], 6.0, min_support=2)[0]
    assert certain.entities[0]["label"] == "Nike"


def test_safety_label_needs_two_frames_even_when_strong():
    def unsafe(t, s):
        return (t, t, Detection(term="a car crash or traffic accident", score=s, kind="safety",
                                category="sensitive", categories=["sensitive", "accident"]))
    one = aggregate_segments([unsafe(0.0, 0.99)], 6.0, min_support=2)[0]
    assert "sensitive" not in one.categories
    two = aggregate_segments([unsafe(0.0, 0.75), unsafe(1.0, 0.8)], 6.0, min_support=2)[0]
    assert two.categories == ["accident", "sensitive"]
    assert two.signals["safety"] == ["a car crash or traffic accident"]
