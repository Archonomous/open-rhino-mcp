// Fetches official Rhino 7 command help pages and turns them into readable text.

export type FetchLike = (url: string) => Promise<{ ok: boolean; status: number; text(): Promise<string> }>;

export const DOCS_BASE = "https://docs.mcneel.com/rhino/7/help/en-us/commands/";
const MAX_CHARS = 12_000;

const cache = new Map<string, string>();

export function commandHelpUrl(command: string): string {
  const name = command.trim().replace(/^[-_!']+/, "").replace(/^_/, "").toLowerCase();
  return `${DOCS_BASE}${encodeURIComponent(name)}.htm`;
}

const ENTITIES: Record<string, string> = {
  nbsp: " ", amp: "&", lt: "<", gt: ">", quot: '"', apos: "'", "#39": "'", mdash: "—", ndash: "–",
  hellip: "…", rsquo: "’", lsquo: "‘", rdquo: "”", ldquo: "“", deg: "°", times: "×", bull: "•", copy: "©",
};

function decodeEntities(s: string): string {
  return s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+\d*);/gi, (m, e: string) => {
    if (e[0] === "#") {
      const code = e[1] === "x" || e[1] === "X" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return Number.isFinite(code) ? String.fromCodePoint(code) : m;
    }
    return ENTITIES[e.toLowerCase()] ?? m;
  });
}

/** Minimal HTML → markdown-ish text. Good enough for McNeel help pages. */
export function htmlToText(html: string): string {
  let s = html;
  // Prefer the main content if marked.
  const body = s.match(/<body[^>]*>([\s\S]*)<\/body>/i);
  if (body) s = body[1];
  s = s.replace(/<(script|style|noscript|head|nav|footer|iframe|svg|form)[^>]*>[\s\S]*?<\/\1>/gi, "");
  s = s.replace(/<!--[\s\S]*?-->/g, "");
  s = s.replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, (_m, alt: string) => (alt ? `[${alt}]` : ""));
  s = s.replace(/<h([1-6])[^>]*>([\s\S]*?)<\/h\1>/gi, (_m, n: string, t: string) => `\n\n${"#".repeat(Number(n))} ${t}\n\n`);
  s = s.replace(/<(tr)[^>]*>/gi, "\n");
  s = s.replace(/<\/(td|th)>/gi, " | ");
  s = s.replace(/<li[^>]*>/gi, "\n- ");
  s = s.replace(/<br\s*\/?>/gi, "\n");
  s = s.replace(/<\/(p|div|table|ul|ol|dl|dt|dd|section|article|blockquote|pre)>/gi, "\n\n");
  s = s.replace(/<(b|strong)[^>]*>([\s\S]*?)<\/\1>/gi, "**$2**");
  s = s.replace(/<[^>]+>/g, "");
  s = decodeEntities(s);
  s = s
    .split("\n")
    .map((l) => l.replace(/[ \t ]+/g, " ").replace(/\s*\|\s*$/, "").trim())
    .join("\n");
  s = s.replace(/\n{3,}/g, "\n\n").trim();
  // Drop leading boilerplate before the first heading if there is one.
  const firstHeading = s.search(/^#{1,3} /m);
  if (firstHeading > 0 && firstHeading < 2000) s = s.slice(firstHeading);
  return s;
}

export class CommandHelpError extends Error {}

export async function fetchCommandHelp(command: string, fetchImpl: FetchLike): Promise<string> {
  const url = commandHelpUrl(command);
  const hit = cache.get(url);
  if (hit) return hit;
  let res;
  try {
    res = await fetchImpl(url);
  } catch (e) {
    throw new CommandHelpError(
      `Couldn't reach the Rhino help site (${(e as Error).message}). Try rhino_python_help or rhino_run_command "_Help" instead.`,
    );
  }
  if (res.status === 404) {
    throw new CommandHelpError(
      `No Rhino 7 help page for "${command}". Check the exact command name with rhino_list_commands (use a filter, e.g. { filter: "${command.slice(0, 4)}" }).`,
    );
  }
  if (!res.ok) throw new CommandHelpError(`The Rhino help site answered ${res.status} for ${url}.`);
  let text = htmlToText(await res.text());
  if (text.length > MAX_CHARS) text = text.slice(0, MAX_CHARS) + `\n\n[…truncated; full page: ${url}]`;
  text = `Source: ${url}\n\n${text}`;
  cache.set(url, text);
  return text;
}

export function clearHelpCache(): void {
  cache.clear();
}
