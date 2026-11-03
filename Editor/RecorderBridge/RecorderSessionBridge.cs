using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEditor.Recorder.SessionBridge
{
    /// <summary>
    /// A read-only view of one live Recorder session, flattened into public data.
    /// </summary>
    /// <remarks>
    /// Instances are pooled and refreshed in place by <see cref="RecorderSessionBridge.Collect"/>; do not hold on
    /// to one past the call that produced it.
    /// </remarks>
    public sealed class RecorderSessionView
    {
        /// <summary>A stable identifier for this session, assigned the first time the session is seen.</summary>
        public string SessionId { get; internal set; }

        /// <summary>The name of the Recorder settings asset, as it appears in the Recorder window.</summary>
        public string RecorderName { get; internal set; }

        /// <summary>The settings type name, for example <c>MovieRecorderSettings</c>.</summary>
        public string RecorderType { get; internal set; }

        /// <summary>True while the session is actively writing frames.</summary>
        public bool IsRecording { get; internal set; }

        /// <summary>The number of frames committed to the output so far.</summary>
        public int RecordedFrameCount { get; internal set; }

        /// <summary>The session frame counter, which includes frames skipped before the start frame.</summary>
        public int FrameIndex { get; internal set; }

        /// <summary>Seconds elapsed on the Recorder's own clock since the recording started.</summary>
        public double RecorderTime { get; internal set; }

        /// <summary>Wall-clock time at which the session was created.</summary>
        public DateTime StartedAt { get; internal set; }

        /// <summary>The target frame rate in frames per second.</summary>
        public float FrameRate { get; internal set; }

        /// <summary>Either <c>Constant</c> or <c>Variable</c>.</summary>
        public string FrameRatePlayback { get; internal set; }

        /// <summary>The record mode: <c>Manual</c>, <c>SingleFrame</c>, <c>FrameInterval</c> or <c>TimeInterval</c>.</summary>
        public string RecordMode { get; internal set; }

        /// <summary>The take number used for the output file name.</summary>
        public int Take { get; internal set; }

        /// <summary>Only every Nth source frame is captured when playback is variable.</summary>
        public int CaptureEveryNthFrame { get; internal set; }

        /// <summary>The output file extension, without a leading dot.</summary>
        public string Extension { get; internal set; }

        /// <summary>The absolute path of the output file, resolved once when the session was first seen.</summary>
        public string OutputPath { get; internal set; }

        /// <summary>Output width in pixels, or 0 when the recorder does not produce images.</summary>
        public int OutputWidth { get; internal set; }

        /// <summary>Output height in pixels, or 0 when the recorder does not produce images.</summary>
        public int OutputHeight { get; internal set; }

        /// <summary>True when the output is a numbered image sequence rather than a single file.</summary>
        public bool IsImageSequence { get; internal set; }

        /// <summary>True when the output carries an audio track, false when it does not, null when unknown.</summary>
        public bool? HasAudio { get; internal set; }

        /// <summary>The first frame of the configured interval, for frame-based record modes.</summary>
        public int StartFrame { get; internal set; }

        /// <summary>The first second of the configured interval, for time-based record modes.</summary>
        public float StartTime { get; internal set; }
    }

    /// <summary>
    /// Exposes the Recorder's live session state as public, read-only data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Recorder keeps <c>RecordingSession</c>, <c>RecorderComponent</c> and most of
    /// <c>RecorderSettings</c> internal to this assembly. This file lives inside the same assembly purely so that
    /// tooling in other assemblies can observe a recording without reflection.
    /// </para>
    /// <para>
    /// It is additive: it changes no existing Recorder file, so the package can be updated from upstream without
    /// a merge conflict. Nothing here mutates recorder state.
    /// </para>
    /// </remarks>
    public static class RecorderSessionBridge
    {
        const string k_SessionRootName = "Unity-RecorderSessions";

        static GameObject s_SessionRoot;
        static readonly List<RecorderComponent> s_Components = new List<RecorderComponent>();
        static readonly List<RecorderSessionView> s_Pool = new List<RecorderSessionView>();

        // Reference-keyed so two sessions with identical settings stay distinct, and so entries disappear once the
        // Recorder drops its session.
        static readonly Dictionary<RecordingSession, SessionIdentity> s_Known =
            new Dictionary<RecordingSession, SessionIdentity>();

        static readonly Dictionary<string, RecorderSessionView> s_Active =
            new Dictionary<string, RecorderSessionView>();
        static readonly List<string> s_Scratch = new List<string>();
        static readonly List<RecordingSession> s_Expired = new List<RecordingSession>();

        const int k_DeepScanInterval = 15;
        static int s_LastDeepScanFrame = -k_DeepScanInterval;

        sealed class SessionIdentity
        {
            public string Id;
            public string OutputPath;
            public int LastSeenFrame;
        }

        /// <summary>
        /// Raised the first time a session is observed in a recording state, before <see cref="Collect"/> returns.
        /// The argument is the session's identifier, matching <see cref="RecorderSessionView.SessionId"/>.
        /// </summary>
        public static event Action<RecorderSessionView> SessionStartedRecording;

        /// <summary>
        /// Raised once a previously recording session is gone. The argument is the last view observed for it.
        /// </summary>
        public static event Action<RecorderSessionView> SessionStoppedRecording;

        /// <summary>
        /// Fills <paramref name="results"/> with a view of every Recorder session currently alive in the running
        /// scene, whether it is recording yet or not, and raises the start and stop events.
        /// </summary>
        /// <param name="results">The list to fill. It is cleared first.</param>
        /// <remarks>
        /// Call this once per frame from play mode. It allocates nothing in the steady state.
        /// </remarks>
        public static void Collect(List<RecorderSessionView> results)
        {
            results.Clear();

            if (!Application.isPlaying)
            {
                Reset();
                return;
            }

            if (s_SessionRoot == null)
            {
                // Only runs until the Recorder creates its session root, which then survives scene loads.
                s_SessionRoot = FindSessionRoot();
                if (s_SessionRoot == null)
                {
                    StopAllRecordings();
                    return;
                }
            }

            s_Components.Clear();
            s_SessionRoot.GetComponentsInChildren(true, s_Components);

            var frame = Time.frameCount;
            var used = 0;

            for (var i = 0; i < s_Components.Count; i++)
            {
                var component = s_Components[i];
                if (component == null)
                    continue;

                var session = component.session;
                if (session == null || session.recorder == null || session.settings == null)
                    continue;

                if (!s_Known.TryGetValue(session, out var identity))
                {
                    identity = new SessionIdentity
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        OutputPath = ResolveOutputPath(session)
                    };
                    s_Known.Add(session, identity);
                }

                identity.LastSeenFrame = frame;

                var view = Rent(used++);
                Fill(view, session, identity);
                results.Add(view);
            }

            DetectTransitions(results, frame);
        }

        /// <summary>
        /// Forgets every tracked session, raising <see cref="SessionStoppedRecording"/> for any that were still
        /// recording. Call this when leaving play mode so that in-flight label files are finalized.
        /// </summary>
        public static void Reset()
        {
            StopAllRecordings();
            s_Known.Clear();
            s_SessionRoot = null;
            s_LastDeepScanFrame = -k_DeepScanInterval;
            s_Components.Clear();
        }

        // -------------------------------------------------------------------------------------------------------

        static void DetectTransitions(List<RecorderSessionView> results, int frame)
        {
            for (var i = 0; i < results.Count; i++)
            {
                var view = results[i];
                if (!view.IsRecording)
                    continue;

                if (s_Active.TryGetValue(view.SessionId, out var tracked))
                {
                    CopyInto(tracked, view);
                }
                else
                {
                    s_Active.Add(view.SessionId, Clone(view));
                    SessionStartedRecording?.Invoke(view);
                }
            }

            // Anything we were tracking that is no longer present and recording has finished.
            s_Scratch.Clear();
            foreach (var pair in s_Active)
            {
                if (!IsRecordingIn(results, pair.Key))
                    s_Scratch.Add(pair.Key);
            }

            for (var i = 0; i < s_Scratch.Count; i++)
                RaiseStop(s_Scratch[i]);

            // Drop identities for sessions the Recorder has destroyed, so their ids are never reused.
            s_Expired.Clear();
            foreach (var pair in s_Known)
            {
                if (pair.Value.LastSeenFrame != frame)
                    s_Expired.Add(pair.Key);
            }

            for (var i = 0; i < s_Expired.Count; i++)
                s_Known.Remove(s_Expired[i]);
        }

        static bool IsRecordingIn(List<RecorderSessionView> results, string sessionId)
        {
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i].SessionId == sessionId)
                    return results[i].IsRecording;
            }

            return false;
        }

        static void StopAllRecordings()
        {
            if (s_Active.Count == 0)
                return;

            s_Scratch.Clear();
            foreach (var pair in s_Active)
                s_Scratch.Add(pair.Key);

            for (var i = 0; i < s_Scratch.Count; i++)
                RaiseStop(s_Scratch[i]);
        }

        static void RaiseStop(string sessionId)
        {
            if (!s_Active.TryGetValue(sessionId, out var last))
                return;

            s_Active.Remove(sessionId);
            SessionStoppedRecording?.Invoke(last);
        }

        /// <summary>
        /// Locates the GameObject the Recorder parents its sessions under.
        /// </summary>
        /// <remarks>
        /// The fast path is a name lookup, which is all that is needed in practice. The fallback exists so
        /// detection cannot silently fail if the Recorder ever renames that object or leaves it inactive: it
        /// asks the engine for the session components themselves, which finds them whatever their state. That
        /// call is comparatively expensive, so it is throttled and stops entirely once a root is found.
        /// </remarks>
        static GameObject FindSessionRoot()
        {
            var byName = GameObject.Find(k_SessionRootName);
            if (byName != null)
                return byName;

            var frame = Time.frameCount;
            if (frame - s_LastDeepScanFrame < k_DeepScanInterval)
                return null;

            s_LastDeepScanFrame = frame;

            var components = Resources.FindObjectsOfTypeAll<RecorderComponent>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null || component.gameObject.scene.IsValid() == false)
                    continue;

                return component.transform.root.gameObject;
            }

            return null;
        }

        static RecorderSessionView Rent(int index)
        {
            while (s_Pool.Count <= index)
                s_Pool.Add(new RecorderSessionView());
            return s_Pool[index];
        }

        static RecorderSessionView Clone(RecorderSessionView source)
        {
            var copy = new RecorderSessionView();
            CopyInto(copy, source);
            return copy;
        }

        static void CopyInto(RecorderSessionView target, RecorderSessionView source)
        {
            target.SessionId = source.SessionId;
            target.RecorderName = source.RecorderName;
            target.RecorderType = source.RecorderType;
            target.IsRecording = source.IsRecording;
            target.RecordedFrameCount = source.RecordedFrameCount;
            target.FrameIndex = source.FrameIndex;
            target.RecorderTime = source.RecorderTime;
            target.StartedAt = source.StartedAt;
            target.FrameRate = source.FrameRate;
            target.FrameRatePlayback = source.FrameRatePlayback;
            target.RecordMode = source.RecordMode;
            target.Take = source.Take;
            target.CaptureEveryNthFrame = source.CaptureEveryNthFrame;
            target.Extension = source.Extension;
            target.OutputPath = source.OutputPath;
            target.OutputWidth = source.OutputWidth;
            target.OutputHeight = source.OutputHeight;
            target.IsImageSequence = source.IsImageSequence;
            target.HasAudio = source.HasAudio;
            target.StartFrame = source.StartFrame;
            target.StartTime = source.StartTime;
        }

        static void Fill(RecorderSessionView view, RecordingSession session, SessionIdentity identity)
        {
            var settings = session.settings;

            view.SessionId = identity.Id;
            view.OutputPath = identity.OutputPath;
            view.RecorderName = settings.name;
            view.RecorderType = settings.GetType().Name;
            view.IsRecording = session.isRecording;
            view.RecordedFrameCount = session.recorder.RecordedFramesCount;
            view.FrameIndex = session.frameIndex;
            view.RecorderTime = session.recorderTime;
            view.StartedAt = session.sessionStartTS;
            view.FrameRate = settings.FrameRate;
            view.FrameRatePlayback = settings.FrameRatePlayback.ToString();
            view.RecordMode = settings.RecordMode.ToString();
            view.Take = settings.Take;
            view.CaptureEveryNthFrame = Math.Max(1, settings.captureEveryNthFrame);
            view.Extension = settings.Extension;
            view.StartFrame = settings.StartFrame;
            view.StartTime = settings.StartTime;

            if (settings is RecorderSettings.IResolutionUser resolution)
            {
                view.OutputWidth = resolution.OutputWidth;
                view.OutputHeight = resolution.OutputHeight;
            }
            else
            {
                view.OutputWidth = 0;
                view.OutputHeight = 0;
            }

            view.IsImageSequence = settings is ImageRecorderSettings || settings is AOVRecorderSettings;

            if (settings is MovieRecorderSettings movie)
                view.HasAudio = movie.CaptureAudio;
            else if (settings is AudioRecorderSettings)
                view.HasAudio = true;
            else
                view.HasAudio = null;
        }

        static string ResolveOutputPath(RecordingSession session)
        {
            try
            {
                return session.settings.FileNameGenerator.BuildAbsolutePath(session);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Labeled Recorder could not resolve the output path of recorder " +
                    $"'{session.settings.name}': {exception.Message}");
                return null;
            }
        }
    }
}
