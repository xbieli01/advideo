import { BulkVideoItem } from '../core/models';

export interface ParsedCatalog {
  items: BulkVideoItem[];
  errors: string[];
}

const HEADER = /^\s*"?video_?id"?\s*[,;\t]/i;

/**
 * Parses a catalogue for bulk import.
 * - JSON: an array of {videoId, videoUri, analyzer?, track?} or {items: [...]}
 * - CSV/TSV (comma, semicolon or tab): videoId,videoUri[,analyzer[,track]] per line;
 *   optional header line, empty lines and lines starting with # are ignored, values may be quoted.
 */
export function parseCatalog(text: string): ParsedCatalog {
  const trimmed = (text ?? '').trim();
  if (!trimmed) return { items: [], errors: [] };
  if (trimmed.startsWith('[') || trimmed.startsWith('{')) return parseJson(trimmed);
  return parseDelimited(trimmed);
}

function parseJson(text: string): ParsedCatalog {
  try {
    const data = JSON.parse(text) as unknown;
    const arr = Array.isArray(data) ? data : (data as { items?: unknown }).items;
    if (!Array.isArray(arr)) return { items: [], errors: ['JSON must be an array or an object with "items"'] };
    const items: BulkVideoItem[] = [];
    const errors: string[] = [];
    arr.forEach((raw, i) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      const videoId = str(o['videoId'] ?? o['video_id'] ?? o['id']);
      const videoUri = str(o['videoUri'] ?? o['video_uri'] ?? o['uri'] ?? o['url']);
      if (!videoId || !videoUri) errors.push(`item ${i + 1}: videoId and videoUri are required`);
      else items.push(compact({ videoId, videoUri, analyzer: str(o['analyzer']), track: str(o['track']) }));
    });
    return { items, errors };
  } catch (e) {
    return { items: [], errors: [`invalid JSON: ${(e as Error).message}`] };
  }
}

function parseDelimited(text: string): ParsedCatalog {
  const lines = text.split(/\r?\n/);
  const delimiter = detectDelimiter(lines.find((l) => l.trim() && !l.trim().startsWith('#')) ?? '');
  const items: BulkVideoItem[] = [];
  const errors: string[] = [];
  lines.forEach((line, i) => {
    const t = line.trim();
    if (!t || t.startsWith('#')) return;
    if (i === 0 && HEADER.test(t)) return;
    const [videoId, videoUri, analyzer, track] = splitLine(t, delimiter).map((v) => v.trim());
    if (!videoId || !videoUri) errors.push(`line ${i + 1}: expected videoId${delimiter}videoUri`);
    else items.push(compact({ videoId, videoUri, analyzer: analyzer || null, track: track || null }));
  });
  return { items, errors };
}

function detectDelimiter(line: string): string {
  if (line.includes('\t')) return '\t';
  if (line.includes(';') && !line.includes(',')) return ';';
  return ',';
}

/** Splits one line, honouring double quotes ("a,b" stays one value, "" is an escaped quote). */
function splitLine(line: string, delimiter: string): string[] {
  const out: string[] = [];
  let cur = '';
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (quoted) {
      if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; }
      else if (ch === '"') quoted = false;
      else cur += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === delimiter) { out.push(cur); cur = ''; }
    else cur += ch;
  }
  out.push(cur);
  return out;
}

function str(v: unknown): string | null {
  return typeof v === 'string' && v.trim() ? v.trim() : null;
}

function compact(item: BulkVideoItem): BulkVideoItem {
  const out: BulkVideoItem = { videoId: item.videoId, videoUri: item.videoUri };
  if (item.analyzer) out.analyzer = item.analyzer;
  if (item.track) out.track = item.track;
  return out;
}
