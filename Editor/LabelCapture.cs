using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Recorder.Labeling;
using UnityEngine;
using UnityEngine.LowLevel;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Drives label capture: it publishes the frame clock, watches the Recorder for sessions starting and
    /// stopping, drains the label sink, and writes one sidecar file per recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything runs from two player-loop callbacks injected only while the Editor is in play mode and
    /// <see cref="LabeledRecorderSettings.Enabled"/> is true. Using the player loop rather than a MonoBehaviour is
    /// what guarantees the clock is published before any game script runs, so a label pushed from a job thread is
    /// stamped with the right frame.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class LabelCapture
    {
        struct LabelEarlyTick { }
        struct LabelLateTick { }

        static readonly List<RecorderSessionView> s_Views = new List<RecorderSessionView>();
        static readonly List<LabelRecord> s_Drained = new List<LabelRecord>();
        static readonly List<LabelRecording> s_Recordings = new List<LabelRecording>();
        static readonly Dictionary<string, LabelRecording> s_BySession = new Dictionary<string, LabelRecording>();

        static bool s_Installed;
        static double s_LastFlush;

        /// <summary>
        /// Raised after a label file has been written, with its absolute path. Useful for piping recordings into
        /// your own tooling.
        /// </summary>
        public static event Action<string> LabelFileWritten;

        /// <summary>True while at least one recording is collecting labels.</summary>
        public static bool IsCapturing => s_Recordings.Count > 0;

        /// <summary>The number of recordings currently being labelled.</summary>
        public static int ActiveRecordingCount => s_Recordings.Count;

        static LabelCapture()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            if (EditorApplication.isPlayingOrWillChangePlaymode && Application.isPlaying)
                Refresh();
        }

        /// <summary>
        /// Re-reads <see cref="LabeledRecorderSettings"/> and installs or removes the hooks to match. Call this
        /// after changing the enabled flag while play mode is running.
        /// </summary>
        public static void Refresh()
        {
            var shouldRun = Application.isPlaying && LabeledRecorderSettings.instance.Enabled;

            if (shouldRun && !s_Installed)
                Install();
            else if (!shouldRun && s_Installed)
                Uninstall();
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (LabeledRecorderSettings.instance.Enabled)
                        Install();
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    Uninstall();
                    break;
            }
        }

        static void Install()
        {
            if (s_Installed)
                return;

            RecorderSessionBridge.SessionStartedRecording += OnRecordingStarted;
            RecorderSessionBridge.SessionStoppedRecording += OnRecordingStopped;

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            InsertFirst<UnityEngine.PlayerLoop.EarlyUpdate>(ref loop, typeof(LabelEarlyTick), EarlyTick);
            InsertLast<UnityEngine.PlayerLoop.PostLateUpdate>(ref loop, typeof(LabelLateTick), LateTick);
            PlayerLoop.SetPlayerLoop(loop);

            LabelSink.Time = Time.timeAsDouble;
            LabelSink.Frame = Time.frameCount;

            s_Installed = true;

            if (LabeledRecorderSettings.instance.VerboseLog)
                Debug.Log("Labeled Recorder: watching for recordings.");
        }

        static void Uninstall()
        {
            if (!s_Installed)
                return;

            // Anything still recording when play mode ends must still produce a file.
            DrainInto();
            RecorderSessionBridge.Reset();
            CompleteAll();

            RecorderSessionBridge.SessionStartedRecording -= OnRecordingStarted;
            RecorderSessionBridge.SessionStoppedRecording -= OnRecordingStopped;

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Remove(ref loop, typeof(LabelEarlyTick));
            Remove(ref loop, typeof(LabelLateTick));
            PlayerLoop.SetPlayerLoop(loop);

            LabelSink.Release();
            LabelSink.ForgetSignals();
            AutoCapture.Reset();

            s_Views.Clear();
            s_Drained.Clear();
            s_Installed = false;
        }

        // -----------------------------------------------------------------------------------------------------

        static void EarlyTick()
        {
            var settings = LabeledRecorderSettings.instance;

            if (!settings.Enabled)
            {
                Uninstall();
                return;
            }

            var frame = Time.frameCount;
            var time = Time.timeAsDouble;

            LabelSink.Time = time;
            LabelSink.Frame = frame;

            // Hand over anything pushed late in the previous frame before a stop can close its file.
            DrainInto();

            RecorderSessionBridge.Collect(s_Views);

            for (var i = 0; i < s_Recordings.Count; i++)
            {
                var recording = s_Recordings[i];
                var view = FindView(recording.Context.SessionId);
                var recordedFrames = view?.RecordedFrameCount ?? recording.LastMediaFrame;
                recording.BeginFrame(frame, recordedFrames, time);
            }

            AutoCapture.Tick(settings, time);

            DrainInto();

            if (time - s_LastFlush >= 1d)
            {
                s_LastFlush = time;
                for (var i = 0; i < s_Recordings.Count; i++)
                    s_Recordings[i].Flush();
            }
        }

        static void LateTick()
        {
            // Labels pushed from Update and LateUpdate reach their file in the frame they describe.
            DrainInto();
        }

        static void DrainInto()
        {
            s_Drained.Clear();
            if (LabelSink.Drain(s_Drained) == 0)
                return;

            if (s_Recordings.Count == 0)
                return;

            for (var i = 0; i < s_Drained.Count; i++)
            {
                // A label belongs to every recording running at that moment, since they all captured it.
                for (var r = 0; r < s_Recordings.Count; r++)
                    s_Recordings[r].Accept(s_Drained[i]);
            }
        }

        static void OnRecordingStarted(RecorderSessionView view)
        {
            if (s_BySession.ContainsKey(view.SessionId))
                return;

            var settings = LabeledRecorderSettings.instance;

            if (s_Recordings.Count == 0)
            {
                LabelSink.Begin(settings.BufferCapacity);
                s_LastFlush = Time.timeAsDouble;
            }

            var recording = new LabelRecording(view, settings);
            recording.Track.SetCap(settings.MaxLabelsPerRecording);

            s_Recordings.Add(recording);
            s_BySession.Add(view.SessionId, recording);

            if (settings.VerboseLog)
            {
                Debug.Log($"Labeled Recorder: labelling '{view.RecorderName}' into " +
                    $"'{LabelPaths.ToProjectRelative(recording.FinalPath)}'.");
            }
        }

        static void OnRecordingStopped(RecorderSessionView view)
        {
            if (!s_BySession.TryGetValue(view.SessionId, out var recording))
                return;

            s_BySession.Remove(view.SessionId);
            s_Recordings.Remove(recording);

            Finish(recording, view);

            if (s_Recordings.Count == 0)
                LabelSink.End();
        }

        static void CompleteAll()
        {
            if (s_Recordings.Count == 0)
                return;

            var pending = s_Recordings.ToArray();

            s_Recordings.Clear();
            s_BySession.Clear();

            for (var i = 0; i < pending.Length; i++)
                Finish(pending[i], null);
        }

        static void Finish(LabelRecording recording, RecorderSessionView lastView)
        {
            var path = recording.Complete(lastView);
            if (string.IsNullOrEmpty(path))
                return;

            if (LabeledRecorderSettings.instance.VerboseLog)
            {
                Debug.Log($"Labeled Recorder: wrote {recording.Track.AcceptedCount} labels to " +
                    $"'{LabelPaths.ToProjectRelative(path)}'.");
            }

            try
            {
                LabelFileWritten?.Invoke(path);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            LabelAssetRefresh.Queue(path);
        }

        static RecorderSessionView FindView(string sessionId)
        {
            for (var i = 0; i < s_Views.Count; i++)
            {
                if (s_Views[i].SessionId == sessionId)
                    return s_Views[i];
            }

            return null;
        }

        // -----------------------------------------------------------------------------------------------------

        static void InsertFirst<TParent>(ref PlayerLoopSystem loop, Type marker, PlayerLoopSystem.UpdateFunction tick)
        {
            Insert(ref loop, typeof(TParent), marker, tick, true);
        }

        static void InsertLast<TParent>(ref PlayerLoopSystem loop, Type marker, PlayerLoopSystem.UpdateFunction tick)
        {
            Insert(ref loop, typeof(TParent), marker, tick, false);
        }

        static bool Insert(ref PlayerLoopSystem system, Type parent, Type marker,
            PlayerLoopSystem.UpdateFunction tick, bool first)
        {
            if (system.type == parent)
            {
                var children = new List<PlayerLoopSystem>(system.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                children.RemoveAll(child => child.type == marker);

                var inserted = new PlayerLoopSystem { type = marker, updateDelegate = tick };
                if (first)
                    children.Insert(0, inserted);
                else
                    children.Add(inserted);

                system.subSystemList = children.ToArray();
                return true;
            }

            if (system.subSystemList == null)
                return false;

            for (var i = 0; i < system.subSystemList.Length; i++)
            {
                if (Insert(ref system.subSystemList[i], parent, marker, tick, first))
                    return true;
            }

            return false;
        }

        static void Remove(ref PlayerLoopSystem system, Type marker)
        {
            if (system.subSystemList == null)
                return;

            var children = new List<PlayerLoopSystem>(system.subSystemList);
            children.RemoveAll(child => child.type == marker);

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                Remove(ref child, marker);
                children[i] = child;
            }

            system.subSystemList = children.ToArray();
        }
    }
}
