from advideo_pipeline import taxonomy as t


def test_taxonomy_loaded_from_shared_json():
    assert "automotive" in t.CATEGORIES
    assert t.categories_for("Car") == ["automotive"]
    assert t.BRAND_LEXICON["coca cola"] == ("Coca-Cola", ["beverage"])
    assert ("driving", "action") in t.CLIP_SCENE_LABELS


def test_all_referenced_categories_are_declared():
    declared = set(t.CATEGORIES)
    used = {c for cats in t.TERM_CATEGORIES.values() for c in cats}
    used |= {c for _, cats in t.BRAND_LEXICON.values() for c in cats}
    assert used <= declared
