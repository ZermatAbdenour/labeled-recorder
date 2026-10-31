# Labeled Recorder MCP server

Gives an editing agent the timecode context that the Labeled Recorder Unity package writes alongside every
Unity Recorder capture: what happened, when, and what the game's state was at that moment.

It reads label files and answers questions about them. It renders nothing, encodes nothing, and writes
nothing — `build_cut_list` returns an edit decision for something else to execute.

## Build

```bash
cd com.labeledrecorder/McpServer~
npm install
npm run build
```

Check it can see your recordings:

```bash
node dist/selftest.js --root=C:/Game
```

That prints the newest recording's duration, label inventory and top candidate ranges, and flags anything that
would make the file misleading to edit from.

## Register it

**Tools > Labeled Recorder > Copy MCP Server Config** in Unity puts the right block on your clipboard. It
looks like this:

```json
{
  "mcpServers": {
    "labeled-recorder": {
      "command": "node",
      "args": ["C:/Game/Packages/com.labeledrecorder/McpServer~/dist/index.js"],
      "env": {
        "LABELED_RECORDER_ROOTS": "C:/Game"
      }
    }
  }
}
```

`LABELED_RECORDER_ROOTS` is where to look for recordings — one or more folders separated by `;` on Windows or
`:` elsewhere. Extra command-line arguments are treated as additional roots. Without it the server searches
its working directory.

For Claude Code: `claude mcp add labeled-recorder --env LABELED_RECORDER_ROOTS=C:/Game -- node <path>/dist/index.js`

## Tools

| Tool | Answers |
|---|---|
| `labels_guide` | how the format works and how to edit from it — read this first |
| `list_recordings` | what recordings exist, and are their labels usable |
| `describe_recording` | what is in this take: vocabulary, signals, chapters, busiest stretch |
| `query_events` | when did X happen, with what payload |
| `get_signal` | what was the value of Y, over time, at one moment, or where it crossed a threshold |
| `find_windows` | which ranges are worth cutting, and why |
| `build_cut_list` | turn chosen ranges into timecodes, a CMX3600 EDL and reference ffmpeg commands |
| `timecode` | convert between seconds, frames and `HH:MM:SS:FF`, and report game state there |
| `validate_labels` | is this file trustworthy |

Every tool that takes a `file` accepts the label file, the video beside it, or just the recording's name.

It also serves two resources (`labeled-recorder://guide`, `labeled-recorder://schema`) and two prompts
(`make_short`, `make_trailer`).

## `find_windows`

The one tool that proposes ranges. Pick the strategy that matches the question:

- **`markers`** — moments the game flagged with `GameLabel.Highlight` or `Mark`. Highest trust; start here.
- **`spans`** — things that lasted: a boss fight, a chase, a cutscene. Already a clip range.
- **`density`** — where the most happened at once. Set `namePrefix` to the category that means action in this
  game and `windowSeconds` to the pace you want. This is how you find a montage.
- **`events`** — one specific thing, padded.
- **`signal`** — state-driven: health below 20, a weapon change, slow motion.
- **`chapters`** — structural coverage, for a trailer that needs breadth.

All of them return the same shape, already padded by `pre`/`post`, merged, clamped to the media, scored, and
penalised when `perf.hitchMs` says the footage stuttered. Each window carries a `reason` and the labels it
contains, so an agent can justify the cut. Scores are comparable only within one call.

Chapters and spans are not merged by default, because merging would collapse the very structure you asked for.

## Interrupted recordings

If the Editor crashed, only `*.labels.jsonl` exists. The server reads it, rebuilds the same structure — spans
paired, chapters chained, anything still open closed at the last timestamp — and flags the result with
`recoveredFromStream`. A truncated final line is expected and skipped.

## Development

```bash
npm run watch     # recompile on change
npm run selftest  # exercise the whole analysis path over one recording
```

Source layout: `types.ts` (the file format), `load.ts` (discovery, caching, crash recovery), `timecode.ts`,
`analyze.ts` (queries, windows, cut lists), `guide.ts` (the text served to agents), `index.ts` (MCP wiring).
