/**
 * The text this server exists to deliver: what a label file means, and how to turn one into a short or a
 * trailer. Served as a tool, a resource and the opening of each prompt, so an agent gets it whichever way it
 * reaches for help.
 */

export const SCHEMA_REFERENCE = `# Label file format (\`labeled-recorder/1\`)

A Unity Recorder capture that was made with the Labeled Recorder package has a sidecar file beside it:

    Recordings/arena_run_003.mp4
    Recordings/arena_run_003.labels.json

If a recording was interrupted, only \`arena_run_003.labels.jsonl\` exists — an append-as-you-go stream. This
server reads it and rebuilds the same structure, flagged with \`recoveredFromStream\`.

## The two conventions that matter

1. **\`t\` is media time and \`f\` is the media frame.** \`t\` is seconds from the first recorded frame of the
   video; \`f\` is the zero-based index of that frame. They are *not* game time or engine frames. You can use
   either as an in or out point without converting anything.
2. **Names are dotted and \`cat\` is the prefix.** \`combat.kill\` has \`cat: "combat"\`. This is how you reason
   about a game you know nothing about: ask for a category, not a name you guessed.

When a capture ran at a variable frame rate, labels also carry \`gt\` (game time). Ignore it unless you need to
line labels up against something outside the video.

## Sections

| Section | What it holds | Use it for |
|---|---|---|
| \`media\` | path, frame rate, resolution, frame count, duration, audio | converting times, checking the file is there |
| \`session\` | recorder name, take, scene, start time, and \`meta\` the game set | grouping takes, captions, filtering runs |
| \`stats\` | label counts, \`dropped\`, \`truncatedLabels\` | deciding whether to trust the file |
| \`chapters\` | named, non-overlapping stretches covering the whole take | coarse structure; never cut across one by accident |
| \`spans\` | things that lasted, with \`t\`/\`endT\`/\`dur\` and merged payload | ready-made clip ranges |
| \`markers\` | scored moments the game flagged as interesting | the first thing to rank |
| \`events\` | points in time with payloads | density, specificity, captions |
| \`signals\` | named game data as sparse step functions | state at a cut point, threshold moments |

## Signals are step functions

\`samples\` is an array of \`[t, f, value]\`, written only when the value changed. The value at any time is the
last sample at or before it, and \`null\` before the first sample. A channel that never changed has one sample.

Numeric channels carry \`min\` and \`max\`. Vector values arrive as arrays of 2, 3 or 4 numbers.

## Channels you get without the game doing anything

| Channel | Meaning |
|---|---|
| \`scene\` | active scene name |
| \`time.scale\` | \`Time.timeScale\`; a drop to 0 is a pause, below 1 is slow motion |
| \`perf.fps\` | average frame rate over the sampling interval |
| \`perf.hitchMs\` | the worst single frame in that interval, in milliseconds |
| \`cam.pos\`, \`cam.speed\`, \`cam.turnRate\` | main camera position, metres per second, degrees per second |
| \`cam.name\` | the active camera, which usually changes at a cut or a cutscene |

\`perf.hitchMs\` is the one to respect: footage where it is more than about 2.5x the frame budget
(\`1000 / media.frameRate\`) looks broken no matter how good the moment was. \`cam.speed\` and \`cam.turnRate\`
are a serviceable proxy for how much is happening on screen.
`;

export const EDITING_GUIDE = `# Cutting a short or a trailer from a label file

You have a video and a machine-readable record of everything that happened in it, timestamped to the frame.
The job is to pick ranges and justify them. Work in this order.

## 1. Look before you choose

Call \`describe_recording\` first, every time. It returns the event vocabulary, the signal inventory, the
chapter structure and the busiest ten seconds of the take. Until you have it you are guessing at names.

Then call \`validate_labels\` if anything looks off. It tells you whether the media is actually on disk,
whether labels were dropped, and whether spans were left open.

## 2. Find candidates, do not invent them

\`find_windows\` is the one tool that proposes ranges. Pick the strategy that matches the question:

- **\`markers\`** — the game said "this bit is good". Highest-trust source. Start here, filter with
  \`minScore\`.
- **\`spans\`** — something that lasted: a boss fight, a chase, a cutscene. Already a clip range; often you
  want a slice of it rather than the whole thing.
- **\`density\`** — where the most happened at once. Set \`namePrefix\` to the category that means action in
  this game (often \`combat.\`) and \`windowSeconds\` to the pace you want. This is how you find a montage.
- **\`events\`** — one specific thing, padded. Use when you know exactly what you want.
- **\`signal\`** — state-driven moments: \`player.health\` below 20, \`time.scale\` under 1, a weapon change.
- **\`chapters\`** — structural coverage, for a trailer that needs to show breadth.

Every strategy returns the same shape: padded, merged, clamped to the media, scored, with a \`reason\` string
and the events and markers the range contains. Scores are only comparable inside one call.

## 3. Shape the ranges deliberately

- \`pre\` and \`post\` decide what the cut reads as. A kill needs the half-second *before* it to make sense;
  default \`pre: 1.5\`, \`post: 2.5\` is a reasonable action default. For a reaction, invert it.
- \`minDuration\` below about 1.2s is unwatchable in isolation. For a fast montage, 1.2-2s per clip; for a
  trailer beat, 2-4s.
- \`merge\` is on by default, so two markers a second apart become one range. Turn it off when you want each
  moment as its own clip.
- \`maxDuration\` stops one long span from eating the whole budget.

## 4. Commit to an edit

\`build_cut_list\` turns chosen ranges into an edit decision: ordered clips with source and record timecodes, a
CMX3600 \`edl\` string that Premiere and Resolve both import, and reference ffmpeg commands.

- \`targetDuration\` fills the budget highest-score-first, then reorders. This is how you hit "30 seconds".
- \`order\` defaults to \`chronological\`, which is almost always what you want in the finished piece even
  though selection was by score.
- \`handles\` adds padding on both sides of every clip for the human who will add transitions.

Read the \`notes\` it returns. They flag stuttery clips and incomplete labels.

## Shapes that work

**30-second gameplay short.** \`find_windows\` with \`markers\`, then \`density\` over the action category;
combine both result sets, \`build_cut_list\` with \`targetDuration: 30\`, \`minDuration: 1.5\`,
\`maxDuration: 4\`, \`order: "chronological"\`. Open on the single highest-scoring range rather than the
chronologically first one if the first few seconds are quiet.

**90-second trailer.** One range per chapter for structure, plus the top markers for payoff. Ask for
\`chapters\` first to see the arc, then weight the back half: trailers escalate. Use \`spans\` for the boss or
climax and take its last few seconds, not its first.

**Highlight reel across several takes.** \`list_recordings\` to enumerate, then \`find_windows\` on each with
the same spec, then merge and sort by score yourself. Keep \`session.take\` and \`session.meta\` in your
captions so a human can find the source.

## Things that will catch you out

- A range is only as good as the footage. Check \`worstFrameMs\` on each window against
  \`1000 / media.frameRate\`.
- \`time.scale\` of 0 means the game was paused. Those seconds are a frozen frame on screen.
- An \`open: true\` span was cut off when the recording stopped; its \`endT\` is the end of the take, not a
  real ending.
- \`stats.dropped\` above zero means labels are missing, so absence of a marker proves nothing.
- Chapter boundaries are usually hard scene changes. Cutting across one is jarring unless you meant it.
- Times are media time already. Do not add the recording start time to them.
`;

export const QUICK_REFERENCE = `## Tools

| Tool | Answers |
|---|---|
| \`labels_guide\` | how this format works and how to edit from it |
| \`list_recordings\` | what recordings exist, and are their labels usable |
| \`describe_recording\` | what is in this take: vocabulary, signals, chapters, busiest stretch |
| \`query_events\` | when did X happen, with what payload |
| \`get_signal\` | what was the value of Y, over time or at one moment |
| \`find_windows\` | which ranges are worth cutting, and why |
| \`build_cut_list\` | turn chosen ranges into timecodes, an EDL and ffmpeg commands |
| \`timecode\` | convert between seconds, frames and HH:MM:SS:FF |
| \`validate_labels\` | is this file trustworthy |

Any tool that takes a \`file\` accepts the label file, the video beside it, or just the recording's name.
`;

/** The whole guide, in the order an agent should read it. */
export function fullGuide(): string {
  return [EDITING_GUIDE, SCHEMA_REFERENCE, QUICK_REFERENCE].join("\n\n---\n\n");
}
