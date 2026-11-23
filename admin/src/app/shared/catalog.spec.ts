import { parseCatalog } from './catalog';

describe('parseCatalog', () => {
  it('parses CSV with header, comments, quotes and optional columns', () => {
    const r = parseCatalog(
      'videoId,videoUri,analyzer,track\n# comment\n\nv1,https://cdn/x.mp4\n"v2","https://cdn/a,b.m3u8",mock,whisper\n',
    );
    expect(r.errors).toEqual([]);
    expect(r.items).toEqual([
      { videoId: 'v1', videoUri: 'https://cdn/x.mp4' },
      { videoId: 'v2', videoUri: 'https://cdn/a,b.m3u8', analyzer: 'mock', track: 'whisper' },
    ]);
  });

  it('detects semicolon and tab delimiters', () => {
    expect(parseCatalog('a;https://cdn/a.mp4').items[0].videoUri).toBe('https://cdn/a.mp4');
    expect(parseCatalog('a\thttps://cdn/a,1.mp4').items[0].videoUri).toBe('https://cdn/a,1.mp4');
  });

  it('reports lines without a URI', () => {
    const r = parseCatalog('ok,https://cdn/ok.mp4\nbroken');
    expect(r.items.length).toBe(1);
    expect(r.errors).toEqual(['line 2: expected videoId,videoUri']);
  });

  it('parses JSON arrays and {items}', () => {
    expect(parseCatalog('[{"videoId":"a","videoUri":"/a.mp4"}]').items).toEqual([{ videoId: 'a', videoUri: '/a.mp4' }]);
    expect(parseCatalog('{"items":[{"id":"b","url":"https://cdn/b.mp4","analyzer":"mock"}]}').items).toEqual([
      { videoId: 'b', videoUri: 'https://cdn/b.mp4', analyzer: 'mock' },
    ]);
    expect(parseCatalog('{"x":1}').errors.length).toBe(1);
    expect(parseCatalog('[{"videoId":"a"}]').errors[0]).toContain('item 1');
  });

  it('returns nothing for empty input', () => {
    expect(parseCatalog('  ')).toEqual({ items: [], errors: [] });
  });
});
