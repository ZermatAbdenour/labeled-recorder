#!/usr/bin/env node
/**
 * Labeled Recorder MCP server.
 *
 * Gives an editing agent the timecode context that the Labeled Recorder Unity package writes alongside every
 * Unity Recorder capture: what happened, when, and what the game's state was at that moment.
 *
 * It reads label files and answers questions about them. It never renders, encodes, moves or deletes
 * anything; `build_cut_list` returns an edit decision for something else to execute.
 */
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

import {
  buildCutList,
  describe,
  findWindows,
  findSignal,
  queryEvents,
  queryMarkers,
  querySpans,
  signalRanges,
  signalValueAt,
  stateAt,
  thinSamples,
  validate,
  type Condition,
  type Selector,
  type WindowSpec,
} from "./analyze.js";
import { EDITING_GUIDE, QUICK_REFERENCE, SCHEMA_REFERENCE, fullGuide } from "./guide.js";
import { findLabelFiles, openRecording, resolveRoots, summarize } from "./load.js";
import { parseTime, round, secondsToClock, secondsToFrame, secondsToTimecode } from "./timecode.js";
import type { Window } from "./types.js";

const ROOTS = resolveRoots(process.argv.slice(2));

const server = new McpServer(
  { name: "labeled-recorder", version: "1.0.0" },
  {
    instructions:
      "Reads the label files that the Labeled Recorder Unity package writes next to Unity Recorder " +
      "captures, so you can cut shorts and trailers from gameplay footage without watching it. Call " +
      "labels_guide once before your first edit, then describe_recording before working on any particular " +
      "take. Every tool that takes a `file` accepts the label file, the video beside it, or the recording's " +
      "name.",
  }
);

// ---------------------------------------------------------------------------------------------------------
// Shared argument shapes
// ---------------------------------------------------------------------------------------------------------

const operatorSchema = z.enum(["==", "!=", "<", "<=", ">", ">=", "contains", "changes"]);
const valueSchema = z.union([z.string(), z.number(), z.boolean()]);

const fileArg = z
  .string()
  .describe(
    "The recording: a .labels.json path, the video file beside it, or just its name (searched under the " +
      "configured roots)."
  );

const conditionSchema = z
  .object({
    key: z.string().describe("The payload field to test, for example 'weapon'."),
    op: operatorSchema,
    value: valueSchema.optional(),
  })
  .describe("A test against an event's payload.");

const selectorShape = {
  names: z.array(z.string()).optional().describe("Exact label names to include."),
  namePrefix: z.string().optional().describe("Include labels whose name starts with this, e.g. 'combat.'."),
  categories: z.array(z.string()).optional().describe("Include labels in these categories (text before the first dot)."),
  where: z.array(conditionSchema).optional().describe("All conditions must hold for the label to match."),
  from: z.number().optional().describe("Earliest media time, in seconds."),
  to: z.number().optional().describe("Latest media time, in seconds."),
};

function toSelector(args: Record<string, unknown>): Selector {
  return {
    names: args.names as string[] | undefined,
    namePrefix: args.namePrefix as string | undefined,
    categories: args.categories as string[] | undefined,
    where: args.where as Condition[] | undefined,
    from: args.from as number | undefined,
    to: args.to as number | undefined,
  };
}

function json(value: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }] };
}

function text(value: string) {
  return { content: [{ type: "text" as const, text: value }] };
}

function failure(error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  return { content: [{ type: "text" as const, text: message }], isError: true };
}

// ---------------------------------------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------------------------------------

server.registerTool(
  "labels_guide",
  {
    title: "How to use label files",
    description:
      "Explains what a Labeled Recorder label file contains and how to turn one into a short or a trailer. " +
      "Read this before your first edit. Sections: 'editing' (the workflow), 'schema' (the file format), " +
      "'tools' (a one-line summary of each tool), 'all'.",
    inputSchema: {
      section: z.enum(["all", "editing", "schema", "tools"]).default("all").optional(),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ section }) => {
    switch (section ?? "all") {
      case "editing":
        return text(EDITING_GUIDE);
      case "schema":
        return text(SCHEMA_REFERENCE);
      case "tools":
        return text(QUICK_REFERENCE);
      default:
        return text(fullGuide());
    }
  }
);

server.registerTool(
  "list_recordings",
  {
    title: "List labelled recordings",
    description:
      "Finds every labelled recording under the configured roots, newest first, with its duration, frame " +
      "rate, chapter names and label counts. Start here when you do not already have a file path.",
    inputSchema: {
      root: z.string().optional().describe("Search only this folder instead of the configured roots."),
      limit: z.number().int().min(1).max(500).default(50).optional(),
      maxDepth: z.number().int().min(0).max(16).default(8).optional(),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ root, limit, maxDepth }) => {
    try {
      const roots = root ? [root] : ROOTS;
      const files = await findLabelFiles({ roots, maxDepth, limit });
      const recordings = [];

      for (const file of files) {
        try {
          recordings.push(await summarize(file));
        } catch (error) {
          recordings.push({ labelPath: file, error: (error as Error).message });
        }
      }

      return json({ searchedRoots: roots, count: recordings.length, recordings });
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "describe_recording",
  {
    title: "Describe a recording",
    description:
      "The inventory of one take: media facts, session metadata, chapter structure, every event and marker " +
      "name with its count and payload keys, every signal channel with its range, and the busiest ten " +
      "seconds. Call this before querying anything, so you use names this game actually uses.",
    inputSchema: { file: fileArg },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ file }) => {
    try {
      return json(describe(await openRecording(file, ROOTS)));
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "query_events",
  {
    title: "Query events, markers and spans",
    description:
      "Returns the labels matching a selector, with their media times, frames and timecodes. Use `kind` to " +
      "choose between point events, scored markers and spans that lasted.",
    inputSchema: {
      file: fileArg,
      kind: z.enum(["events", "markers", "spans", "all"]).default("events").optional(),
      minScore: z.number().min(0).max(1).optional().describe("Markers only: the lowest score to include."),
      limit: z.number().int().min(1).max(2000).default(200).optional(),
      order: z.enum(["time", "score"]).default("time").optional(),
      ...selectorShape,
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async (args) => {
    try {
      const loaded = await openRecording(args.file, ROOTS);
      const fps = loaded.doc.media.frameRate;
      const selector = toSelector(args);
      const kind = args.kind ?? "events";
      const limit = args.limit ?? 200;

      const decorate = <T extends { t: number; f: number }>(entry: T) => ({
        ...entry,
        timecode: secondsToTimecode(entry.t, fps),
        clock: secondsToClock(entry.t),
      });

      const result: Record<string, unknown> = { file: loaded.labelPath, frameRate: fps };

      if (kind === "events" || kind === "all") {
        const events = queryEvents(loaded.doc, selector);
        result.events = events.slice(0, limit).map(decorate);
        result.eventsTotal = events.length;
      }

      if (kind === "markers" || kind === "all") {
        let markers = queryMarkers(loaded.doc, selector, args.minScore ?? 0);
        if (args.order === "score") markers = [...markers].sort((a, b) => b.score - a.score);
        result.markers = markers.slice(0, limit).map(decorate);
        result.markersTotal = markers.length;
      }

      if (kind === "spans" || kind === "all") {
        const spans = querySpans(loaded.doc, selector);
        result.spans = spans.slice(0, limit).map((span) => ({
          ...span,
          timecode: secondsToTimecode(span.t, fps),
          endTimecode: secondsToTimecode(span.endT, fps),
        }));
        result.spansTotal = spans.length;
      }

      return json(result);
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "get_signal",
  {
    title: "Read a game-data channel",
    description:
      "Returns a signal's samples over a time range, its value at one moment, or the stretches where it " +
      "satisfied a condition. Signals are step functions: a value holds until the next sample. Use `at` for " +
      "a single moment, or `op`/`value` to find threshold crossings such as health below 20.",
    inputSchema: {
      file: fileArg,
      name: z.string().describe("Channel name, for example 'player.health' or 'perf.hitchMs'."),
      at: z.union([z.number(), z.string()]).optional().describe("Report the value at this time only."),
      from: z.number().optional(),
      to: z.number().optional(),
      maxPoints: z.number().int().min(2).max(5000).default(400).optional(),
      op: operatorSchema.optional().describe("With `value`, return the stretches where this holds."),
      value: valueSchema.optional(),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ file, name, at, from, to, maxPoints, op, value }) => {
    try {
      const loaded = await openRecording(file, ROOTS);
      const { doc } = loaded;
      const signal = findSignal(doc, name);

      if (!signal) {
        throw new Error(
          `No signal called '${name}'. This recording has: ` +
            `${doc.signals.map((entry) => entry.name).join(", ") || "none"}.`
        );
      }

      if (at !== undefined) {
        const time = parseTime(at, doc.media.frameRate);
        return json({
          name: signal.name,
          type: signal.type,
          at: round(time),
          timecode: secondsToTimecode(time, doc.media.frameRate),
          value: signalValueAt(signal, time),
        });
      }

      if (op !== undefined) {
        const ranges = signalRanges(signal, op, value ?? null, doc.media.duration).map((range) => ({
          ...range,
          startTimecode: secondsToTimecode(range.start, doc.media.frameRate),
          endTimecode: secondsToTimecode(range.end, doc.media.frameRate),
        }));
        return json({ name: signal.name, type: signal.type, op, value: value ?? null, count: ranges.length, ranges });
      }

      const start = from ?? 0;
      const end = to ?? doc.media.duration;
      const samples = thinSamples(signal, start, end, maxPoints ?? 400);

      return json({
        name: signal.name,
        type: signal.type,
        min: signal.min,
        max: signal.max,
        totalSamples: signal.count,
        returnedSamples: samples.length,
        thinned: samples.length < signal.samples.filter(([t]) => t >= start && t <= end).length,
        format: "[seconds, frame, value] - a step function; the value holds until the next sample",
        samples,
      });
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "find_windows",
  {
    title: "Find candidate clip ranges",
    description:
      "Proposes ranked time ranges worth cutting, each with a reason and the labels it contains. Strategies: " +
      "'markers' (moments the game flagged, highest trust), 'spans' (things that lasted), 'density' (where " +
      "the most happened at once), 'events' (one specific thing, padded), 'signal' (state-driven, e.g. health " +
      "below 20), 'chapters' (structural coverage). Ranges are padded, merged, clamped to the media and " +
      "penalised for stuttery footage. Scores are comparable only within one call.",
    inputSchema: {
      file: fileArg,
      strategy: z.enum(["markers", "events", "density", "spans", "signal", "chapters"]),
      minScore: z.number().min(0).max(1).optional().describe("Markers strategy: the lowest marker score to seed from."),
      windowSeconds: z.number().min(0.5).max(120).default(6).optional().describe("Density strategy: the burst width."),
      minCount: z.number().int().min(2).default(3).optional().describe("Density strategy: events needed inside the burst."),
      signal: z.string().optional().describe("Signal strategy: the channel name."),
      op: operatorSchema.optional().describe("Signal strategy: how to test the channel. Defaults to 'changes'."),
      value: valueSchema.optional().describe("Signal strategy: the value to test against."),
      pre: z.number().min(0).max(60).default(1.5).optional().describe("Seconds of lead-in before each moment."),
      post: z.number().min(0).max(60).default(2.5).optional().describe("Seconds of follow-through after it."),
      minDuration: z.number().min(0.1).max(600).default(1.5).optional(),
      maxDuration: z.number().min(0.2).max(3600).default(15).optional(),
      merge: z.boolean().default(true).optional().describe("Combine ranges that overlap. Turn off to keep each moment separate."),
      mergeGap: z.number().min(0).max(30).default(0).optional().describe("Also merge ranges separated by less than this."),
      avoidHitches: z.boolean().default(true).optional().describe("Penalise ranges containing long frames."),
      limit: z.number().int().min(1).max(200).default(10).optional(),
      ...selectorShape,
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async (args) => {
    try {
      const loaded = await openRecording(args.file, ROOTS);
      const spec: WindowSpec = { ...toSelector(args), ...args, strategy: args.strategy };
      const windows = findWindows(loaded, spec);

      return json({
        file: loaded.labelPath,
        media: loaded.mediaPath,
        frameRate: loaded.doc.media.frameRate,
        mediaDuration: loaded.doc.media.duration,
        strategy: args.strategy,
        count: windows.length,
        totalDuration: round(windows.reduce((sum, window) => sum + window.duration, 0)),
        windows,
        hint:
          windows.length === 0
            ? "Nothing matched. Call describe_recording to see the names and signals this recording actually has."
            : "Pass these windows straight to build_cut_list, or edit the list first.",
      });
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "build_cut_list",
  {
    title: "Build an edit decision",
    description:
      "Turns chosen ranges into an edit: ordered clips with source and record timecodes, a CMX3600 EDL that " +
      "Premiere and Resolve both import, and reference ffmpeg commands. Nothing is rendered or written; the " +
      "result is a decision for something else to execute. Use `targetDuration` to fill a fixed budget " +
      "highest-score-first.",
    inputSchema: {
      file: fileArg,
      windows: z
        .array(
          z.object({
            start: z.number().describe("In point, in seconds of media time."),
            end: z.number().describe("Out point, in seconds of media time."),
            score: z.number().default(1).optional(),
            reason: z.string().default("").optional(),
            chapter: z.string().optional(),
            worstFrameMs: z.number().optional(),
          })
        )
        .min(1)
        .describe("The ranges to cut, usually taken from find_windows."),
      title: z.string().optional(),
      targetDuration: z.number().min(0.5).optional().describe("Total seconds to fill, highest score first."),
      order: z.enum(["chronological", "score", "asGiven"]).default("chronological").optional(),
      handles: z.number().min(0).max(10).default(0).optional().describe("Extra seconds on both sides of every clip."),
      trimLastToFit: z.boolean().default(true).optional(),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ file, windows, title, targetDuration, order, handles, trimLastToFit }) => {
    try {
      const loaded = await openRecording(file, ROOTS);
      const fps = loaded.doc.media.frameRate;

      const shaped: Window[] = windows.map((window) => ({
        start: window.start,
        end: window.end,
        duration: round(window.end - window.start),
        startFrame: secondsToFrame(window.start, fps),
        endFrame: secondsToFrame(window.end, fps),
        startTimecode: secondsToTimecode(window.start, fps),
        endTimecode: secondsToTimecode(window.end, fps),
        score: window.score ?? 1,
        reason: window.reason ?? "",
        chapter: window.chapter,
        worstFrameMs: window.worstFrameMs,
      }));

      return json(
        buildCutList(loaded, shaped, { title, targetDuration, order, handles, trimLastToFit })
      );
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "timecode",
  {
    title: "Convert times",
    description:
      "Converts between seconds, frame numbers and HH:MM:SS:FF for a recording's frame rate, and reports " +
      "the game state at that moment. Accepts seconds, MM:SS or HH:MM:SS:FF.",
    inputSchema: {
      file: fileArg,
      time: z.union([z.number(), z.string()]).optional().describe("A time in seconds, MM:SS or HH:MM:SS:FF."),
      frame: z.number().int().min(0).optional().describe("A zero-based media frame number."),
      includeState: z.boolean().default(true).optional().describe("Also report every signal's value at that moment."),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ file, time, frame, includeState }) => {
    try {
      const loaded = await openRecording(file, ROOTS);
      const fps = loaded.doc.media.frameRate;

      if (time === undefined && frame === undefined) {
        throw new Error("Pass either 'time' or 'frame'.");
      }

      const seconds = frame !== undefined ? (fps > 0 ? frame / fps : 0) : parseTime(time!, fps);
      const chapter = loaded.doc.chapters.find((entry) => seconds >= entry.t && seconds < entry.endT);

      return json({
        seconds: round(seconds),
        frame: secondsToFrame(seconds, fps),
        timecode: secondsToTimecode(seconds, fps),
        clock: secondsToClock(seconds),
        frameRate: fps,
        withinMedia: seconds >= 0 && seconds <= loaded.doc.media.duration,
        chapter: chapter?.name,
        state: includeState === false ? undefined : stateAt(loaded.doc, seconds),
      });
    } catch (error) {
      return failure(error);
    }
  }
);

server.registerTool(
  "validate_labels",
  {
    title: "Check a label file",
    description:
      "Reports anything that would make a recording misleading to edit from: a missing media file, dropped " +
      "labels, spans left open, labels past the end of the video. Call this when a result looks wrong.",
    inputSchema: { file: fileArg },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ file }) => {
    try {
      const loaded = await openRecording(file, ROOTS);
      const report = validate(loaded);

      return json({
        file: loaded.labelPath,
        media: loaded.mediaPath,
        recoveredFromStream: loaded.recovered,
        ...report,
      });
    } catch (error) {
      return failure(error);
    }
  }
);

// ---------------------------------------------------------------------------------------------------------
// Resources and prompts
// ---------------------------------------------------------------------------------------------------------

server.registerResource(
  "editing-guide",
  "labeled-recorder://guide",
  {
    title: "Labeled Recorder editing guide",
    description: "How to turn a label file into a short or a trailer.",
    mimeType: "text/markdown",
  },
  async (uri) => ({ contents: [{ uri: uri.href, mimeType: "text/markdown", text: fullGuide() }] })
);

server.registerResource(
  "label-schema",
  "labeled-recorder://schema",
  {
    title: "Label file format",
    description: "The labeled-recorder/1 sidecar format.",
    mimeType: "text/markdown",
  },
  async (uri) => ({ contents: [{ uri: uri.href, mimeType: "text/markdown", text: SCHEMA_REFERENCE }] })
);

server.registerPrompt(
  "make_short",
  {
    title: "Cut a short",
    description: "Plan a vertical short from one recording, using its labels.",
    argsSchema: {
      file: z.string().describe("The recording to cut."),
      seconds: z.string().optional().describe("Target length. Defaults to 30."),
    },
  },
  ({ file, seconds }) => ({
    messages: [
      {
        role: "user" as const,
        content: {
          type: "text" as const,
          text:
            `Cut a ${seconds ?? "30"}-second short from the recording '${file}'.\n\n` +
            `Work in this order:\n` +
            `1. describe_recording, to learn this game's label vocabulary and signals.\n` +
            `2. find_windows with strategy 'markers' for the moments the game flagged, then again with ` +
            `strategy 'density' over whichever event category means action here.\n` +
            `3. Combine the two sets, drop ranges whose worstFrameMs is more than 2.5x the frame budget, ` +
            `and keep clips between 1.5 and 4 seconds.\n` +
            `4. build_cut_list with targetDuration ${seconds ?? "30"} and order 'chronological'.\n\n` +
            `Report the cut list with a one-line justification per clip, and say what you deliberately left out.`,
        },
      },
    ],
  })
);

server.registerPrompt(
  "make_trailer",
  {
    title: "Cut a trailer",
    description: "Plan a trailer from one recording, using its chapter structure for the arc.",
    argsSchema: {
      file: z.string().describe("The recording to cut."),
      seconds: z.string().optional().describe("Target length. Defaults to 90."),
    },
  },
  ({ file, seconds }) => ({
    messages: [
      {
        role: "user" as const,
        content: {
          type: "text" as const,
          text:
            `Cut a ${seconds ?? "90"}-second trailer from the recording '${file}'.\n\n` +
            `A trailer needs an arc, not just the best moments:\n` +
            `1. describe_recording, then find_windows with strategy 'chapters' to see the structure.\n` +
            `2. Take at least one range from each chapter for breadth, and the top-scoring markers for payoff.\n` +
            `3. Use strategy 'spans' for the climax, and take the end of that span rather than its start.\n` +
            `4. Weight the back half: build_cut_list with targetDuration ${seconds ?? "90"}, order ` +
            `'chronological', handles 0.5.\n\n` +
            `Explain the arc you chose, name the beat each clip serves, and flag any chapter you could not cover.`,
        },
      },
    ],
  })
);

// ---------------------------------------------------------------------------------------------------------

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  // stdout is the protocol channel, so anything human-readable has to go to stderr.
  console.error(`labeled-recorder MCP server ready. Searching: ${ROOTS.join(", ")}`);
}

main().catch((error) => {
  console.error("labeled-recorder MCP server failed to start:", error);
  process.exit(1);
});
