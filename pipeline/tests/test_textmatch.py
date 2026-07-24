"""Pure text-matching logic — fully testable without any ML models."""

from advideo_pipeline.textmatch import (
    brand_detections,
    keyword_detections,
    match_brands,
    match_keywords,
    normalize,
)


def test_normalize():
    assert normalize("Tesla, Model-3!") == "tesla model 3"


def test_match_brand_simple():
    hits = dict(match_brands("I love my new Tesla so much"))
    assert "Tesla" in hits
    assert "automotive" in hits["Tesla"]


def test_match_brand_multiword_and_alias():
    assert "Coca-Cola" in dict(match_brands("a can of Coca Cola"))
    assert "VW" in dict(match_brands("the new VW is out"))


def test_no_false_positive_substring():
    # "teslas" should not spuriously match because we do word-boundary matching
    assert dict(match_brands("teslas")) == {}


def test_match_keywords_multiword():
    hits = dict(match_keywords("he picked up his cell phone"))
    assert "cell phone" in hits
    assert "tech" in hits["cell phone"]


def test_brand_detections_are_entities():
    dets = brand_detections("Nike ad here", kind="audio", score=0.9)
    assert len(dets) == 1
    d = dets[0]
    assert d.entity_type == "brand"
    assert d.entity_label == "Nike"
    assert d.kind == "audio"
    assert d.category in ("sport", "footwear")


def test_keyword_detections_kind_and_category():
    dets = keyword_detections("a car on the road", kind="text", score=0.8)
    terms = {d.term for d in dets}
    assert "car" in terms
    assert all(d.kind == "text" for d in dets)
