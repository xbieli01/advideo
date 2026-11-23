"""R8b: brand safety from speech / on-screen text."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.analyzers.base import TrackAnalyzer
from advideo_pipeline.pipeline import analyze_video
from advideo_pipeline.textmatch import match_safety, news_detections, normalize, safety_detections

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "eval"))
from run_text_safety_eval import CASES, MIN_PRECISION, MIN_RECALL, evaluate  # noqa: E402


def _segment(text: str, kind: str = "audio"):
    dets = safety_detections(text, kind, 0.9) + news_detections(text, kind, 0.9)
    return aggregate_segments([(0.0, 5.0, d) for d in dets], duration_sec=6.0, window_sec=6.0, min_support=2)[0]


def test_normalize_strips_diacritics():
    assert normalize("Zabitých, Überschwemmung, Straße!") == "zabitych uberschwemmung strasse"


def test_prefix_terms_match_inflected_forms():
    words = {w for w, _ in match_safety("Polícia našla zavraždenú ženu, vraždu vyšetruje.")}
    assert {"zavrazdenu", "vrazdu"} <= words


def test_longest_match_wins():
    hits = dict(match_safety("a car crash on the highway"))
    assert "car crash" in hits and "crash" not in hits
    assert hits["car crash"].severity == "high"


def test_exclusion_idioms():
    assert match_safety("Na dovolenke sme zabili čas kartami.") == []
    assert match_safety("Take our crash course in photography") == []


def test_single_medium_hit_does_not_mark_window():
    seg = _segment("The team will attack the title next season.")
    assert "sensitive" not in seg.categories


def test_two_medium_hits_mark_window():
    seg = _segment("Soldiers moved in as the war entered its second week.")
    assert {"sensitive", "war"} <= set(seg.categories)
    assert any(s.startswith("speech: ") for s in seg.signals["safety"])


def test_high_hit_marks_window_alone():
    assert "sensitive" in _segment("Two people were killed.").categories


def test_profanity_is_categorized_but_not_sensitive():
    seg = _segment("What the fuck is this")
    assert "profanity" in seg.categories and "sensitive" not in seg.categories


def test_non_sensitive_hits_do_not_corroborate_sensitive_ones():
    seg = _segment("The documentary follows a cocaine dealer and his overdose victims.")
    assert "drugs" in seg.categories and "sensitive" not in seg.categories


def test_news_cue_and_no_keywords_from_cues():
    seg = _segment("Breaking news: hostages are being held, police said.")
    assert {"news", "terror", "sensitive"} <= set(seg.categories)
    assert seg.signals["news"]
    assert not any(k["term"].startswith(("speech:", "on-screen:")) for k in seg.keywords)


def test_ocr_source_prefix():
    seg = _segment("BREAKING NEWS: TERROR ATTACK IN CITY CENTRE", kind="text")
    assert any(s.startswith("on-screen: ") for s in seg.signals["safety"])


class _FakeSpeech(TrackAnalyzer):
    name = "fake-speech"

    def analyze_track(self, video_path, duration_sec):
        out = []
        for start, end, text in [(0.5, 4.0, "Welcome to our cooking show."),
                                 (7.0, 11.0, "Police said three people were killed in the shooting.")]:
            out += [(start, end, d) for d in safety_detections(text, "audio", 0.9)]
        return out


def test_speech_safety_lands_in_its_window():
    vmap = analyze_video("x.mp4", analyzer=None, track=[_FakeSpeech()], duration_sec=12.0)
    first, second = vmap.segments
    assert "sensitive" not in first.categories
    assert {"sensitive", "violence"} <= set(second.categories)


def test_tuning_set_quality():
    res = evaluate(CASES)
    assert res["total"]["precision"] >= MIN_PRECISION, res["failures"]
    assert res["total"]["recall"] >= MIN_RECALL, res["failures"]


def test_holdout_precision_guard():
    """Held-out set is never tuned on; we only guard precision (a false block costs revenue).
    Recall on unseen phrasing is reported by the eval script (≈0.55 for the lexicon approach)."""
    res = evaluate(CASES.with_name("text_safety_holdout.json"))
    assert res["total"]["precision"] >= 0.9, res["failures"]
