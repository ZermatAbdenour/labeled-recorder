import path from "node:path";
import { round, secondsToClock, secondsToFrame, secondsToTimecode } from "./timecode.js";
import type {
  LabelData,
  LabelDocument,
  LabelEvent,
  LabelMarker,
  LabelSignal,
  LabelSpan,
  LabelValue,
  LoadedDocument,
  Window,
} from "./types.js";

/** Comparison operators accepted by payload filters and signal predicates. */
export type Operator = "==" | "!=" | "<" | "<=" | ">" | ">=" | "contains" | "changes";

/** One condition on an event payload or a signal value. */
export interface Condition {
  key?: string;
  op: Operator;
  value?: LabelValue;
}

/** Which labels to look at. */
export interface Selector {
  names?: string[];
  namePrefix?: string;
  categories?: string[];
  where?: Condition[];
  from?: number;
  to?: number;
}

// ---------------------------------------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------------------------------------

function matchesSelector(name: string, cat: string | undefined, t: number, data: LabelData | undefined,
  selector: Selector): boolean {
  if (selector.from !== undefined && t < selector.from) return false;
  if (selector.to !== undefined && t > selector.to) return false;
  if (selector.names && selector.names.length > 0 && !selector.names.includes(name)) return false;
  if (selector.namePrefix && !name.startsWith(selector.namePrefix)) return false;
  if (selector.categories && selector.categories.length > 0) {
    const resolved = cat ?? categoryOf(name);
    if (!resolved || !selector.categories.includes(resolved)) return false;
  }

  if (selector.where && selector.where.length > 0) {
    for (const condition of selector.where) {
      const field = condition.key ? data?.[condition.key] : undefined;
      if (!compare(field ?? null, condition.op, condition.value ?? null)) return false;
    }
  }

  return true;
}

/** Returns the events matching a selector, in time order. */
export function queryEvents(doc: LabelDocument, selector: Selector): LabelEvent[] {
  return doc.events.filter((event) => matchesSelector(event.name, event.cat, event.t, event.data, selector));
}

/** Returns the markers matching a selector, in time order. */
export function queryMarkers(doc: LabelDocument, selector: Selector, minScore = 0): LabelMarker[] {
  return doc.markers.filter(
    (marker) =>
      marker.score >= minScore &&
      matchesSelector(marker.name, marker.cat, marker.t, marker.data, selector)
  );
}

/** Returns the spans matching a selector, in start order. */
export function querySpans(doc: LabelDocument, selector: Selector): LabelSpan[] {
  return doc.spans.filter((span) => matchesSelector(span.name, span.cat, span.t, span.data, selector));
}

function compare(left: LabelValue, op: Operator, right: LabelValue): boolean {
  switch (op) {
    case "==":
      return looseEquals(left, right);
    case "!=":
      return !looseEquals(left, right);
    case "contains":
      return String(left ?? "").toLowerCase().includes(String(right ?? "").toLowerCase());
    case "changes":
      return true;
    case "<":
    case "<=":
    case ">":
    case ">=": {
      const a = Number(left);
      const b = Number(right);
      if (!Number.isFinite(a) || !Number.isFinite(b)) return false;
      if (op === "<") return a < b;
      if (op === "<=") return a <= b;
      if (op === ">") return a > b;
      return a >= b;
    }
    default:
      return false;
  }
}

function looseEquals(left: LabelValue, right: LabelValue): boolean {
  if (Array.isArray(left) || Array.isArray(right)) {
    return JSON.stringify(left) === JSON.stringify(right);
  }
  if (typeof left === "number" || typeof right === "number") {
    return Number(left) === Number(right);
  }
  if (typeof left === "boolean" || typeof right === "boolean") {
    return Boolean(left) === Boolean(right);
  }
  return String(left) === String(right);
}

function categoryOf(name: string): string | undefined {
  const dot = name.indexOf(".");
  return dot <= 0 ? name || undefined : name.slice(0, dot);
}

// ---------------------------------------------------------------------------------------------------------
// Signals
// ---------------------------------------------------------------------------------------------------------

/** Finds a signal channel by exact name, then by case-insensitive suffix. */
export function findSignal(doc: LabelDocument, name: string): LabelSignal | undefined {
  const exact = doc.signals.find((signal) => signal.name === name);
  if (exact) return exact;

  const lowered = name.toLowerCase();
  return doc.signals.find(
    (signal) => signal.name.toLowerCase() === lowered || signal.name.toLowerCase().endsWith(`.${lowered}`)
  );
}

/**
 * The value of a channel at a point in time.
 *
 * Signals are step functions, so this is the last sample at or before `t`, and `null` before the first sample.
 */
export function signalValueAt(signal: LabelSignal, t: number): LabelValue {
  let value: LabelValue = null;
  for (const [sampleTime, , sampleValue] of signal.samples) {
    if (sampleTime > t) break;
    value = sampleValue;
  }
  return value;
}

/** Every channel's value at a point in time, for reporting the game state at a cut point. */
export function stateAt(doc: LabelDocument, t: number): Record<string, LabelValue> {
  const state: Record<string, LabelValue> = {};
  for (const signal of doc.signals) {
    state[signal.name] = signalValueAt(signal, t);
  }
  return state;
}

/** Reduces a channel to at most `maxPoints` samples by keeping evenly spaced ones plus the extremes. */
export function thinSamples(signal: LabelSignal, from: number, to: number, maxPoints: number) {
  const inRange = signal.samples.filter(([t]) => t >= from && t <= to);
  if (inRange.length <= maxPoints) return inRange;

  const keep = new Set<number>([0, inRange.length - 1]);

  if (signal.type === "number") {
    let lowest = 0;
    let highest = 0;
    inRange.forEach(([, , value], index) => {
      const current = Number(value);
      if (current < Number(inRange[lowest]?.[2])) lowest = index;
      if (current > Number(inRange[highest]?.[2])) highest = index;
    });
    keep.add(lowest);
    keep.add(highest);
  }

  const stride = (inRange.length - 1) / (maxPoints - keep.size);
  for (let i = 0; i < inRange.length; i += Math.max(1, stride)) {
    keep.add(Math.round(i));
  }

  return [...keep]
    .sort((a, b) => a - b)
    .map((index) => inRange[index])
    .filter((sample): sample is NonNullable<typeof sample> => sample !== undefined);
}

/** A stretch of time during which a channel satisfied a condition. */
export interface SignalRange {
  start: number;
  end: number;
  duration: number;
  value: LabelValue;
}

/**
 * Finds the stretches where a channel satisfies a condition, and the instants where it changes when the
 * operator is `changes`.
 */
export function signalRanges(signal: LabelSignal, op: Operator, value: LabelValue,
  mediaDuration: number): SignalRange[] {
  const ranges: SignalRange[] = [];

  if (op === "changes") {
    let previous: LabelValue | undefined;
    for (const [t, , sampleValue] of signal.samples) {
      if (previous !== undefined && !looseEquals(previous, sampleValue)) {
        ranges.push({ start: t, end: t, duration: 0, value: sampleValue });
      }
      previous = sampleValue;
    }
    return ranges;
  }

  let openAt: number | null = null;
  let openValue: LabelValue = null;

  for (const [t, , sampleValue] of signal.samples) {
    const satisfied = compare(sampleValue, op, value);

    if (satisfied && openAt === null) {
      openAt = t;
      openValue = sampleValue;
    } else if (!satisfied && openAt !== null) {
      ranges.push({ start: openAt, end: t, duration: round(t - openAt), value: openValue });
      openAt = null;
    }
  }

  if (openAt !== null) {
    ranges.push({ start: openAt, end: mediaDuration, duration: round(mediaDuration - openAt), value: openValue });
  }

  return ranges;
}

// ---------------------------------------------------------------------------------------------------------
// Windows
// ---------------------------------------------------------------------------------------------------------

/** How to propose candidate clip ranges. */
export type WindowStrategy = "markers" | "events" | "density" | "spans" | "signal" | "chapters";

/** Inputs to {@link findWindows}. */
export interface WindowSpec extends Selector {
  strategy: WindowStrategy;
  minScore?: number;
  windowSeconds?: number;
  minCount?: number;
  signal?: string;
  op?: Operator;
  value?: LabelValue;
  pre?: number;
  post?: number;
  minDuration?: number;
  maxDuration?: number;
  merge?: boolean;
  mergeGap?: number;
  avoidHitches?: boolean;
  limit?: number;
}

interface Seed {
  start: number;
  end: number;
  score: number;
  reason: string;
}

/**
 * Proposes ranked candidate clip ranges.
 *
 * The strategies answer different editorial questions: `markers` and `spans` trust what the game said was
 * interesting, `density` finds where the most happened at once, and `signal` finds state-driven moments such
 * as low health. All of them return the same shape, already padded, merged, clamped and scored, so a caller
 * can compare across strategies.
 */
export function findWindows(loaded: LoadedDocument, spec: WindowSpec): Window[] {
  const { doc } = loaded;
  const duration = doc.media.duration;
  const pre = spec.pre ?? 1.5;
  const post = spec.post ?? 2.5;
  const minDuration = spec.minDuration ?? 1.5;
  const maxDuration = spec.maxDuration ?? 15;
  const limit = spec.limit ?? 10;

  let seeds: Seed[] = [];

  switch (spec.strategy) {
    case "markers": {
      for (const marker of queryMarkers(doc, spec, spec.minScore ?? 0)) {
        seeds.push({
          start: marker.t - pre,
          end: marker.t + post,
          score: 10 * (marker.score || 1),
          reason: `marker '${marker.name}'${describeData(marker.data)} at ${secondsToClock(marker.t)}`,
        });
      }
      break;
    }

    case "events": {
      for (const event of queryEvents(doc, spec)) {
        seeds.push({
          start: event.t - pre,
          end: event.t + post,
          score: 4,
          reason: `event '${event.name}'${describeData(event.data)} at ${secondsToClock(event.t)}`,
        });
      }
      break;
    }

    case "density": {
      const span = spec.windowSeconds ?? 6;
      const minCount = spec.minCount ?? 3;
      const times = queryEvents(doc, spec).map((event) => event.t);

      for (let i = 0; i < times.length; i++) {
        const start = times[i]!;
        let last = i;
        while (last + 1 < times.length && times[last + 1]! - start <= span) last++;

        const count = last - i + 1;
        if (count < minCount) continue;

        seeds.push({
          start: start - pre,
          end: times[last]! + post,
          score: 3 * count,
          reason: `${count} matching events inside ${span.toFixed(1)}s from ${secondsToClock(start)}`,
        });

        i = last; // Do not re-seed from events already covered by this burst.
      }
      break;
    }

    case "spans": {
      for (const span of querySpans(doc, spec)) {
        seeds.push({
          start: span.t - pre,
          end: span.endT + post,
          score: 6,
          reason:
            `span '${span.name}'${describeData(span.data)} lasting ${span.dur.toFixed(1)}s` +
            (span.open ? " (never closed)" : ""),
        });
      }
      break;
    }

    case "chapters": {
      for (const chapter of doc.chapters) {
        if (spec.from !== undefined && chapter.endT < spec.from) continue;
        if (spec.to !== undefined && chapter.t > spec.to) continue;
        if (spec.names && spec.names.length > 0 && !spec.names.includes(chapter.name)) continue;

        seeds.push({
          start: chapter.t,
          end: chapter.endT,
          score: 2,
          reason: `chapter '${chapter.name}' (${chapter.dur.toFixed(1)}s)`,
        });
      }
      break;
    }

    case "signal": {
      if (!spec.signal) throw new Error("The 'signal' strategy needs a 'signal' name.");
      const channel = findSignal(doc, spec.signal);
      if (!channel) {
        throw new Error(
          `No signal called '${spec.signal}'. This recording has: ${doc.signals.map((s) => s.name).join(", ") || "none"}.`
        );
      }

      const op = spec.op ?? "changes";
      for (const range of signalRanges(channel, op, spec.value ?? null, duration)) {
        if (spec.from !== undefined && range.end < spec.from) continue;
        if (spec.to !== undefined && range.start > spec.to) continue;

        seeds.push({
          start: range.start - pre,
          end: range.end + post,
          score: 5,
          reason:
            op === "changes"
              ? `${channel.name} changed to ${format(range.value)} at ${secondsToClock(range.start)}`
              : `${channel.name} ${op} ${format(spec.value ?? null)} for ${range.duration.toFixed(1)}s ` +
                `from ${secondsToClock(range.start)}`,
        });
      }
      break;
    }
  }

  if (seeds.length === 0) return [];

  seeds.sort((a, b) => a.start - b.start);

  // Chapters sit end to end and spans are already deliberate ranges, so merging them by default would
  // collapse the structure the caller asked for. Everything else benefits from it.
  const mergeByDefault = spec.strategy !== "chapters" && spec.strategy !== "spans";
  if (spec.merge ?? mergeByDefault) {
    seeds = mergeSeeds(seeds, spec.mergeGap ?? 0);
  }

  const hitches = spec.avoidHitches === false ? undefined : findSignal(doc, "perf.hitchMs");
  const budgetMs = doc.media.frameRate > 0 ? 1000 / doc.media.frameRate : 0;

  const windows = seeds
    .map((seed) => shape(seed, doc, duration, minDuration, maxDuration, hitches, budgetMs))
    .filter((window): window is Window => window !== null);

  windows.sort((a, b) => b.score - a.score || a.start - b.start);
  return windows.slice(0, limit);
}

function mergeSeeds(seeds: Seed[], gap: number): Seed[] {
  const merged: Seed[] = [];

  for (const seed of seeds) {
    const previous = merged[merged.length - 1];

    if (previous && seed.start - previous.end <= gap) {
      previous.end = Math.max(previous.end, seed.end);
      // A merged range is worth more than its best part but less than the sum, so one busy stretch does not
      // crowd out everything else.
      previous.score = Math.max(previous.score, seed.score) + 0.25 * Math.min(previous.score, seed.score);
      previous.reason = `${previous.reason}; ${seed.reason}`;
      continue;
    }

    merged.push({ ...seed });
  }

  return merged;
}

function shape(seed: Seed, doc: LabelDocument, duration: number, minDuration: number, maxDuration: number,
  hitches: LabelSignal | undefined, budgetMs: number): Window | null {
  let start = Math.max(0, seed.start);
  let end = Math.min(duration > 0 ? duration : seed.end, seed.end);

  if (end <= start) end = Math.min(duration > 0 ? duration : start + minDuration, start + minDuration);

  if (end - start < minDuration) {
    const shortfall = minDuration - (end - start);
    start = Math.max(0, start - shortfall / 2);
    end = Math.min(duration > 0 ? duration : end + shortfall, end + shortfall / 2);
  }

  if (end - start > maxDuration) {
    end = start + maxDuration;
  }

  if (end - start <= 0.001) return null;

  const frameRate = doc.media.frameRate;
  let score = seed.score;
  let worstFrameMs: number | undefined;

  if (hitches) {
    const inRange = hitches.samples.filter(([t]) => t >= start && t <= end).map(([, , v]) => Number(v));
    if (inRange.length > 0) {
      worstFrameMs = round(Math.max(...inRange), 2);
      // Footage that stuttered reads badly however good the moment was.
      if (budgetMs > 0 && worstFrameMs > budgetMs * 2.5) score *= 0.5;
      else if (budgetMs > 0 && worstFrameMs > budgetMs * 1.5) score *= 0.8;
    }
  }

  const chapter = doc.chapters.find((entry) => start >= entry.t && start < entry.endT);

  const events = doc.events
    .filter((event) => event.t >= start && event.t <= end)
    .map((event) => ({ name: event.name, t: round(event.t), data: event.data }));

  const markers = doc.markers
    .filter((marker) => marker.t >= start && marker.t <= end)
    .map((marker) => ({ name: marker.name, t: round(marker.t), score: marker.score, data: marker.data }));

  return {
    start: round(start),
    end: round(end),
    duration: round(end - start),
    startFrame: secondsToFrame(start, frameRate),
    endFrame: secondsToFrame(end, frameRate),
    startTimecode: secondsToTimecode(start, frameRate),
    endTimecode: secondsToTimecode(end, frameRate),
    score: round(score, 3),
    reason: seed.reason,
    chapter: chapter?.name,
    events: events.length > 0 ? events : undefined,
    markers: markers.length > 0 ? markers : undefined,
    worstFrameMs,
  };
}

function describeData(data: LabelData | undefined): string {
  if (!data) return "";
  const parts = Object.entries(data).map(([key, value]) => `${key}=${format(value)}`);
  return parts.length > 0 ? ` (${parts.join(", ")})` : "";
}

function format(value: LabelValue): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return `[${value.map((part) => round(part, 3)).join(", ")}]`;
  if (typeof value === "number") return String(round(value, 3));
  return String(value);
}

// ---------------------------------------------------------------------------------------------------------
// Cut lists
// ---------------------------------------------------------------------------------------------------------

/** Inputs to {@link buildCutList}. */
export interface CutListOptions {
  title?: string;
  targetDuration?: number;
  order?: "chronological" | "score" | "asGiven";
  handles?: number;
  trimLastToFit?: boolean;
}

/** One clip in a cut list. */
export interface Cut {
  index: number;
  source: string;
  sourceIn: number;
  sourceOut: number;
  duration: number;
  sourceInFrame: number;
  sourceOutFrame: number;
  sourceInTimecode: string;
  sourceOutTimecode: string;
  recordIn: number;
  recordOut: number;
  recordInTimecode: string;
  recordOutTimecode: string;
  label: string;
  chapter?: string;
  worstFrameMs?: number;
}

/** A complete edit decision, in structured form and in two text forms an NLE can read. */
export interface CutList {
  title: string;
  source: string;
  frameRate: number;
  resolution?: string;
  clipCount: number;
  totalDuration: number;
  clips: Cut[];
  droppedForBudget: number;
  edl: string;
  ffmpegReference: string;
  notes: string[];
}

/**
 * Turns chosen ranges into an edit decision: ordered clips with source and record timecodes, a CMX3600 EDL,
 * and reference ffmpeg commands.
 *
 * Nothing here renders anything. The point is to hand an editing agent the decisions in a form a human or
 * another tool can execute without re-deriving timecodes.
 */
export function buildCutList(loaded: LoadedDocument, windows: Window[], options: CutListOptions = {}): CutList {
  const { doc } = loaded;
  const frameRate = doc.media.frameRate;
  const handles = options.handles ?? 0;
  const order = options.order ?? "chronological";
  const notes: string[] = [];

  let chosen = windows.map((window) => ({ ...window }));

  if (handles > 0) {
    chosen = chosen.map((window) => ({
      ...window,
      start: Math.max(0, window.start - handles),
      end: Math.min(doc.media.duration, window.end + handles),
    }));
  }

  let droppedForBudget = 0;

  if (options.targetDuration !== undefined && options.targetDuration > 0) {
    const byScore = [...chosen].sort((a, b) => b.score - a.score);
    const kept: Window[] = [];
    let used = 0;

    for (const window of byScore) {
      const length = window.end - window.start;

      if (used + length <= options.targetDuration) {
        kept.push(window);
        used += length;
        continue;
      }

      const remaining = options.targetDuration - used;
      if (options.trimLastToFit !== false && remaining > 0.5) {
        kept.push({ ...window, end: window.start + remaining });
        used = options.targetDuration;
        continue;
      }

      droppedForBudget++;
    }

    chosen = kept;
    if (droppedForBudget > 0) {
      notes.push(
        `${droppedForBudget} candidate range(s) did not fit the ${options.targetDuration}s budget and were left out.`
      );
    }
  }

  if (order === "chronological") chosen.sort((a, b) => a.start - b.start);
  else if (order === "score") chosen.sort((a, b) => b.score - a.score);

  const source = loaded.mediaPath ?? doc.media.absolutePath ?? doc.media.path;
  const sourceName = path.basename(source);

  let record = 0;
  const clips: Cut[] = chosen.map((window, index) => {
    const duration = round(window.end - window.start);
    const recordIn = round(record);
    const recordOut = round(record + duration);
    record = recordOut;

    return {
      index: index + 1,
      source: sourceName,
      sourceIn: round(window.start),
      sourceOut: round(window.end),
      duration,
      sourceInFrame: secondsToFrame(window.start, frameRate),
      sourceOutFrame: secondsToFrame(window.end, frameRate),
      sourceInTimecode: secondsToTimecode(window.start, frameRate),
      sourceOutTimecode: secondsToTimecode(window.end, frameRate),
      recordIn,
      recordOut,
      recordInTimecode: secondsToTimecode(recordIn, frameRate),
      recordOutTimecode: secondsToTimecode(recordOut, frameRate),
      label: window.reason,
      chapter: window.chapter,
      worstFrameMs: window.worstFrameMs,
    };
  });

  const stuttery = clips.filter((clip) => {
    const budget = frameRate > 0 ? 1000 / frameRate : 0;
    return clip.worstFrameMs !== undefined && budget > 0 && clip.worstFrameMs > budget * 2.5;
  });

  if (stuttery.length > 0) {
    notes.push(
      `Clip(s) ${stuttery.map((clip) => clip.index).join(", ")} contain frames that took more than ` +
        `2.5x the frame budget. Consider re-recording those moments.`
    );
  }

  if (doc.stats.dropped > 0) {
    notes.push(
      `${doc.stats.dropped} label(s) were dropped during capture, so this recording's labels are incomplete.`
    );
  }

  const title = options.title ?? `${path.parse(sourceName).name} cut`;

  return {
    title,
    source,
    frameRate,
    resolution: doc.media.width && doc.media.height ? `${doc.media.width}x${doc.media.height}` : undefined,
    clipCount: clips.length,
    totalDuration: round(record),
    clips,
    droppedForBudget,
    edl: renderEdl(title, clips, frameRate),
    ffmpegReference: renderFfmpeg(source, clips),
    notes,
  };
}

function renderEdl(title: string, clips: Cut[], frameRate: number): string {
  const lines = [`TITLE: ${title}`, "FCM: NON-DROP FRAME", ""];

  for (const clip of clips) {
    const id = String(clip.index).padStart(3, "0");
    lines.push(
      `${id}  AX       V     C        ` +
        `${clip.sourceInTimecode} ${clip.sourceOutTimecode} ` +
        `${clip.recordInTimecode} ${clip.recordOutTimecode}`
    );
    lines.push(`* FROM CLIP NAME: ${clip.source}`);
    lines.push(`* COMMENT: ${clip.label.replace(/[\r\n]+/g, " ")}`);
    lines.push("");
  }

  if (frameRate && Math.abs(frameRate - Math.round(frameRate)) > 0.001) {
    lines.push(`* NOTE: source is ${frameRate} fps; timecode above is non-drop at ${Math.round(frameRate)} fps.`);
  }

  return lines.join("\n");
}

function renderFfmpeg(source: string, clips: Cut[]): string {
  const quoted = (value: string) => `"${value.replace(/"/g, '\\"')}"`;
  const lines = [
    "# Reference only; this server does not run anything.",
    "# 1. Cut each selected range, re-encoding so the cuts land on exact frames.",
  ];

  clips.forEach((clip) => {
    lines.push(
      `ffmpeg -ss ${clip.sourceIn} -to ${clip.sourceOut} -i ${quoted(source)} ` +
        `-c:v libx264 -crf 18 -preset slow -c:a aac part_${String(clip.index).padStart(3, "0")}.mp4`
    );
  });

  lines.push("", "# 2. Join them in order.");
  lines.push(
    `printf "${clips.map((clip) => `file 'part_${String(clip.index).padStart(3, "0")}.mp4'`).join("\\n")}\\n" > parts.txt`
  );
  lines.push("ffmpeg -f concat -safe 0 -i parts.txt -c copy out.mp4");

  return lines.join("\n");
}

// ---------------------------------------------------------------------------------------------------------
// Description
// ---------------------------------------------------------------------------------------------------------

/**
 * How many markers and spans `describe` lists in full before it reports only a count. The vocabularies and
 * signal inventory are always complete, so a caller can still query anything it sees.
 */
const LIST_CAP = 200;

/** A readable inventory of everything one recording contains. */
export function describe(loaded: LoadedDocument) {
  const { doc } = loaded;

  const eventNames = tally(doc.events.map((event) => event.name));
  const eventCategories = tally(doc.events.map((event) => event.cat ?? categoryOf(event.name) ?? "?"));
  const markerNames = tally(doc.markers.map((marker) => marker.name));
  const spanNames = tally(doc.spans.map((span) => span.name));

  const payloadKeys = new Map<string, Set<string>>();
  for (const event of doc.events) {
    if (!event.data) continue;
    const keys = payloadKeys.get(event.name) ?? new Set<string>();
    Object.keys(event.data).forEach((key) => keys.add(key));
    payloadKeys.set(event.name, keys);
  }

  return {
    labelFile: loaded.labelPath,
    mediaFile: loaded.mediaPath,
    mediaMissing: loaded.mediaPath === null,
    recoveredFromStream: loaded.recovered,
    schema: doc.schema,
    media: doc.media,
    session: doc.session,
    stats: doc.stats,
    timeline: {
      duration: doc.media.duration,
      durationClock: secondsToClock(doc.media.duration),
      lastFrame: Math.max(0, doc.media.frameCount - 1),
      endTimecode: secondsToTimecode(doc.media.duration, doc.media.frameRate),
    },
    chapters: doc.chapters.map((chapter) => ({
      name: chapter.name,
      t: chapter.t,
      endT: chapter.endT,
      dur: chapter.dur,
      timecode: secondsToTimecode(chapter.t, doc.media.frameRate),
    })),
    spans: doc.spans.slice(0, LIST_CAP).map((span) => ({
      name: span.name,
      t: span.t,
      endT: span.endT,
      dur: span.dur,
      open: span.open ?? false,
      data: span.data,
    })),
    spansTruncated: doc.spans.length > LIST_CAP ? doc.spans.length : undefined,
    markers: doc.markers.slice(0, LIST_CAP).map((marker) => ({
      name: marker.name,
      t: marker.t,
      score: marker.score,
      timecode: secondsToTimecode(marker.t, doc.media.frameRate),
      data: marker.data,
    })),
    markersTruncated: doc.markers.length > LIST_CAP ? doc.markers.length : undefined,
    eventVocabulary: eventNames.map(([name, count]) => ({
      name,
      count,
      payloadKeys: [...(payloadKeys.get(name) ?? [])],
    })),
    eventCategories: eventCategories.map(([name, count]) => ({ name, count })),
    markerVocabulary: markerNames.map(([name, count]) => ({ name, count })),
    spanVocabulary: spanNames.map(([name, count]) => ({ name, count })),
    signals: doc.signals.map((signal) => ({
      name: signal.name,
      type: signal.type,
      samples: signal.count,
      min: signal.min,
      max: signal.max,
      firstValue: signal.samples[0]?.[2] ?? null,
      lastValue: signal.samples[signal.samples.length - 1]?.[2] ?? null,
    })),
    busiestTenSeconds: busiest(doc, 10),
  };
}

function busiest(doc: LabelDocument, windowSeconds: number) {
  const times = doc.events.map((event) => event.t).sort((a, b) => a - b);
  if (times.length === 0) return null;

  let best = { start: 0, count: 0 };

  for (let i = 0; i < times.length; i++) {
    let last = i;
    while (last + 1 < times.length && times[last + 1]! - times[i]! <= windowSeconds) last++;
    const count = last - i + 1;
    if (count > best.count) best = { start: times[i]!, count };
  }

  return {
    start: round(best.start),
    end: round(best.start + windowSeconds),
    events: best.count,
    timecode: secondsToTimecode(best.start, doc.media.frameRate),
  };
}

function tally(values: string[]): Array<[string, number]> {
  const counts = new Map<string, number>();
  for (const value of values) counts.set(value, (counts.get(value) ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

/** Reports anything that would make a label file misleading to edit from. */
export function validate(loaded: LoadedDocument): { ok: boolean; problems: string[]; warnings: string[] } {
  const { doc } = loaded;
  const problems: string[] = [];
  const warnings: string[] = [];

  if (!doc.media.frameRate || doc.media.frameRate <= 0) {
    problems.push("The label file reports no frame rate, so times cannot be converted to frames.");
  }

  if (doc.media.frameCount <= 0) {
    problems.push("The label file reports no frames, so the recording may have produced nothing.");
  }

  if (loaded.mediaPath === null) {
    problems.push(
      `The media file is missing. The label file points at '${doc.media.path}', which is not on disk ` +
        `next to it.`
    );
  }

  if (doc.stats.dropped > 0) {
    warnings.push(
      `${doc.stats.dropped} label(s) were dropped during capture. Raise Buffer Capacity in ` +
        `Project Settings > Labeled Recorder.`
    );
  }

  if (doc.stats.truncatedLabels > 0) {
    warnings.push(`${doc.stats.truncatedLabels} label(s) were discarded for exceeding the per-recording cap.`);
  }

  if (loaded.recovered) {
    warnings.push("Rebuilt from an interrupted recording; the media may be shorter than the labels.");
  }

  const openSpans = doc.spans.filter((span) => span.open);
  if (openSpans.length > 0) {
    warnings.push(
      `Span(s) never closed and were cut off at the end of the recording: ` +
        `${openSpans.map((span) => span.name).join(", ")}.`
    );
  }

  const beyond = [...doc.events, ...doc.markers].filter((entry) => entry.t > doc.media.duration + 0.001);
  if (beyond.length > 0) {
    warnings.push(`${beyond.length} label(s) are timestamped past the end of the media.`);
  }

  if (doc.markers.length === 0) {
    warnings.push(
      "No markers. Adding GameLabel.Highlight calls at the moments you would point at yourself is the " +
        "single biggest improvement you can make to automatic editing."
    );
  }

  return { ok: problems.length === 0, problems, warnings };
}
