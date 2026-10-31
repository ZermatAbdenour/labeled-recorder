# The Recorder bridge

`RecorderSessionBridge.cs` has to be copied into your clone of Unity's Recorder package, at:

```
com.unity.recorder/Editor/Sources/Labeling/RecorderSessionBridge.cs
```

**Labeled Recorder does not compile without it.**

## Why it lives there and not here

The Recorder keeps `RecordingSession`, `RecorderComponent` and most of `RecorderSettings` `internal` to its
own assembly. Labeled Recorder needs to read that state — the output path a recording will write to, its
frame rate and resolution, and how many frames it has committed so far — to pair a label file with its video
and to map engine frames onto media frames.

The bridge solves this by being compiled *inside* `Unity.Recorder.Editor`, where those members are visible,
and re-exposing them as plain public data. Doing it any other way would mean reflecting over private members
every frame.

## Why this is safe to update around

The bridge is **additive**. It is a new file in a new folder; it changes no existing Recorder file and mutates
no Recorder state. `git status` in a fresh clone of the Recorder shows exactly one untracked directory and
zero modified files, so you can pull upstream Recorder updates without a merge conflict.

After updating the Recorder, copy this file in again if the update removed it.

## Version

Written against `com.unity.recorder` 5.1.7. It uses only long-standing Recorder internals, but if a future
Recorder release renames them this file is the one place that needs fixing.
