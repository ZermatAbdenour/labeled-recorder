using System;
using System.Collections.Generic;
using System.Globalization;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Turns a <see cref="LabelRecording"/> into the sidecar label file, and into the one-line-per-label stream
    /// written while the recording is still running.
    /// </summary>
    /// <remarks>
    /// The format is documented in <c>Documentation~/label-file-format.md</c> and is what the companion MCP server
    /// reads. Times are seconds from the first recorded frame and frames are zero-based indices into the recorded
    /// file, so both can be used directly as in and out points.
    /// </remarks>
    public static class LabelTrackSerializer
    {
        /// <summary>The schema identifier written into every label file.</summary>
        public const string Schema = "labeled-recorder/1";

        /// <summary>The version of the package that produced the file.</summary>
        public const string PackageVersion = "1.1.0";

        /// <summary>
        /// Serializes a finished recording.
        /// </summary>
        /// <param name="recording">The capture to serialize.</param>
        /// <param name="mediaFrameCount">How many frames the recorder committed.</param>
        /// <param name="settings">Project settings, for pretty printing.</param>
        /// <returns>The complete label file as JSON.</returns>
        public static string ToJson(LabelRecording recording, int mediaFrameCount,
            LabeledRecorderSettings settings)
        {
            var context = recording.Context;
            var track = recording.Track;
            var variable = context.IsVariableRate;
            var writer = new LabelJsonWriter(settings.PrettyPrint, 1 << 18);

            writer.BeginObject();
            writer.Str("schema", Schema);

            writer.BeginObject("generator");
            writer.Str("package", "com.labeledrecorder");
            writer.Str("version", PackageVersion);
            writer.Str("unity", context.UnityVersion);
            writer.EndObject();

            WriteMedia(writer, context, mediaFrameCount, recording);
            WriteSession(writer, context, track);
            WriteStats(writer, track, mediaFrameCount);

            WriteChapters(writer, track);
            WriteSpans(writer, track);
            WriteMarkers(writer, track, variable);
            WriteEvents(writer, track, variable);
            WriteSignals(writer, track);

            writer.EndObject();
            return writer.ToString();
        }

        /// <summary>
        /// The first line of the stream file: enough context to interpret the lines that follow if the recording
        /// never reaches a clean stop.
        /// </summary>
        /// <param name="recording">The capture being streamed.</param>
        /// <returns>A single JSON object, without a trailing newline.</returns>
        public static string StreamHeader(LabelRecording recording)
        {
            var context = recording.Context;
            var writer = new LabelJsonWriter(false, 512);

            writer.BeginObject();
            writer.Str("op", "header");
            writer.Str("schema", Schema);
            writer.Str("media", LabelPaths.ToProjectRelative(context.MediaPath));
            writer.Num("frameRate", context.FrameRate);
            writer.Str("frameRatePlayback", context.FrameRatePlayback);
            writer.Int("width", context.Width);
            writer.Int("height", context.Height);
            writer.Str("recorder", context.RecorderName);
            writer.Str("startedAt", Iso(context.StartedAtUtc));
            writer.EndObject();

            return writer.ToString();
        }

        /// <summary>
        /// Serializes one label as a single JSON line for the stream file.
        /// </summary>
        /// <param name="placed">The label to write.</param>
        /// <param name="includeGameTime">True to include the game-time stamp, for variable frame rate captures.</param>
        /// <returns>A single JSON object, without a trailing newline.</returns>
        public static string StreamLine(in PlacedLabel placed, bool includeGameTime)
        {
            var writer = new LabelJsonWriter(false, 256);

            writer.BeginObject();
            writer.Str("op", OpName(placed.Record.Op));
            writer.Str("name", placed.Record.Name);
            writer.Num("t", placed.MediaTime);
            writer.Int("f", placed.MediaFrame);

            if (includeGameTime)
                writer.Num("gt", placed.GameTime);

            if (placed.Record.Op == LabelOp.Marker)
                writer.Num("score", placed.Record.Score);

            if (placed.Record.Op == LabelOp.Signal || placed.Record.Op == LabelOp.Meta)
                writer.Value("v", placed.Record.V0);
            else
                WriteData(writer, placed.Record);

            writer.EndObject();
            return writer.ToString();
        }

        // -----------------------------------------------------------------------------------------------------

        static void WriteMedia(LabelJsonWriter writer, RecordingContext context, int frameCount,
            LabelRecording recording)
        {
            writer.BeginObject("media");
            writer.Str("path", LabelPaths.ToProjectRelative(context.MediaPath));
            writer.Str("absolutePath", context.MediaPath);
            writer.Str("kind", MediaKind(context));
            writer.Str("extension", context.Extension);
            writer.Bool("imageSequence", context.IsImageSequence);

            if (context.Width > 0 && context.Height > 0)
            {
                writer.Int("width", context.Width);
                writer.Int("height", context.Height);
                writer.Num("aspect", context.Height == 0 ? 0d : (double)context.Width / context.Height);
            }

            writer.Num("frameRate", context.FrameRate);
            writer.Str("frameRatePlayback", context.FrameRatePlayback);
            writer.Int("captureEveryNthFrame", context.CaptureEveryNthFrame);
            writer.Int("frameCount", frameCount);
            writer.Num("duration", recording.ToMediaTime(frameCount));

            if (context.HasAudio.HasValue)
                writer.Bool("hasAudio", context.HasAudio.Value);

            writer.EndObject();
        }

        static void WriteSession(LabelJsonWriter writer, RecordingContext context, LabelTrack track)
        {
            writer.BeginObject("session");
            writer.Str("id", context.SessionId);
            writer.Str("recorder", context.RecorderName);
            writer.Str("recorderType", context.RecorderType);
            writer.Str("recordMode", context.RecordMode);
            writer.Int("take", context.Take);
            writer.Str("startedAt", Iso(context.StartedAtUtc));
            writer.Str("scene", context.Scene);
            writer.Str("product", context.ProductName);
            writer.Str("platform", context.Platform);

            if (track.Meta.Count > 0)
            {
                writer.BeginObject("meta");
                for (var i = 0; i < track.Meta.Count; i++)
                    writer.Value(track.Meta[i].Key, track.Meta[i].Value);
                writer.EndObject();
            }

            writer.EndObject();
        }

        static void WriteStats(LabelJsonWriter writer, LabelTrack track, int frameCount)
        {
            writer.BeginObject("stats");
            writer.Int("labels", track.AcceptedCount);
            writer.Int("events", track.Events.Count);
            writer.Int("markers", track.Markers.Count);
            writer.Int("spans", track.Spans.Count);
            writer.Int("chapters", track.Chapters.Count);
            writer.Int("signals", track.Signals.Count);
            writer.Int("mediaFrames", frameCount);
            writer.Int("dropped", LabelSink.DroppedCount);
            writer.Int("truncatedLabels", track.TruncatedCount);
            writer.Bool("stoppedAtLabelLimit", track.StoppedAtCap);

            if (LabelSink.DroppedCount > 0)
            {
                writer.Str("warning", "Some labels were dropped because the capture buffer filled up. " +
                    "Raise Buffer Capacity in Project Settings > Labeled Recorder.");
            }

            writer.EndObject();
        }

        static void WriteChapters(LabelJsonWriter writer, LabelTrack track)
        {
            writer.BeginArray("chapters");

            for (var i = 0; i < track.Chapters.Count; i++)
            {
                var chapter = track.Chapters[i];
                writer.BeginObject();
                writer.Str("name", chapter.Name);
                writer.Num("t", chapter.StartTime);
                writer.Int("f", chapter.StartFrame);
                writer.Num("endT", chapter.EndTime);
                writer.Int("endF", chapter.EndFrame);
                writer.Num("dur", Math.Max(0d, chapter.EndTime - chapter.StartTime));
                writer.EndObject();
            }

            writer.EndArray();
        }

        static void WriteSpans(LabelJsonWriter writer, LabelTrack track)
        {
            writer.BeginArray("spans");

            for (var i = 0; i < track.Spans.Count; i++)
            {
                var span = track.Spans[i];
                writer.BeginObject();
                writer.Str("name", span.Name);
                writer.Str("cat", Category(span.Name));
                writer.Num("t", span.StartTime);
                writer.Int("f", span.StartFrame);
                writer.Num("endT", span.EndTime);
                writer.Int("endF", span.EndFrame);
                writer.Num("dur", span.Duration);

                if (span.Open)
                    writer.Bool("open", true);

                WriteFields(writer, span.Fields);
                writer.EndObject();
            }

            writer.EndArray();
        }

        static void WriteMarkers(LabelJsonWriter writer, LabelTrack track, bool variable)
        {
            writer.BeginArray("markers");

            for (var i = 0; i < track.Markers.Count; i++)
            {
                var marker = track.Markers[i];
                writer.BeginObject();
                writer.Str("name", marker.Record.Name);
                writer.Str("cat", Category(marker.Record.Name));
                writer.Num("t", marker.MediaTime);
                writer.Int("f", marker.MediaFrame);
                writer.Num("score", marker.Record.Score);

                if (variable)
                    writer.Num("gt", marker.GameTime);

                WriteData(writer, marker.Record);
                writer.EndObject();
            }

            writer.EndArray();
        }

        static void WriteEvents(LabelJsonWriter writer, LabelTrack track, bool variable)
        {
            writer.BeginArray("events");

            for (var i = 0; i < track.Events.Count; i++)
            {
                var entry = track.Events[i];
                writer.BeginObject();
                writer.Str("name", entry.Record.Name);
                writer.Str("cat", Category(entry.Record.Name));
                writer.Num("t", entry.MediaTime);
                writer.Int("f", entry.MediaFrame);

                if (variable)
                    writer.Num("gt", entry.GameTime);

                WriteData(writer, entry.Record);
                writer.EndObject();
            }

            writer.EndArray();
        }

        static void WriteSignals(LabelJsonWriter writer, LabelTrack track)
        {
            writer.BeginArray("signals");

            foreach (var pair in track.Signals)
            {
                var channel = pair.Value;

                writer.BeginObject();
                writer.Str("name", channel.Name);
                writer.Str("cat", Category(channel.Name));
                writer.Str("type", TypeName(channel.Kind));
                writer.Int("count", channel.Samples.Count);

                if (channel.Kind == LabelValueKind.Number && channel.Min <= channel.Max)
                {
                    writer.Num("min", channel.Min);
                    writer.Num("max", channel.Max);
                }

                // Each sample is [time, frame, value]; a step function, so a value holds until the next sample.
                writer.BeginArray("samples");
                for (var i = 0; i < channel.Samples.Count; i++)
                {
                    var sample = channel.Samples[i];
                    writer.BeginArray();
                    writer.Num(null, sample.MediaTime);
                    writer.Int(null, sample.MediaFrame);
                    writer.Value(null, sample.Value);
                    writer.EndArray();
                }
                writer.EndArray();

                writer.EndObject();
            }

            writer.EndArray();
        }

        static void WriteData(LabelJsonWriter writer, in LabelRecord record)
        {
            if (record.FieldCount == 0)
                return;

            var opened = false;

            for (var i = 0; i < record.FieldCount; i++)
            {
                record.GetField(i, out var key, out var value);
                if (string.IsNullOrEmpty(key))
                    continue;

                if (!opened)
                {
                    writer.BeginObject("data");
                    opened = true;
                }

                writer.Value(key, value);
            }

            if (opened)
                writer.EndObject();
        }

        static void WriteFields(LabelJsonWriter writer, List<KeyValuePair<string, LabelValue>> fields)
        {
            if (fields == null || fields.Count == 0)
                return;

            writer.BeginObject("data");
            for (var i = 0; i < fields.Count; i++)
                writer.Value(fields[i].Key, fields[i].Value);
            writer.EndObject();
        }

        /// <summary>
        /// The part of a dotted label name before the first dot, which groups related labels.
        /// </summary>
        /// <param name="name">The label name.</param>
        /// <returns>The category, or the whole name when it contains no dot.</returns>
        public static string Category(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var dot = name.IndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        static string MediaKind(RecordingContext context)
        {
            switch (context.RecorderType)
            {
                case "MovieRecorderSettings": return "movie";
                case "GIFRecorderSettings": return "movie";
                case "ImageRecorderSettings": return "imageSequence";
                case "AOVRecorderSettings": return "aovSequence";
                case "AudioRecorderSettings": return "audio";
                case "AnimationRecorderSettings": return "animation";
                default: return context.IsImageSequence ? "imageSequence" : "other";
            }
        }

        static string TypeName(LabelValueKind kind)
        {
            switch (kind)
            {
                case LabelValueKind.Number: return "number";
                case LabelValueKind.Bool: return "bool";
                case LabelValueKind.Text: return "string";
                case LabelValueKind.Vector: return "vector";
                default: return "null";
            }
        }

        static string OpName(LabelOp op)
        {
            switch (op)
            {
                case LabelOp.Event: return "event";
                case LabelOp.SpanBegin: return "spanBegin";
                case LabelOp.SpanEnd: return "spanEnd";
                case LabelOp.Signal: return "signal";
                case LabelOp.Marker: return "marker";
                case LabelOp.Chapter: return "chapter";
                case LabelOp.Meta: return "meta";
                default: return "unknown";
            }
        }

        static string Iso(DateTime utc)
        {
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
    }
}
