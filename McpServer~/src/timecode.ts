/** Conversions between seconds, frame numbers and SMPTE-style timecode. */

/** Rounds to millisecond precision, which is as fine as any NLE needs. */
export function round(value: number, places = 6): number {
  const factor = 10 ** places;
  return Math.round(value * factor) / factor;
}

/** Converts seconds to a zero-based frame number. */
export function secondsToFrame(seconds: number, frameRate: number): number {
  if (!Number.isFinite(frameRate) || frameRate <= 0) return 0;
  return Math.max(0, Math.round(seconds * frameRate));
}

/** Converts a zero-based frame number to seconds. */
export function frameToSeconds(frame: number, frameRate: number): number {
  if (!Number.isFinite(frameRate) || frameRate <= 0) return 0;
  return round(frame / frameRate);
}

/**
 * Formats seconds as `HH:MM:SS:FF`, the form Premiere, Resolve and an EDL all expect.
 *
 * Fractional rates such as 29.97 and 59.94 are rendered non-drop-frame: the frame count uses the rounded
 * nominal rate, which is what a non-drop timeline does.
 */
export function secondsToTimecode(seconds: number, frameRate: number): string {
  const nominal = Math.max(1, Math.round(frameRate || 1));
  const total = Math.max(0, Math.round(seconds * nominal));

  const frames = total % nominal;
  const totalSeconds = Math.floor(total / nominal);
  const ss = totalSeconds % 60;
  const mm = Math.floor(totalSeconds / 60) % 60;
  const hh = Math.floor(totalSeconds / 3600);

  return [hh, mm, ss, frames].map((part) => String(part).padStart(2, "0")).join(":");
}

/** Formats seconds as `MM:SS.mmm`, which reads better in prose than full timecode. */
export function secondsToClock(seconds: number): string {
  const safe = Math.max(0, seconds);
  const ms = Math.floor((safe % 1) * 1000);
  const ss = Math.floor(safe) % 60;
  const mm = Math.floor(safe / 60);
  return `${String(mm).padStart(2, "0")}:${String(ss).padStart(2, "0")}.${String(ms).padStart(3, "0")}`;
}

/** Parses `HH:MM:SS:FF`, `HH:MM:SS;FF`, `MM:SS` or a plain number of seconds. */
export function parseTime(input: string | number, frameRate: number): number {
  if (typeof input === "number") return input;

  const text = input.trim();
  if (/^-?\d+(\.\d+)?$/.test(text)) return Number(text);

  const parts = text.split(/[:;]/).map((part) => Number(part));
  if (parts.some((part) => !Number.isFinite(part))) {
    throw new Error(`Cannot read '${input}' as a time. Use seconds, MM:SS, or HH:MM:SS:FF.`);
  }

  const nominal = Math.max(1, Math.round(frameRate || 1));

  if (parts.length === 4) {
    const [hh, mm, ss, ff] = parts as [number, number, number, number];
    return hh * 3600 + mm * 60 + ss + ff / nominal;
  }
  if (parts.length === 3) {
    const [hh, mm, ss] = parts as [number, number, number];
    return hh * 3600 + mm * 60 + ss;
  }
  if (parts.length === 2) {
    const [mm, ss] = parts as [number, number];
    return mm * 60 + ss;
  }

  throw new Error(`Cannot read '${input}' as a time.`);
}
