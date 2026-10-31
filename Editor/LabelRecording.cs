using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Recorder.Labeling;
using UnityEngine;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Facts about the capture that are fixed for its whole duration.
    /// </summary>
    public sealed class RecordingContext
    {
        /// <summary>The identifier the bridge assigned to the Recorder session.</summary>
        public string SessionId;

        /// <summary>The Recorder settings asset name, as shown in the Recorder window.</summary>
        public string RecorderName;

        /// <summary>The Recorder settings type name.</summary>
        public string RecorderType;

        /// <summary>The Recorder's record mode.</summary>
        public string RecordMode;

        /// <summary>The take number.</summary>
        public int Take;

        /// <summary>When the recording started, in UTC.</summary>
        public DateTime StartedAtUtc;

        /// <summary>The absolute path of the recorded media.</summary>
        public string MediaPath;

        /// <summary>The media file extension, without a dot.</summary>
        public string Extension;

        /// <summary>True when the media is a numbered image sequence.</summary>
        public bool IsImageSequence;

        /// <summary>The output frame rate.</summary>
        public float FrameRate;

        /// <summary>Either <c>Constant</c> or <c>Variable</c>.</summary>
        public string FrameRatePlayback;

        /// <summary>Only every Nth source frame is captured.</summary>
        public int CaptureEveryNthFrame;

        /// <summary>Output width in pixels, or 0 when not an image recorder.</summary>
        public int Width;

        /// <summary>Output height in pixels, or 0 when not an image recorder.</summary>
        public int Height;

        /// <summary>Whether the media carries audio, when that is known.</summary>
        public bool? HasAudio;

        /// <summary>The active scene when the recording started.</summary>
        public string Scene;

        /// <summary>The Unity version that produced the recording.</summary>
        public string UnityVersion;

        /// <summary>The project's product name.</summary>
        public string ProductName;

        /// <summary>The Editor platform.</summary>
        public string Platform;

        /// <summary>True when the output frame rate is not locked.</summary>
        public bool IsVariableRate => FrameRatePlayback == "Variable";
    }

    /// <summary>
    /// One live label capture, bound to one Recorder session for as long as that session is recording.
    /// </summary>
    /// <remarks>
    /// Owns the mapping from engine frames to media frames, the in-memory <see cref="LabelTrack"/>, and the
    /// append-as-you-go stream file.
    /// </remarks>
    public sealed class LabelRecording
    {
        struct FrameMark
        {
            public int EngineFrame;
            public int MediaFrame;
            public double GameTime;
        }

        const int k_MarkHistory = 256;

        readonly FrameMark[] m_Marks = new FrameMark[k_MarkHistory];
        int m_MarkCount;
        int m_MarkNext;

        readonly LabeledRecorderSettings m_Settings;
        StreamWriter m_Stream;

        double m_StartGameTime;
        bool m_HasStartGameTime;

        /// <summary>The fixed facts about this capture.</summary>
        public RecordingContext Context { get; }

        /// <summary>The labels gathered so far.</summary>
        public LabelTrack Track { get; } = new LabelTrack();

        /// <summary>The path the finished label file will be written to.</summary>
        public string FinalPath { get; }

        /// <summary>The path of the append-as-you-go file, or null when streaming is off.</summary>
        public string StreamPath { get; }

        /// <summary>The highest media frame observed.</summary>
        public int LastMediaFrame { get; private set; }

        /// <summary>The media time of <see cref="LastMediaFrame"/>.</summary>
        public double LastMediaTime => ToMediaTime(LastMediaFrame);

        /// <summary>
        /// Opens a capture for a Recorder session that has just started recording.
        /// </summary>
        /// <param name="view">The session as the bridge sees it.</param>
        /// <param name="settings">Project settings controlling output and streaming.</param>
        public LabelRecording(RecorderSessionView view, LabeledRecorderSettings settings)
        {
            m_Settings = settings;

            Context = new RecordingContext
            {
                SessionId = view.SessionId,
                RecorderName = view.RecorderName,
                RecorderType = view.RecorderType,
                RecordMode = view.RecordMode,
                Take = view.Take,
                StartedAtUtc = view.StartedAt.ToUniversalTime(),
                MediaPath = view.OutputPath,
                Extension = view.Extension,
                IsImageSequence = view.IsImageSequence,
                FrameRate = view.FrameRate,
                FrameRatePlayback = view.FrameRatePlayback,
                CaptureEveryNthFrame = Math.Max(1, view.CaptureEveryNthFrame),
                Width = view.OutputWidth,
                Height = view.OutputHeight,
                HasAudio = view.HasAudio,
                Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                UnityVersion = Application.unityVersion,
                ProductName = Application.productName,
                Platform = Application.platform.ToString()
            };

            if (LabelPaths.TryResolve(view, settings, out var finalPath, out var streamPath))
            {
                FinalPath = finalPath;
                StreamPath = settings.WriteStreamFile ? streamPath : null;
            }

            OpenStream();
        }

        /// <summary>
        /// Records the engine-to-media frame correspondence for the frame about to be simulated, and seeds the
        /// baseline signal samples on the first frame.
        /// </summary>
        /// <param name="engineFrame">The value of <c>Time.frameCount</c> for this frame.</param>
        /// <param name="recordedFrameCount">How many frames the Recorder has committed so far.</param>
        /// <param name="gameTime">The current game time in seconds.</param>
        public void BeginFrame(int engineFrame, int recordedFrameCount, double gameTime)
        {
            if (!m_HasStartGameTime)
            {
                m_HasStartGameTime = true;
                m_StartGameTime = gameTime;
                WriteBaseline();
            }

            var mark = new FrameMark
            {
                EngineFrame = engineFrame,
                MediaFrame = Math.Max(0, recordedFrameCount),
                GameTime = gameTime - m_StartGameTime
            };

            m_Marks[m_MarkNext] = mark;
            m_MarkNext = (m_MarkNext + 1) % k_MarkHistory;
            if (m_MarkCount < k_MarkHistory)
                m_MarkCount++;

            if (mark.MediaFrame > LastMediaFrame)
                LastMediaFrame = mark.MediaFrame;
        }

        /// <summary>
        /// Places one captured label on the media timeline and files it.
        /// </summary>
        /// <param name="record">A label drained from the sink.</param>
        public void Accept(in LabelRecord record)
        {
            Resolve(record.Frame, out var mediaFrame, out var gameTime);

            var placed = new PlacedLabel
            {
                Record = record,
                MediaFrame = mediaFrame,
                MediaTime = ToMediaTime(mediaFrame),
                GameTime = gameTime
            };

            Track.Add(placed);
            StreamLine(placed);
        }

        /// <summary>
        /// Closes open spans and chapters, writes the label file, and removes the stream file unless it was
        /// configured to be kept.
        /// </summary>
        /// <param name="lastView">The final state of the Recorder session, used for the media frame count.</param>
        /// <returns>The path written, or null when nothing could be written.</returns>
        public string Complete(RecorderSessionView lastView)
        {
            var frameCount = Math.Max(LastMediaFrame, lastView?.RecordedFrameCount ?? 0);
            var endFrame = Math.Max(0, frameCount - 1);
            Track.Finish(ToMediaTime(endFrame), endFrame);

            CloseStream();

            if (string.IsNullOrEmpty(FinalPath))
            {
                Debug.LogWarning("Labeled Recorder captured labels but could not work out where to write them, " +
                    "because the recorder did not report an output path.");
                return null;
            }

            try
            {
                var json = LabelTrackSerializer.ToJson(this, frameCount, m_Settings);
                var directory = Path.GetDirectoryName(FinalPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(FinalPath, json);

                if (!string.IsNullOrEmpty(StreamPath) && !m_Settings.KeepStreamFile && File.Exists(StreamPath))
                    File.Delete(StreamPath);

                return FinalPath;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Labeled Recorder failed to write '{FinalPath}': {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts a media frame number to its time in the recorded file.
        /// </summary>
        /// <param name="mediaFrame">The zero-based frame number.</param>
        /// <returns>Seconds from the first frame.</returns>
        public double ToMediaTime(int mediaFrame)
        {
            return Context.FrameRate > 0f ? mediaFrame / Context.FrameRate : 0d;
        }

        // -----------------------------------------------------------------------------------------------------

        void Resolve(int engineFrame, out int mediaFrame, out double gameTime)
        {
            if (m_MarkCount == 0)
            {
                mediaFrame = 0;
                gameTime = 0d;
                return;
            }

            // Walk back from the newest mark to the first one at or before this engine frame. Labels pushed from a
            // job thread can arrive a frame or two late, which is exactly what this history is for.
            for (var i = 1; i <= m_MarkCount; i++)
            {
                var index = (m_MarkNext - i + k_MarkHistory) % k_MarkHistory;
                if (m_Marks[index].EngineFrame > engineFrame)
                    continue;

                mediaFrame = m_Marks[index].MediaFrame;
                gameTime = m_Marks[index].GameTime;
                return;
            }

            var oldest = (m_MarkNext - m_MarkCount + k_MarkHistory) % k_MarkHistory;
            mediaFrame = m_Marks[oldest].MediaFrame;
            gameTime = m_Marks[oldest].GameTime;
        }

        void WriteBaseline()
        {
            var snapshot = new List<KeyValuePair<string, LabelValue>>();
            LabelSink.SnapshotSignals(snapshot);

            for (var i = 0; i < snapshot.Count; i++)
                Track.AddSignal(snapshot[i].Key, snapshot[i].Value, 0d, 0);
        }

        void OpenStream()
        {
            if (string.IsNullOrEmpty(StreamPath))
                return;

            try
            {
                var directory = Path.GetDirectoryName(StreamPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                m_Stream = new StreamWriter(StreamPath, false) { AutoFlush = false };
                m_Stream.WriteLine(LabelTrackSerializer.StreamHeader(this));
                m_Stream.Flush();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Labeled Recorder could not open '{StreamPath}' for streaming: " +
                    $"{exception.Message}. Labels will still be written when the recording stops.");
                m_Stream = null;
            }
        }

        void StreamLine(in PlacedLabel placed)
        {
            if (m_Stream == null)
                return;

            try
            {
                m_Stream.WriteLine(LabelTrackSerializer.StreamLine(placed, Context.IsVariableRate));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Labeled Recorder stopped streaming labels: {exception.Message}");
                CloseStream();
            }
        }

        /// <summary>
        /// Flushes the stream file so a crash loses at most the last frame of labels.
        /// </summary>
        public void Flush()
        {
            try
            {
                m_Stream?.Flush();
            }
            catch (Exception)
            {
                // A failed flush is not worth interrupting the recording for; Complete will report real problems.
            }
        }

        void CloseStream()
        {
            if (m_Stream == null)
                return;

            try
            {
                m_Stream.Flush();
                m_Stream.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do here.
            }

            m_Stream = null;
        }
    }
}
