# Label file format — `labeled-recorder/1`

Every Unity Recorder capture made while label capture is enabled gets a sidecar file beside it:

```
Recordings/arena_run_003.mp4
Recordings/arena_run_003.labels.json
```

While the recording is running, labels are also appended to `arena_run_003.labels.jsonl`, one JSON object per
line. If the Editor crashes or you force-quit play mode, that file is all that survives — the MCP server reads
it and rebuilds the same structure, flagged as recovered. On a clean stop the stream file is deleted unless
**Keep Stream File** is on.

## The two conventions

1. **`t` is media time and `f` is the media frame.** `t` is seconds from the first recorded frame of the
   video; `f` is the zero-based index of that frame in the file. Neither is game time or an engine frame
   number. Both can be used directly as in and out points.
2. **Names are dotted; `cat` is the part before the first dot.** `combat.kill` has `cat: "combat"`. This is
   what lets a tool reason about groups of labels in a game it knows nothing about.

When the capture ran at a variable frame rate, labels also carry `gt`, the game time of the same moment.

## Example

```json
{
  "schema": "labeled-recorder/1",
  "generator": { "package": "com.labeledrecorder", "version": "1.0.0", "unity": "6000.0.52f1" },

  "media": {
    "path": "Recordings/arena_run_003.mp4",
    "absolutePath": "C:/Game/Recordings/arena_run_003.mp4",
    "kind": "movie",
    "extension": "mp4",
    "imageSequence": false,
    "width": 1920, "height": 1080, "aspect": 1.777778,
    "frameRate": 60,
    "frameRatePlayback": "Constant",
    "captureEveryNthFrame": 1,
    "frameCount": 1200,
    "duration": 20,
    "hasAudio": true
  },

  "session": {
    "id": "6f2c…", "recorder": "Movie Recorder", "recorderType": "MovieRecorderSettings",
    "recordMode": "Manual", "take": 3,
    "startedAt": "2026-10-31T14:05:00.000Z",
    "scene": "Arena", "product": "My Game", "platform": "WindowsEditor",
    "meta": { "difficulty": "nightmare", "build": "0.8.4-rc2", "seed": 918273 }
  },

  "stats": {
    "labels": 53, "events": 14, "markers": 2, "spans": 1, "chapters": 3, "signals": 4,
    "mediaFrames": 1200, "dropped": 0, "truncatedLabels": 0
  },

  "chapters": [
    { "name": "Arena Entry", "t": 0,  "f": 0,   "endT": 5,  "endF": 300, "dur": 5 },
    { "name": "Wave 1",      "t": 5,  "f": 300, "endT": 13, "endF": 780, "dur": 8 },
    { "name": "Boss",        "t": 13, "f": 780, "endT": 19.983, "endF": 1199, "dur": 6.983 }
  ],

  "spans": [
    {
      "name": "boss.fight", "cat": "boss",
      "t": 13, "f": 780, "endT": 19, "endF": 1140, "dur": 6,
      "data": { "name": "Gravelord", "won": true, "timeTaken": 6 }
    }
  ],

  "markers": [
    {
      "name": "highlight", "cat": "highlight",
      "t": 10.666667, "f": 640, "score": 0.95,
      "data": { "note": "triple kill from the rafters" }
    }
  ],

  "events": [
    {
      "name": "combat.kill", "cat": "combat",
      "t": 1.616667, "f": 97,
      "data": { "weapon": "pistol", "headshot": true }
    }
  ],

  "signals": [
    {
      "name": "player.health", "cat": "player", "type": "number",
      "count": 11, "min": 54.64, "max": 100,
      "samples": [ [0, 0, 100], [10.25, 615, 74.8], [15.083333, 905, 100] ]
    }
  ]
}
```

## Sections

### `media`

| Field | Meaning |
|---|---|
| `path` | project-relative when the recording lives inside the project, otherwise absolute |
| `kind` | `movie`, `imageSequence`, `aovSequence`, `audio`, `animation`, `other` |
| `frameRate` | the output frame rate; divide `f` by this to get `t` |
| `frameRatePlayback` | `Constant` (locked) or `Variable` |
| `captureEveryNthFrame` | only every Nth source frame was captured |
| `frameCount` / `duration` | how long the finished media is |

For an image sequence, `path` has the frame number stripped: `shot_0001.png` is reported as `shot`.

### `session`

Identifies the take. `meta` holds whatever the game passed to `GameLabel.Meta` — build number, difficulty,
seed, character, test condition.

### `stats`

`dropped` above zero means the capture buffer filled up and labels were lost; raise **Buffer Capacity** in
**Project Settings > Labeled Recorder**. `truncatedLabels` above zero means the per-recording ceiling was hit.
Either way the file is incomplete, so the absence of a label proves nothing.

### `chapters`

Non-overlapping, end to end, covering the whole recording. Each one ends where the next begins; the last ends
at the last recorded frame. Chapter boundaries are usually hard scene changes.

### `spans`

A `GameLabel.Begin` paired with its `GameLabel.End`. Nesting is supported, pairing is by name, and `data`
merges both calls with the closing one winning — so outcomes belong on `End`. A span still open when the
recording stopped carries `"open": true` and an `endT` at the end of the take, which is not a real ending.

An `End` with no matching `Begin` is kept as a zero-length span rather than discarded.

### `markers`

Scored moments. `score` is whatever the game passed, nominally 0 to 1. These are the highest-trust input to an
automatic edit, because they are the moments a human decided were worth keeping.

### `events`

Points in time with up to four payload fields. Values are JSON numbers, booleans or strings; vectors and
colours arrive as arrays of 2, 3 or 4 numbers.

### `signals`

Named game data as a sparse step function. `samples` is `[t, f, value]`, written only when the value changed,
so **the value at any time is the last sample at or before it**, and `null` before the first sample.

Numeric channels carry `min` and `max`. A channel that never changed has exactly one sample.

#### Channels captured without any code in your game

| Channel | Meaning |
|---|---|
| `scene` | the active scene name |
| `time.scale` | `Time.timeScale`; 0 is paused, below 1 is slow motion |
| `perf.fps` | average frame rate over the sampling interval |
| `perf.hitchMs` | the worst single frame in that interval, in milliseconds |
| `cam.name` | the active main camera |
| `cam.pos` | main camera position as `[x, y, z]` |
| `cam.speed` | metres per second |
| `cam.turnRate` | degrees per second |

Each can be turned off in **Project Settings > Labeled Recorder**.

`perf.hitchMs` is the one worth respecting when cutting: footage where it exceeds roughly 2.5× the frame
budget (`1000 / media.frameRate`) looks broken however good the moment was. `cam.speed` and `cam.turnRate` are
a serviceable proxy for how much is happening on screen.

## The stream file

`*.labels.jsonl` has a header line followed by one line per label:

```jsonl
{"op":"header","schema":"labeled-recorder/1","media":"…/arena_run_003.mp4","frameRate":60,…}
{"op":"chapter","name":"Arena Entry","t":0,"f":0}
{"op":"signal","name":"player.health","t":0,"f":0,"v":100}
{"op":"event","name":"combat.kill","t":1.616667,"f":97,"data":{"weapon":"pistol","headshot":true}}
{"op":"spanBegin","name":"boss.fight","t":13,"f":780,"data":{"name":"Gravelord"}}
{"op":"marker","name":"highlight","t":10.666667,"f":640,"score":0.95,"data":{"note":"triple kill"}}
```

`op` is one of `header`, `event`, `marker`, `spanBegin`, `spanEnd`, `signal`, `chapter`, `meta`. A truncated
final line is expected after a crash and is skipped.

## How a time is decided

The Editor publishes the frame clock before any game script runs, so a label pushed from a job thread is
stamped with the frame it belongs to. At the start of each frame the driver also records how many frames the
Recorder has committed so far — which is the index of the frame being produced right now. A label captured
during that frame therefore lands on exactly that media frame, whether the recorder is skipping frames, is
still waiting for its start frame, or is running at a variable rate.

`t` is then `f / media.frameRate`, because the output file always plays at `frameRate` regardless of how
quickly the Editor produced the frames.
