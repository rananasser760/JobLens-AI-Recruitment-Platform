export interface ParsedAiFeedback {
  review: string;
  strengths: string[];
  weaknesses: string[];
}

function toStringList(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value
    .map((item) => (typeof item === 'string' ? item : item == null ? '' : String(item)))
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

export function parseAiFeedback(raw: string | null | undefined): ParsedAiFeedback | null {
  const text = (raw ?? '').trim();
  if (!text) return null;

  if (text.startsWith('{')) {
    try {
      const data = JSON.parse(text) as Record<string, unknown>;
      const review = typeof data['review'] === 'string' ? data['review'].trim() : '';
      const strengths = toStringList(data['strengths']);
      const weaknesses = toStringList(data['weaknesses']);
      if (!review && strengths.length === 0 && weaknesses.length === 0) return null;
      return { review, strengths, weaknesses };
    } catch {
      return { review: text, strengths: [], weaknesses: [] };
    }
  }

  return { review: text, strengths: [], weaknesses: [] };
}
