using System;
using System.Collections.Generic;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// A label placed on the media timeline.
    /// </summary>
    public struct PlacedLabel
    {
        /// <summary>The label as it was captured on the game thread.</summary>
        public LabelRecord Record;

        /// <summary>Seconds from the first frame of the recorded file.</summary>
        public double MediaTime;

        /// <summary>The zero-based frame number in the recorded file.</summary>
        public int MediaFrame;

        /// <summary>Seconds of game time since the recording started. Differs from <see cref="MediaTime"/> only when playback is variable.</summary>
        public double GameTime;
    }

    /// <summary>
    /// One sample of a named game-data channel.
    /// </summary>
    public struct SignalSample
    {
        /// <summary>Seconds from the first frame of the recorded file.</summary>
        public double MediaTime;

        /// <summary>The zero-based frame number in the recorded file.</summary>
        public int MediaFrame;

        /// <summary>The value at this point.</summary>
        public LabelValue Value;
    }

    /// <summary>
    /// A named game-data channel and every value it took during the recording.
    /// </summary>
    public sealed class SignalChannel
    {
        /// <summary>The channel name, as passed to <see cref="GameLabel.Set"/>.</summary>
        public string Name;

        /// <summary>The storage class of the channel's values, taken from its first sample.</summary>
        public LabelValueKind Kind;

        /// <summary>Every recorded value, in time order.</summary>
        public readonly List<SignalSample> Samples = new List<SignalSample>();

        /// <summary>The smallest numeric value seen, for numeric channels.</summary>
        public double Min = double.MaxValue;

        /// <summary>The largest numeric value seen, for numeric channels.</summary>
        public double Max = double.MinValue;
    }

    /// <summary>
    /// Something that lasted: the pairing of a <see cref="GameLabel.Begin(string)"/> with its
    /// <see cref="GameLabel.End(string)"/>.
    /// </summary>
    public sealed class SpanEntry
    {
        /// <summary>The span name.</summary>
        public string Name;

        /// <summary>Where the span opened.</summary>
        public double StartTime;

        /// <summary>The frame on which the span opened.</summary>
        public int StartFrame;

        /// <summary>Where the span closed, or the end of the recording when it never did.</summary>
        public double EndTime;

        /// <summary>The frame on which the span closed.</summary>
        public int EndFrame;

        /// <summary>True when the recording ended before the span was closed.</summary>
        public bool Open;

        /// <summary>Payload fields from the opening and closing calls, merged with the closing call winning.</summary>
        public readonly List<KeyValuePair<string, LabelValue>> Fields =
            new List<KeyValuePair<string, LabelValue>>();

        /// <summary>The span length in seconds.</summary>
        public double Duration => Math.Max(0d, EndTime - StartTime);
    }

    /// <summary>
    /// A named stretch of the recording. Chapters never overlap and cover the whole take.
    /// </summary>
    public sealed class ChapterEntry
    {
        /// <summary>The chapter name.</summary>
        public string Name;

        /// <summary>Where the chapter begins.</summary>
        public double StartTime;

        /// <summary>The frame on which the chapter begins.</summary>
        public int StartFrame;

        /// <summary>Where the chapter ends, which is where the next one begins or the end of the recording.</summary>
        public double EndTime;

        /// <summary>The frame on which the chapter ends.</summary>
        public int EndFrame;
    }

    /// <summary>
    /// Everything captured for one recording: events, spans, signals, markers, chapters and session facts, all
    /// already placed on the media timeline.
    /// </summary>
    public sealed class LabelTrack
    {
        /// <summary>Point-in-time events, in time order.</summary>
        public readonly List<PlacedLabel> Events = new List<PlacedLabel>();

        /// <summary>Scored moments, in time order.</summary>
        public readonly List<PlacedLabel> Markers = new List<PlacedLabel>();

        /// <summary>Closed and unclosed spans, in the order they opened.</summary>
        public readonly List<SpanEntry> Spans = new List<SpanEntry>();

        /// <summary>Chapters, in time order.</summary>
        public readonly List<ChapterEntry> Chapters = new List<ChapterEntry>();

        /// <summary>Game-data channels, keyed by name.</summary>
        public readonly Dictionary<string, SignalChannel> Signals = new Dictionary<string, SignalChannel>();

        /// <summary>Facts about the take as a whole, from <see cref="GameLabel.Meta"/>.</summary>
        public readonly List<KeyValuePair<string, LabelValue>> Meta =
            new List<KeyValuePair<string, LabelValue>>();

        /// <summary>The number of labels accepted into this track.</summary>
        public int AcceptedCount { get; private set; }

        /// <summary>The number of labels discarded because <see cref="LabeledRecorderSettings.MaxLabelsPerRecording"/> was reached.</summary>
        public int TruncatedCount { get; private set; }

        /// <summary>True when at least one label was discarded for being over the cap.</summary>
        public bool Truncated => TruncatedCount > 0;

        /// <summary>True once this track holds as many labels as its cap allows.</summary>
        public bool AtCap => AcceptedCount >= m_Cap;

        /// <summary>True when the recording was stopped early because this track reached its cap.</summary>
        public bool StoppedAtCap { get; set; }

        readonly Dictionary<string, List<SpanEntry>> m_OpenSpans = new Dictionary<string, List<SpanEntry>>();
        int m_Cap = int.MaxValue;

        /// <summary>
        /// Sets the maximum number of labels this track will keep.
        /// </summary>
        /// <param name="cap">The label ceiling.</param>
        public void SetCap(int cap)
        {
            m_Cap = Math.Max(1, cap);
        }

        /// <summary>
        /// Files one placed label into the right collection.
        /// </summary>
        /// <param name="placed">The label, already resolved to a media timecode.</param>
        public void Add(in PlacedLabel placed)
        {
            if (AcceptedCount >= m_Cap)
            {
                TruncatedCount++;
                return;
            }

            AcceptedCount++;

            switch (placed.Record.Op)
            {
                case LabelOp.Event:
                    Events.Add(placed);
                    break;

                case LabelOp.Marker:
                    Markers.Add(placed);
                    break;

                case LabelOp.SpanBegin:
                    OpenSpan(placed);
                    break;

                case LabelOp.SpanEnd:
                    CloseSpan(placed);
                    break;

                case LabelOp.Signal:
                    AddSample(placed);
                    break;

                case LabelOp.Chapter:
                    OpenChapter(placed);
                    break;

                case LabelOp.Meta:
                    SetMeta(placed.Record.Name, placed.Record.V0);
                    break;
            }
        }

        /// <summary>
        /// Closes anything still open at the end of the recording.
        /// </summary>
        /// <param name="endTime">The media time of the last recorded frame.</param>
        /// <param name="endFrame">The last recorded frame number.</param>
        public void Finish(double endTime, int endFrame)
        {
            foreach (var pair in m_OpenSpans)
            {
                var open = pair.Value;
                for (var i = 0; i < open.Count; i++)
                {
                    open[i].EndTime = endTime;
                    open[i].EndFrame = endFrame;
                    open[i].Open = true;
                }
            }

            m_OpenSpans.Clear();

            if (Chapters.Count > 0)
            {
                var last = Chapters[Chapters.Count - 1];
                if (last.EndFrame < last.StartFrame)
                {
                    last.EndTime = endTime;
                    last.EndFrame = endFrame;
                }
            }
        }

        /// <summary>
        /// Records a session fact, replacing any earlier value for the same key.
        /// </summary>
        /// <param name="key">The fact name.</param>
        /// <param name="value">The fact value.</param>
        public void SetMeta(string key, in LabelValue value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            for (var i = 0; i < Meta.Count; i++)
            {
                if (Meta[i].Key == key)
                {
                    Meta[i] = new KeyValuePair<string, LabelValue>(key, value);
                    return;
                }
            }

            Meta.Add(new KeyValuePair<string, LabelValue>(key, value));
        }

        /// <summary>
        /// Appends a signal sample directly, used for the baseline written at the start of a recording.
        /// </summary>
        /// <param name="name">The channel name.</param>
        /// <param name="value">The value.</param>
        /// <param name="mediaTime">The media time of the sample.</param>
        /// <param name="mediaFrame">The media frame of the sample.</param>
        public void AddSignal(string name, in LabelValue value, double mediaTime, int mediaFrame)
        {
            var channel = GetChannel(name, value.Kind);
            channel.Samples.Add(new SignalSample { MediaTime = mediaTime, MediaFrame = mediaFrame, Value = value });
            Track(channel, value);
        }

        // -----------------------------------------------------------------------------------------------------

        void OpenSpan(in PlacedLabel placed)
        {
            var entry = new SpanEntry
            {
                Name = placed.Record.Name,
                StartTime = placed.MediaTime,
                StartFrame = placed.MediaFrame,
                EndTime = placed.MediaTime,
                EndFrame = -1
            };

            CopyFields(placed.Record, entry.Fields);
            Spans.Add(entry);

            if (!m_OpenSpans.TryGetValue(entry.Name, out var stack))
            {
                stack = new List<SpanEntry>(1);
                m_OpenSpans.Add(entry.Name, stack);
            }

            stack.Add(entry);
        }

        void CloseSpan(in PlacedLabel placed)
        {
            var name = placed.Record.Name;

            if (!m_OpenSpans.TryGetValue(name, out var stack) || stack.Count == 0)
            {
                // An End with no matching Begin. Record it as a zero-length span so the information is not lost.
                var orphan = new SpanEntry
                {
                    Name = name,
                    StartTime = placed.MediaTime,
                    StartFrame = placed.MediaFrame,
                    EndTime = placed.MediaTime,
                    EndFrame = placed.MediaFrame
                };
                CopyFields(placed.Record, orphan.Fields);
                Spans.Add(orphan);
                return;
            }

            var entry = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
                m_OpenSpans.Remove(name);

            entry.EndTime = placed.MediaTime;
            entry.EndFrame = placed.MediaFrame;
            MergeFields(placed.Record, entry.Fields);
        }

        void OpenChapter(in PlacedLabel placed)
        {
            if (Chapters.Count > 0)
            {
                var previous = Chapters[Chapters.Count - 1];
                previous.EndTime = placed.MediaTime;
                previous.EndFrame = placed.MediaFrame;
            }

            Chapters.Add(new ChapterEntry
            {
                Name = placed.Record.Name,
                StartTime = placed.MediaTime,
                StartFrame = placed.MediaFrame,
                EndTime = placed.MediaTime,
                EndFrame = -1
            });
        }

        void AddSample(in PlacedLabel placed)
        {
            var channel = GetChannel(placed.Record.Name, placed.Record.V0.Kind);
            channel.Samples.Add(new SignalSample
            {
                MediaTime = placed.MediaTime,
                MediaFrame = placed.MediaFrame,
                Value = placed.Record.V0
            });
            Track(channel, placed.Record.V0);
        }

        SignalChannel GetChannel(string name, LabelValueKind kind)
        {
            if (Signals.TryGetValue(name, out var channel))
                return channel;

            channel = new SignalChannel { Name = name, Kind = kind };
            Signals.Add(name, channel);
            return channel;
        }

        static void Track(SignalChannel channel, in LabelValue value)
        {
            if (value.Kind != LabelValueKind.Number)
                return;

            if (value.Number < channel.Min)
                channel.Min = value.Number;
            if (value.Number > channel.Max)
                channel.Max = value.Number;
        }

        static void CopyFields(in LabelRecord record, List<KeyValuePair<string, LabelValue>> into)
        {
            for (var i = 0; i < record.FieldCount; i++)
            {
                record.GetField(i, out var key, out var value);
                if (!string.IsNullOrEmpty(key))
                    into.Add(new KeyValuePair<string, LabelValue>(key, value));
            }
        }

        static void MergeFields(in LabelRecord record, List<KeyValuePair<string, LabelValue>> into)
        {
            for (var i = 0; i < record.FieldCount; i++)
            {
                record.GetField(i, out var key, out var value);
                if (string.IsNullOrEmpty(key))
                    continue;

                var replaced = false;
                for (var j = 0; j < into.Count; j++)
                {
                    if (into[j].Key != key)
                        continue;

                    into[j] = new KeyValuePair<string, LabelValue>(key, value);
                    replaced = true;
                    break;
                }

                if (!replaced)
                    into.Add(new KeyValuePair<string, LabelValue>(key, value));
            }
        }
    }
}
