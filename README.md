# Labeled Recorder

Writes a machine-readable label track beside every Unity Recorder capture, so an AI agent can cut shorts and
trailers from your gameplay footage without watching it.

You add one-line calls to your game code. They do nothing in a shipped build — the compiler deletes them —
and while a recording is running they produce `arena_run_003.labels.json` next to `arena_run_003.mp4`.

```csharp
GameLabel.Event("combat.kill", "weapon", "shotgun", "headshot", true);
GameLabel.Begin("boss.fight");  …  GameLabel.End("boss.fight", "won", true);
GameLabel.Set("player.health", health);          // every frame; written only on change
GameLabel.Highlight("triple kill", 0.9f);
GameLabel.Chapter("Level 2");
```

A companion MCP server ships in [`McpServer~/`](McpServer~/README.md) so an agent can read those files: list
recordings, describe what is in one, query events and game state, propose ranked clip ranges and build an edit
decision.

---

## This is not the Unity Recorder, and it is not a fork of it

Labeled Recorder is a **separate package that works alongside Unity's Recorder package**
([`com.unity.recorder`](https://docs.unity3d.com/Packages/com.unity.recorder@5.1/manual/index.html)). The
Recorder is Unity Technologies' package, published under the Unity Companion License — none of its code is in
this repository, and nothing here replaces it. Recording itself is still done entirely by the Recorder, from
the Recorder window or a Timeline Recorder clip, exactly as before.

Developed against Recorder 5.1.7.

What this package needs from the Recorder is visibility into a live recording session: which file it will
write, at what frame rate and resolution, and how many frames it has committed so far. The Recorder keeps all
of that `internal`. So Labeled Recorder ships one small bridge,
[`Editor/RecorderBridge/RecorderSessionBridge.cs`](Editor/RecorderBridge/RecorderSessionBridge.cs), with an
assembly definition reference that compiles it into the Recorder's own assembly, where it re-exposes that state
as plain public data.

The bridge **changes no Recorder file** and nothing has to be copied or cloned, so it works with the ordinary
registry install of the Recorder.

---

## Install

**1. Add the package** through **Package Manager > + > Add package from git URL**:

```
https://github.com/ZermatAbdenour/labeled-recorder.git
```

Unity installs the Recorder (5.1.7) for you as a dependency.

Use the **HTTPS** URL, not `git@github.com:…`. Unity runs git non-interactively, so an SSH URL fails with
`Host key verification failed` unless you already have an SSH key and `github.com` in `~/.ssh/known_hosts`.

A git-URL package is read-only and pinned to a commit in `Library/PackageCache`. If you intend to edit this
package, clone it and add it to `Packages/manifest.json` with a `file:` path instead:

```json
{
  "dependencies": {
    "com.labeledrecorder": "file:../../LabeledRecorder/com.labeledrecorder"
  }
}
```

**2. Check the switch.** Label capture is on by default:
**Project Settings > Labeled Recorder > Enable Label Capture**. The same toggle is on the
**Tools > Labeled Recorder** menu.

Requires Unity 6000.0 or newer and `com.unity.recorder` 5.1.7.

## Use

1. Add `using LabeledRecorder;` to a gameplay script and call `GameLabel` wherever something interesting
   happens. Scripts in `Assembly-CSharp` need nothing further; if your game code lives in its own assembly
   definition, add `LabeledRecorder.Runtime` to that asmdef's references.
2. Record as you normally would.
3. When the recording stops, the label file is written next to the video.

Import the **Quick Start** sample from the Package Manager for a script showing every call shape.

## The API

Everything on `GameLabel` carries `[Conditional("UNITY_EDITOR")]`, so in a player build the C# compiler
removes the call *and the expressions you passed to it*. No `#if` blocks, no branch, no string building, no
cost. In the Editor, outside a recording, each call is a single static `bool` read.

| Call | Lands in | For |
|---|---|---|
| `Event(name, …)` | `events` | a moment: a kill, a pickup, a death |
| `Begin(name, …)` / `End(name, …)` | `spans` | something that lasts: a fight, a chase, a cutscene |
| `Span(name)` in a `using` block | `spans` | the same, closed automatically |
| `Set(name, value)` | `signals` | game data; call every frame, a sample is written only on change |
| `Set(name, value, epsilon)` | `signals` | a number that moves constantly but matters in steps |
| `Sample(name, value)` | `signals` | a value written every time, change or not |
| `Mark(name, score)` | `markers` | a scored moment |
| `Highlight(note, score)` | `markers` | a scored moment with a caption |
| `Chapter(name)` | `chapters` | the coarse structure of the take |
| `Meta(key, value)` | `session.meta` | a fact about the whole run: build, difficulty, seed |
| `IsRecording` | — | guard genuinely expensive payload work |

Use dotted names. Everything before the first dot becomes the label's category, which is what lets a tool
reason about your game without being taught it.

Payload values accept `float`, `int`, `double`, `long`, `bool`, `string`, `Vector2`, `Vector3`, `Color` and
`Quaternion` directly, up to four key/value pairs per call. For an enum, `LabelValue.Of(myEnum)` records the
member name without the allocation `ToString()` would cost.

## What you get for free

Even with no calls in your game, every recording captures the active scene and scene changes, `Time.timeScale`
(so pauses and slow motion are visible), frame pacing (`perf.fps` and `perf.hitchMs`, so an editor can avoid
cutting to footage that stuttered), and main camera position, speed and turn rate. Each is switchable in
Project Settings.

## Performance

- **Player builds:** zero. The calls are not compiled in.
- **Editor, not recording:** one static `bool` read per call.
- **Editor, recording:** one struct written into a pre-allocated lock-free ring buffer. No allocation, no file
  I/O on the game thread, safe to call from jobs and background threads. If the buffer fills, labels are
  dropped rather than stalling the game, and the count is written into the label file so you know the record
  is incomplete.

The frame clock is published from an injected player-loop callback that runs before any game script, which is
why a label pushed from a job thread still lands on the right frame.

## Settings

**Project Settings > Labeled Recorder**

| Setting | Default | Notes |
|---|---|---|
| Enable Label Capture | on | the master switch |
| Output Location | Next To Media | keeps labels paired with their video |
| Write Stream File | on | append-as-you-go `.labels.jsonl`, so a crash still leaves a usable record |
| Keep Stream File | off | keep it after the final file is written |
| Pretty Print | on | turn off for very long recordings |
| Scene Changes / Time Scale / Frame Pacing / Camera Motion | on | the automatic channels |
| Buffer Capacity | 4096 | raise only if a label file reports dropped labels |
| Max Labels Per Recording | 500000 | in-memory ceiling |

## Reading the files

The format is documented in [`Documentation~/label-file-format.md`](Documentation~/label-file-format.md), and
**Tools > Labeled Recorder > Label File Format** opens it.

For the MCP server, see [`McpServer~/README.md`](McpServer~/README.md), or use
**Tools > Labeled Recorder > Copy MCP Server Config** to get a ready-to-paste client configuration.

```bash
cd McpServer~
npm install && npm run build
node dist/selftest.js --root=/path/to/your/UnityProject
```

## Hooking in your own tooling

```csharp
LabeledRecorder.Editor.LabelCapture.LabelFileWritten += path => { /* … */ };
```

## Troubleshooting

**`The type or namespace name 'Labeling' does not exist in the namespace 'UnityEditor.Recorder'`**
You are on version 1.0.0, which needed the bridge copied into a clone of the Recorder. Update to 1.0.1 or newer;
for a git-URL install, remove the `com.labeledrecorder` entry from `Packages/packages-lock.json` so Unity fetches
the latest commit.

**`Host key verification failed` when adding the package from a git URL.** You used the SSH URL
(`git@github.com:…`). Use the HTTPS one: `https://github.com/ZermatAbdenour/labeled-recorder.git`.

**The label file is not written.** Check that **Enable Label Capture** is on, and turn on **Verbose Logging**
in Project Settings — it logs every recording start, stop and file write to the Console.

**Unity generated a pile of `.meta` files.** Expected: this repository has none yet, because they are created
the first time the package is imported into a project. If you fork this and intend to share your fork, commit
them — Unity uses the GUIDs inside them to keep asset references stable.

**`stats.dropped` is above zero.** Labels were lost because the capture buffer filled up. Raise
**Buffer Capacity**.

**Only a `.labels.jsonl` file exists.** The recording was interrupted. The MCP server reads that file and
rebuilds the same structure, flagged as recovered.

## Licence

MIT — see [LICENSE](LICENSE).

Unity, Unity Recorder and `com.unity.recorder` are trademarks or products of Unity Technologies. This project
is not affiliated with or endorsed by Unity Technologies, and contains none of their code.
