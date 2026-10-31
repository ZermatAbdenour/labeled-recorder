#!/usr/bin/env node
/**
 * Runs the whole analysis path over one recording and prints what it found.
 *
 * This is the quickest way to confirm the server can see your recordings and that a label file is usable,
 * without wiring up an MCP client:
 *
 *     node dist/selftest.js "C:/Game/Recordings/arena_run_003.labels.json"
 *     node dist/selftest.js            # picks the newest recording under the configured roots
 */
import { buildCutList, describe, findWindows, validate } from "./analyze.js";
import { findLabelFiles, openRecording, resolveRoots } from "./load.js";
import { secondsToClock } from "./timecode.js";

async function main(): Promise<number> {
  const argv = process.argv.slice(2);
  const roots = resolveRoots(argv.filter((entry) => entry.startsWith("--root=")).map((entry) => entry.slice(7)));
  const named = argv.find((entry) => !entry.startsWith("--"));

  let target = named;
  if (!target) {
    const [newest] = await findLabelFiles({ roots, limit: 1 });
    if (!newest) {
      console.error(`No label files found under: ${roots.join(", ")}`);
      return 1;
    }
    target = newest;
  }

  const loaded = await openRecording(target, roots);
  const summary = describe(loaded);
  const report = validate(loaded);

  console.log(`Recording   ${loaded.labelPath}`);
  console.log(`Media       ${loaded.mediaPath ?? "(not found on disk)"}`);
  console.log(
    `Timeline    ${secondsToClock(summary.media.duration)} at ${summary.media.frameRate} fps ` +
      `(${summary.media.frameCount} frames)`
  );
  console.log(
    `Labels      ${summary.stats.labels} total; ${summary.eventVocabulary.length} event name(s), ` +
      `${summary.markers.length} marker(s), ${summary.spans.length} span(s), ` +
      `${summary.signals.length} signal(s), ${summary.chapters.length} chapter(s)`
  );

  if (summary.chapters.length > 0) {
    console.log(`Chapters    ${summary.chapters.map((chapter) => chapter.name).join(" -> ")}`);
  }

  for (const problem of report.problems) console.log(`PROBLEM     ${problem}`);
  for (const warning of report.warnings) console.log(`warning     ${warning}`);

  const windows = findWindows(loaded, { strategy: "markers", limit: 5 });
  const fallback = windows.length > 0 ? windows : findWindows(loaded, { strategy: "density", limit: 5 });

  console.log(`\nTop candidate ranges (${fallback.length}):`);
  for (const window of fallback) {
    console.log(
      `  ${window.startTimecode} - ${window.endTimecode}  ` +
        `${window.duration.toFixed(2)}s  score ${window.score.toFixed(2)}  ${window.reason}`
    );
  }

  if (fallback.length > 0) {
    const cut = buildCutList(loaded, fallback, { targetDuration: 30 });
    console.log(`\nCut list: ${cut.clipCount} clip(s), ${cut.totalDuration.toFixed(2)}s total`);
    for (const note of cut.notes) console.log(`  note: ${note}`);
  }

  return report.ok ? 0 : 2;
}

main().then(
  (code) => process.exit(code),
  (error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
);
