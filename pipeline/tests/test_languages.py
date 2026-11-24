"""Language packs (schema/languages/*.json) and the R8d context corroboration rule."""

from __future__ import annotations

import json

import pytest

from advideo_pipeline import languages
from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.models import Detection
from advideo_pipeline.taxonomy import SAFETY_GROUPS
from advideo_pipeline.textmatch import match_keywords, match_safety, normalize


@pytest.fixture(autouse=True)
def _reset_languages(monkeypatch):
    for var in ("ADVIDEO_LANGUAGES", "ADVIDEO_WHISPER_LANGUAGE", "ADVIDEO_OCR_LANGUAGES"):
        monkeypatch.delenv(var, raising=False)
    languages.set_languages(None)
    yield
    languages.set_languages(None)


def _hits(text: str) -> set[str]:
    return {w for w, _ in match_safety(text)}


@pytest.mark.parametrize("code", ["en", "sk", "cs", "de", "hu", "pl"])
def test_every_pack_loads_and_is_consistent(code):
    pack = languages.load_pack(code)
    assert pack.code == code and pack.whisper and pack.ocr
    assert pack.safety_terms, "pack without a safety lexicon"
    assert all((t.subtype, t.severity) in SAFETY_GROUPS for t in pack.safety_terms)
    # terms must be written in normalized form (lowercase ascii, no diacritics) or they never match
    for t in pack.safety_terms:
        assert normalize(t.phrase.replace("*", "")) == t.phrase.replace("*", ""), t.phrase
    for local, term in pack.keywords.items():
        assert term == term.lower()


def test_packs_on_disk_match_available():
    assert {"en", "sk", "cs", "de", "hu", "pl"} <= set(languages.available_languages())


def test_default_languages_come_from_taxonomy():
    assert languages.active().codes == ("en", "sk", "cs", "de")


def test_env_overrides_languages(monkeypatch):
    monkeypatch.setenv("ADVIDEO_LANGUAGES", "sk, HU")
    assert languages.active().codes == ("sk", "hu")
    monkeypatch.setenv("ADVIDEO_LANGUAGES", "all")
    assert set(languages.active().codes) >= {"hu", "pl"}


def test_unknown_language_is_rejected():
    with pytest.raises(ValueError, match="unknown language 'xx'"):
        languages.set_languages(["sk", "xx"])


def test_english_word_does_not_fire_in_slovak_only_mode():
    text = "Mala na sebe modré rifle a krémovú bundu."  # rifle = jeans in Slovak
    languages.set_languages(["sk", "en"])
    assert "rifle" in _hits(text)
    languages.set_languages(["sk"])
    assert _hits(text) == set()
    assert "zastrelili" in _hits("Muža zastrelili pred domom.")


def test_hungarian_and_polish_lexicons():
    languages.set_languages(["hu"])
    assert _hits("Egy férfit lelőttek az utcán.") == {"lelottek"}
    assert _hits("A tűzoltóság gyakorlatot tartott.") == set()  # exclusion idiom / no hit
    languages.set_languages(["pl"])
    assert "zastrzelony" in _hits("Mężczyzna został zastrzelony na ulicy.")
    assert "zginal" in _hits("Kierowca zginął na miejscu.")  # ł -> l normalization
    assert _hits("Przyjechała straż pożarna.") == set()


def test_polish_l_normalization():
    assert normalize("Zginął, łódź") == "zginal lodz"


def test_local_keywords_map_to_taxonomy_terms():
    languages.set_languages(["sk"])
    assert "car" in dict(match_keywords("Nové elektrické auto má dojazd 500 km."))
    languages.set_languages(["pl"])
    assert "car" in dict(match_keywords("Nowy samochód elektryczny"))
    languages.set_languages(["en"])
    assert "car" not in dict(match_keywords("Nowy samochód elektryczny"))


def test_whisper_language(monkeypatch):
    languages.set_languages(["hu"])
    assert languages.whisper_language() == "hu"
    languages.set_languages(["sk", "cs"])
    assert languages.whisper_language() is None  # several -> auto-detect
    monkeypatch.setenv("ADVIDEO_WHISPER_LANGUAGE", "pl")
    assert languages.whisper_language() == "pl"
    monkeypatch.setenv("ADVIDEO_WHISPER_LANGUAGE", "auto")
    languages.set_languages(["hu"])
    assert languages.whisper_language() is None


def test_ocr_languages(monkeypatch):
    languages.set_languages(["sk", "hu"])
    assert languages.ocr_languages() == ["en", "sk", "hu"]
    monkeypatch.setenv("ADVIDEO_OCR_LANGUAGES", "pl")
    assert languages.ocr_languages() == ["pl"]


def test_semantic_prototypes_follow_languages():
    languages.set_languages(["hu"])
    s = languages.active()
    assert any("lelőttek" in x for x in s.semantic_sensitive["violence"])
    languages.set_languages(["de"])  # no prototypes -> English fallback
    s = languages.active()
    assert s.semantic_neutral and all(x.isascii() for x in s.semantic_neutral)


def test_legacy_taxonomy_constants_still_work():
    from advideo_pipeline import taxonomy

    assert any(t.phrase == "zabil*" for t in taxonomy.SAFETY_TERMS)
    assert "sensitive" in taxonomy.SEMANTIC_SAFETY and taxonomy.SEMANTIC_SAFETY["thresholds"]["high"] > 0


def test_taxonomy_has_no_inline_terms_anymore():
    from advideo_pipeline.taxonomy import TAXONOMY_PATH

    doc = json.loads(TAXONOMY_PATH.read_text(encoding="utf-8"))
    assert all("terms" not in g for g in doc["safetyTerms"]["groups"])
    assert "sensitive" not in doc["semanticSafety"]


# --------------------------------------------------------------------------- R8d context rule

def _det(term: str, severity: str = "medium") -> Detection:
    return Detection(term=f"speech: {term}", score=0.8, kind="safety_speech", category="violence",
                     categories=["violence", "sensitive"], severity=severity)


def _sensitive(segments) -> list[bool]:
    return ["sensitive" in s.categories for s in segments]


def test_medium_hits_in_neighbouring_windows_corroborate():
    dets = [(1, 2, _det("zraneny")), (8, 9, _det("smrt"))]  # windows 0 and 1
    assert _sensitive(aggregate_segments(dets, 18, text_safety_context=1)) == [True, True, False]
    assert _sensitive(aggregate_segments(dets, 18, text_safety_context=0)) == [False, False, False]


def test_corroboration_does_not_reach_two_windows_away():
    dets = [(1, 2, _det("zraneny")), (13, 14, _det("smrt"))]  # windows 0 and 2
    assert _sensitive(aggregate_segments(dets, 18, text_safety_context=1)) == [False, False, False]


def test_semantic_hits_never_corroborate_each_other():
    dets = [(1, 2, _det("~violence")), (8, 9, _det("~terror")), (9, 10, _det("~death"))]
    assert _sensitive(aggregate_segments(dets, 18)) == [False, False, False]
    dets.append((10, 11, _det("napadol")))  # one lexicon word confirms the semantic ones around it
    assert _sensitive(aggregate_segments(dets, 18)) == [True, True, False]


def test_window_without_own_hit_is_not_flagged_by_neighbours():
    dets = [(1, 2, _det("zraneny")), (2, 3, _det("smrt"))]
    assert _sensitive(aggregate_segments(dets, 18)) == [True, False, False]


# --------------------------------------------------------------------------- R8f sport context

def _speech(segments, duration=30.0):
    from advideo_pipeline.textmatch import context_detections, safety_detections

    dets = []
    for start, end, text in segments:
        for d in safety_detections(text, "audio", 0.8) + context_detections(text, "audio", 0.8):
            dets.append((start, end, d))
    return aggregate_segments(dets, duration)


def test_sport_words_are_not_sensitive_in_a_match_report():
    languages.set_languages(["sk"])
    segs = _speech([(0, 5, "V zápase NBA padlo veľa gólov."), (6, 11, "Útočník sa zranený vrátil, streľba z diaľky.")])
    assert not any("sensitive" in s.categories for s in segs)
    # the same words without sport context stay sensitive
    segs = _speech([(6, 11, "Útočník sa zranený vrátil, streľba z diaľky.")])
    assert any("sensitive" in s.categories for s in segs)


def test_sport_context_needs_two_markers_and_keeps_unambiguous_words():
    languages.set_languages(["sk"])
    one_marker = _speech([(0, 5, "Po zápase"), (6, 11, "útočník a zranený muž, streľba")])
    assert any("sensitive" in s.categories for s in one_marker)
    killed = _speech([(0, 5, "Zápas NBA, gól."), (6, 11, "Fanúšika zabili pred štadiónom.")])
    assert any("sensitive" in s.categories for s in killed)  # "zabili" is not sport-ambiguous


def test_visual_match_counts_as_sport_marker():
    languages.set_languages(["sk"])
    from advideo_pipeline.textmatch import context_detections, safety_detections

    dets = [(t, t, Detection(term="playing football", score=0.9, kind="action", categories=["sport"])) for t in (1, 2)]
    dets += [(1, 2, Detection(term="a stadium", score=0.9, kind="scene", categories=["sport"]))]
    dets += [(6, 11, d) for d in safety_detections("útočník zranený, streľba", "audio", 0.8)]
    assert not any("sensitive" in s.categories for s in aggregate_segments(dets, 18))


def test_sport_ambiguous_terms_must_exist_in_lexicon():
    for code in languages.available_languages():
        pack = languages.load_pack(code)  # raises when sportContext.ambiguous lists unknown terms
        assert all(normalize(m.replace("*", "")) == m.replace("*", "") for m in pack.sport_markers)


def test_context_detections_do_not_reach_the_map():
    languages.set_languages(["sk"])
    seg = _speech([(0, 5, "Zápas NBA, gól.")])[0]
    assert not seg.keywords and not seg.categories
