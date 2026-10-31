/**
 * The shape of a `.labels.json` file written by the Labeled Recorder Unity package.
 *
 * Two conventions run through the whole format and are worth stating once:
 *
 * - `t` is seconds from the first recorded frame of the media file, and `f` is the zero-based index of that
 *   same frame. Neither is game time, so both can be used directly as in and out points.
 * - Names are dotted, and `cat` is always the part before the first dot. That is what lets an agent reason
 *   about groups of labels in a game it knows nothing about.
 */

/** A payload value. Vectors arrive as arrays of 2, 3 or 4 numbers. */
export type LabelValue = number | string | boolean | number[] | null;

/** The payload attached to an event, marker or span. */
export type LabelData = Record<string, LabelValue>;

/** Facts about the recorded file itself. */
export interface MediaInfo {
  path: string;
  absolutePath?: string;
  kind: "movie" | "imageSequence" | "aovSequence" | "audio" | "animation" | "other";
  extension?: string;
  imageSequence?: boolean;
  width?: number;
  height?: number;
  aspect?: number;
  frameRate: number;
  frameRatePlayback?: "Constant" | "Variable";
  captureEveryNthFrame?: number;
  frameCount: number;
  duration: number;
  hasAudio?: boolean;
}

/** Facts about the take as a whole. */
export interface SessionInfo {
  id?: string;
  recorder?: string;
  recorderType?: string;
  recordMode?: string;
  take?: number;
  startedAt?: string;
  scene?: string;
  product?: string;
  platform?: string;
  meta?: LabelData;
}

/** Counters that say how complete the capture is. */
export interface LabelStats {
  labels: number;
  events: number;
  markers: number;
  spans: number;
  chapters: number;
  signals: number;
  mediaFrames: number;
  /** Labels lost because the capture buffer filled up. Non-zero means the file is incomplete. */
  dropped: number;
  /** Labels discarded for exceeding the per-recording ceiling. */
  truncatedLabels: number;
  warning?: string;
}

/** A point in time. */
export interface LabelEvent {
  name: string;
  cat?: string;
  t: number;
  f: number;
  /** Game time, present only when the capture ran at a variable frame rate. */
  gt?: number;
  data?: LabelData;
}

/** A point in time that the game flagged as editorially interesting. */
export interface LabelMarker extends LabelEvent {
  /** Caller-supplied importance, 0 to 1. */
  score: number;
}

/** Something that lasted. */
export interface LabelSpan {
  name: string;
  cat?: string;
  t: number;
  f: number;
  endT: number;
  endF: number;
  dur: number;
  /** True when the recording stopped before the span closed. */
  open?: boolean;
  data?: LabelData;
}

/** A named stretch of the recording. Chapters never overlap and cover the whole take. */
export interface LabelChapter {
  name: string;
  t: number;
  f: number;
  endT: number;
  endF: number;
  dur: number;
}

/** One sample of a game-data channel: `[seconds, frame, value]`. */
export type SignalSample = [number, number, LabelValue];

/**
 * A game-data channel. Samples form a step function: a value holds until the next sample, so the value at any
 * time is the last sample at or before it.
 */
export interface LabelSignal {
  name: string;
  cat?: string;
  type: "number" | "bool" | "string" | "vector" | "null";
  count: number;
  min?: number;
  max?: number;
  samples: SignalSample[];
}

/** A complete label file. */
export interface LabelDocument {
  schema: string;
  generator?: { package?: string; version?: string; unity?: string };
  media: MediaInfo;
  session: SessionInfo;
  stats: LabelStats;
  chapters: LabelChapter[];
  spans: LabelSpan[];
  markers: LabelMarker[];
  events: LabelEvent[];
  signals: LabelSignal[];
}

/** A label document plus where it came from. */
export interface LoadedDocument {
  doc: LabelDocument;
  /** Absolute path of the label file that was read. */
  labelPath: string;
  /** Absolute path of the media file, when it could be resolved on disk. */
  mediaPath: string | null;
  /** True when the document was rebuilt from a `.labels.jsonl` stream left by an interrupted recording. */
  recovered: boolean;
}

/** A candidate clip range. */
export interface Window {
  start: number;
  end: number;
  duration: number;
  startFrame: number;
  endFrame: number;
  startTimecode: string;
  endTimecode: string;
  /** Higher is a stronger candidate. Only comparable within one `find_windows` call. */
  score: number;
  /** Why this range was proposed, in words an editor can act on. */
  reason: string;
  chapter?: string;
  events?: Array<{ name: string; t: number; data?: LabelData }>;
  markers?: Array<{ name: string; t: number; score: number; data?: LabelData }>;
  worstFrameMs?: number;
}
