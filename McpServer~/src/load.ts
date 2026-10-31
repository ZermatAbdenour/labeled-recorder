import { promises as fs } from "node:fs";
import path from "node:path";
import type {
  LabelChapter,
  LabelDocument,
  LabelEvent,
  LabelMarker,
  LabelSignal,
  LabelSpan,
  LabelValue,
  LoadedDocument,
} from "./types.js";

export const FINAL_SUFFIX = ".labels.json";
export const STREAM_SUFFIX = ".labels.jsonl";

const SKIP_DIRECTORIES = new Set([
  "node_modules",
  ".git",
  ".svn",
  "Library",
  "Temp",
  "obj",
  "Logs",
  "dist",
  "build",
  ".vs",
  ".idea",
  "__pycache__",
]);

/** Where to look for recordings, and how deep. */
export interface SearchOptions {
  roots: string[];
  maxDepth?: number;
  limit?: number;
}

/** One label file found on disk, with just enough read to list it. */
export interface RecordingSummary {
  labelPath: string;
  mediaPath: string | null;
  mediaExists: boolean;
  recovered: boolean;
  recorder?: string;
  scene?: string;
  startedAt?: string;
  duration: number;
  frameRate: number;
  frameCount: number;
  resolution?: string;
  labels: number;
  markers: number;
  events: number;
  chapters: string[];
  dropped: number;
  modified: string;
}

/**
 * Reads the roots the server should search. Set `LABELED_RECORDER_ROOTS` to one or more directories, separated
 * by the platform path separator; extra command-line arguments are appended. Falls back to the working
 * directory.
 */
export function resolveRoots(argv: string[] = []): string[] {
  const fromEnv = (process.env.LABELED_RECORDER_ROOTS ?? "")
    .split(path.delimiter)
    .map((entry) => entry.trim())
    .filter(Boolean);

  const roots = [...fromEnv, ...argv.map((entry) => entry.trim()).filter(Boolean)];
  const resolved = (roots.length > 0 ? roots : [process.cwd()]).map((root) => path.resolve(root));

  return [...new Set(resolved)];
}

/** Walks the roots and returns every label file found, newest first. */
export async function findLabelFiles(options: SearchOptions): Promise<string[]> {
  const maxDepth = options.maxDepth ?? 8;
  const found = new Map<string, number>();

  for (const root of options.roots) {
    await walk(root, 0);
  }

  // A recording that reached a clean stop has both files; prefer the finished one.
  for (const file of [...found.keys()]) {
    if (file.endsWith(STREAM_SUFFIX)) {
      const final = file.slice(0, -STREAM_SUFFIX.length) + FINAL_SUFFIX;
      if (found.has(final)) found.delete(file);
    }
  }

  const sorted = [...found.entries()].sort((a, b) => b[1] - a[1]).map(([file]) => file);
  return options.limit ? sorted.slice(0, options.limit) : sorted;

  async function walk(directory: string, depth: number): Promise<void> {
    if (depth > maxDepth) return;

    let entries;
    try {
      entries = await fs.readdir(directory, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      const full = path.join(directory, entry.name);

      if (entry.isDirectory()) {
        if (SKIP_DIRECTORIES.has(entry.name) || entry.name.startsWith(".")) continue;
        await walk(full, depth + 1);
        continue;
      }

      if (!entry.isFile()) continue;
      if (!entry.name.endsWith(FINAL_SUFFIX) && !entry.name.endsWith(STREAM_SUFFIX)) continue;

      try {
        const stat = await fs.stat(full);
        found.set(full, stat.mtimeMs);
      } catch {
        found.set(full, 0);
      }
    }
  }
}

/**
 * Turns whatever the caller has into a label file path.
 *
 * Accepts the label file itself, the media file beside it, or a bare name such as `arena_run_003`, which is
 * searched for under the configured roots. This exists because an agent usually starts from a video path.
 */
export async function resolveLabelPath(input: string, roots: string[]): Promise<string> {
  const trimmed = input.trim();
  if (!trimmed) throw new Error("No recording was named.");

  const candidates: string[] = [];
  const direct = path.resolve(trimmed);

  if (trimmed.endsWith(FINAL_SUFFIX) || trimmed.endsWith(STREAM_SUFFIX)) {
    candidates.push(direct);
  } else {
    const withoutExtension = direct.replace(/\.[^./\\]+$/, "");
    candidates.push(withoutExtension + FINAL_SUFFIX, withoutExtension + STREAM_SUFFIX);
    candidates.push(direct + FINAL_SUFFIX, direct + STREAM_SUFFIX);
  }

  for (const candidate of candidates) {
    if (await exists(candidate)) return candidate;
  }

  // Not a path we can reach: fall back to matching a name against everything under the roots.
  const needle = path.basename(trimmed).replace(/\.[^.]+$/, "").toLowerCase();
  const all = await findLabelFiles({ roots });

  const matches = all.filter((file) => {
    const base = path.basename(file).replace(FINAL_SUFFIX, "").replace(STREAM_SUFFIX, "").toLowerCase();
    return base === needle || base.includes(needle);
  });

  if (matches[0]) return matches[0];

  throw new Error(
    `No label file for '${input}'. Looked for ${FINAL_SUFFIX} next to it and searched ` +
      `${roots.join(", ")}. Run list_recordings to see what is available.`
  );
}

interface CacheEntry {
  mtimeMs: number;
  size: number;
  loaded: LoadedDocument;
}

const cache = new Map<string, CacheEntry>();

/** Loads a label file, reusing the parse while the file on disk is unchanged. */
export async function loadDocument(labelPath: string): Promise<LoadedDocument> {
  const stat = await fs.stat(labelPath);
  const cached = cache.get(labelPath);

  if (cached && cached.mtimeMs === stat.mtimeMs && cached.size === stat.size) {
    return cached.loaded;
  }

  const text = await fs.readFile(labelPath, "utf8");
  const recovered = labelPath.endsWith(STREAM_SUFFIX);
  const doc = recovered ? rebuildFromStream(text, labelPath) : parseFinal(text, labelPath);

  const mediaPath = await locateMedia(doc, labelPath);
  const loaded: LoadedDocument = { doc, labelPath, mediaPath, recovered };

  cache.set(labelPath, { mtimeMs: stat.mtimeMs, size: stat.size, loaded });
  return loaded;
}

/** Loads a recording named any of the ways `resolveLabelPath` accepts. */
export async function openRecording(file: string, roots: string[]): Promise<LoadedDocument> {
  return loadDocument(await resolveLabelPath(file, roots));
}

/** Reads a label file down to the fields `list_recordings` reports. */
export async function summarize(labelPath: string): Promise<RecordingSummary> {
  const { doc, mediaPath, recovered } = await loadDocument(labelPath);
  const stat = await fs.stat(labelPath);

  return {
    labelPath,
    mediaPath,
    mediaExists: mediaPath !== null,
    recovered,
    recorder: doc.session.recorder,
    scene: doc.session.scene,
    startedAt: doc.session.startedAt,
    duration: doc.media.duration,
    frameRate: doc.media.frameRate,
    frameCount: doc.media.frameCount,
    resolution:
      doc.media.width && doc.media.height ? `${doc.media.width}x${doc.media.height}` : undefined,
    labels: doc.stats.labels,
    markers: doc.markers.length,
    events: doc.events.length,
    chapters: doc.chapters.map((chapter) => chapter.name),
    dropped: doc.stats.dropped,
    modified: new Date(stat.mtimeMs).toISOString(),
  };
}

// ---------------------------------------------------------------------------------------------------------

function parseFinal(text: string, labelPath: string): LabelDocument {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (error) {
    throw new Error(
      `'${labelPath}' is not valid JSON (${(error as Error).message}). If the recording was interrupted, ` +
        `the ${STREAM_SUFFIX} file beside it can still be read.`
    );
  }

  const doc = raw as Partial<LabelDocument>;
  if (typeof doc.schema !== "string" || !doc.schema.startsWith("labeled-recorder/")) {
    throw new Error(`'${labelPath}' is not a Labeled Recorder label file.`);
  }

  return normalize(doc);
}

function normalize(doc: Partial<LabelDocument>): LabelDocument {
  const media = {
    path: "",
    kind: "other" as const,
    frameRate: 0,
    frameCount: 0,
    duration: 0,
    ...(doc.media ?? {}),
  };

  if (!media.duration && media.frameRate > 0) {
    media.duration = media.frameCount / media.frameRate;
  }

  return {
    schema: doc.schema ?? "labeled-recorder/1",
    generator: doc.generator,
    media,
    session: doc.session ?? {},
    stats: {
      labels: 0,
      events: 0,
      markers: 0,
      spans: 0,
      chapters: 0,
      signals: 0,
      mediaFrames: media.frameCount,
      dropped: 0,
      truncatedLabels: 0,
      ...(doc.stats ?? {}),
    },
    chapters: doc.chapters ?? [],
    spans: doc.spans ?? [],
    markers: doc.markers ?? [],
    events: doc.events ?? [],
    signals: doc.signals ?? [],
  };
}

interface StreamLine {
  op: string;
  name?: string;
  t?: number;
  f?: number;
  score?: number;
  v?: LabelValue;
  data?: Record<string, LabelValue>;
  schema?: string;
  media?: string;
  frameRate?: number;
  frameRatePlayback?: "Constant" | "Variable";
  width?: number;
  height?: number;
  recorder?: string;
  startedAt?: string;
}

/**
 * Rebuilds a document from the append-as-you-go stream a crashed or force-quit recording leaves behind.
 *
 * The same pairing the Unity writer does is applied here: spans match by name on a stack, chapters close when
 * the next one opens, and anything still open at the end of the stream is closed at the last timestamp seen.
 */
export function rebuildFromStream(text: string, labelPath: string): LabelDocument {
  const lines = text.split(/\r?\n/).filter((line) => line.trim().length > 0);

  let header: StreamLine | null = null;
  const events: LabelEvent[] = [];
  const markers: LabelMarker[] = [];
  const spans: LabelSpan[] = [];
  const chapters: LabelChapter[] = [];
  const signals = new Map<string, LabelSignal>();
  const meta: Record<string, LabelValue> = {};
  const openSpans = new Map<string, LabelSpan[]>();

  let lastT = 0;
  let lastF = 0;
  let accepted = 0;

  for (const line of lines) {
    let entry: StreamLine;
    try {
      entry = JSON.parse(line) as StreamLine;
    } catch {
      continue; // A half-written final line is expected after a crash.
    }

    if (entry.op === "header") {
      header = entry;
      continue;
    }

    const t = entry.t ?? 0;
    const f = entry.f ?? 0;
    if (t > lastT) lastT = t;
    if (f > lastF) lastF = f;
    accepted++;

    const name = entry.name ?? "";
    const cat = category(name);

    switch (entry.op) {
      case "event":
        events.push({ name, cat, t, f, data: entry.data });
        break;

      case "marker":
        markers.push({ name, cat, t, f, score: entry.score ?? 1, data: entry.data });
        break;

      case "spanBegin": {
        const span: LabelSpan = { name, cat, t, f, endT: t, endF: f, dur: 0, data: entry.data, open: true };
        spans.push(span);
        const stack = openSpans.get(name) ?? [];
        stack.push(span);
        openSpans.set(name, stack);
        break;
      }

      case "spanEnd": {
        const stack = openSpans.get(name);
        const span = stack?.pop();
        if (stack && stack.length === 0) openSpans.delete(name);

        if (span) {
          span.endT = t;
          span.endF = f;
          span.dur = Math.max(0, t - span.t);
          delete span.open;
          if (entry.data) span.data = { ...(span.data ?? {}), ...entry.data };
        } else {
          spans.push({ name, cat, t, f, endT: t, endF: f, dur: 0, data: entry.data });
        }
        break;
      }

      case "signal": {
        const existing = signals.get(name);
        const channel: LabelSignal =
          existing ??
          { name, cat, type: signalType(entry.v), count: 0, samples: [] };

        channel.samples.push([t, f, entry.v ?? null]);
        channel.count = channel.samples.length;

        if (typeof entry.v === "number") {
          channel.min = channel.min === undefined ? entry.v : Math.min(channel.min, entry.v);
          channel.max = channel.max === undefined ? entry.v : Math.max(channel.max, entry.v);
        }

        signals.set(name, channel);
        break;
      }

      case "chapter": {
        const previous = chapters[chapters.length - 1];
        if (previous) {
          previous.endT = t;
          previous.endF = f;
          previous.dur = Math.max(0, t - previous.t);
        }
        chapters.push({ name, t, f, endT: t, endF: f, dur: 0 });
        break;
      }

      case "meta":
        meta[name] = entry.v ?? null;
        break;

      default:
        break;
    }
  }

  for (const stack of openSpans.values()) {
    for (const span of stack) {
      span.endT = lastT;
      span.endF = lastF;
      span.dur = Math.max(0, lastT - span.t);
      span.open = true;
    }
  }

  const lastChapter = chapters[chapters.length - 1];
  if (lastChapter && lastChapter.endF <= lastChapter.f) {
    lastChapter.endT = lastT;
    lastChapter.endF = lastF;
    lastChapter.dur = Math.max(0, lastT - lastChapter.t);
  }

  const frameRate = header?.frameRate ?? 0;
  const frameCount = lastF + 1;

  return normalize({
    schema: header?.schema ?? "labeled-recorder/1",
    media: {
      path: header?.media ?? path.basename(labelPath).replace(STREAM_SUFFIX, ""),
      kind: "movie",
      frameRate,
      frameRatePlayback: header?.frameRatePlayback,
      width: header?.width,
      height: header?.height,
      frameCount,
      duration: frameRate > 0 ? frameCount / frameRate : lastT,
    },
    session: {
      recorder: header?.recorder,
      startedAt: header?.startedAt,
      meta: Object.keys(meta).length > 0 ? meta : undefined,
    },
    stats: {
      labels: accepted,
      events: events.length,
      markers: markers.length,
      spans: spans.length,
      chapters: chapters.length,
      signals: signals.size,
      mediaFrames: frameCount,
      dropped: 0,
      truncatedLabels: 0,
      warning:
        "Rebuilt from an interrupted recording's stream file. The media file may be shorter than the labels.",
    },
    chapters,
    spans,
    markers,
    events,
    signals: [...signals.values()],
  });
}

function signalType(value: LabelValue | undefined): LabelSignal["type"] {
  if (typeof value === "number") return "number";
  if (typeof value === "boolean") return "bool";
  if (typeof value === "string") return "string";
  if (Array.isArray(value)) return "vector";
  return "null";
}

function category(name: string): string | undefined {
  if (!name) return undefined;
  const dot = name.indexOf(".");
  return dot <= 0 ? name : name.slice(0, dot);
}

async function locateMedia(doc: LabelDocument, labelPath: string): Promise<string | null> {
  const base = labelPath.endsWith(STREAM_SUFFIX)
    ? labelPath.slice(0, -STREAM_SUFFIX.length)
    : labelPath.slice(0, -FINAL_SUFFIX.length);

  const candidates: string[] = [];

  if (doc.media.absolutePath) candidates.push(doc.media.absolutePath);
  if (doc.media.extension) candidates.push(`${base}.${doc.media.extension}`);
  if (doc.media.path) candidates.push(path.resolve(path.dirname(labelPath), doc.media.path));

  for (const candidate of candidates) {
    if (await exists(candidate)) return candidate;
  }

  // An image sequence has no single file; report the folder so the caller still has somewhere to look.
  if (doc.media.imageSequence) {
    const directory = path.dirname(base);
    if (await exists(directory)) return directory;
  }

  return null;
}

async function exists(target: string): Promise<boolean> {
  try {
    await fs.access(target);
    return true;
  } catch {
    return false;
  }
}
