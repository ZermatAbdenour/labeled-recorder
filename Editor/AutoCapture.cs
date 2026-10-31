using UnityEngine;
using UnityEngine.SceneManagement;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Labels that are captured without any code in the game: the active scene, time scale, frame pacing and
    /// camera motion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist so a recording is useful to an editing agent before you have instrumented anything. Frame
    /// pacing tells it which stretches of footage stuttered and should not be cut to; camera speed and turn rate
    /// are a decent proxy for on-screen motion, which is what decides whether a shot reads as calm or frantic.
    /// </para>
    /// <para>
    /// Everything here goes through <see cref="GameLabel"/>, so when no recording is running the whole tick is a
    /// handful of static reads.
    /// </para>
    /// </remarks>
    public static class AutoCapture
    {
        static string s_Scene;
        static double s_NextPerfSample;
        static double s_NextCameraSample;
        static float s_WorstFrameMs;
        static float s_FrameMsSum;
        static int s_FrameCount;
        static Vector3 s_LastCameraPosition;
        static Quaternion s_LastCameraRotation;
        static double s_LastCameraTime;
        static bool s_HasCamera;

        /// <summary>
        /// Clears the remembered state so the next recording starts fresh.
        /// </summary>
        public static void Reset()
        {
            s_Scene = null;
            s_NextPerfSample = 0d;
            s_NextCameraSample = 0d;
            s_WorstFrameMs = 0f;
            s_FrameMsSum = 0f;
            s_FrameCount = 0;
            s_HasCamera = false;
            s_LastCameraTime = 0d;
        }

        /// <summary>
        /// Samples the automatic channels for this frame.
        /// </summary>
        /// <param name="settings">Which channels are enabled and how often to sample them.</param>
        /// <param name="time">The current game time in seconds.</param>
        public static void Tick(LabeledRecorderSettings settings, double time)
        {
            if (settings.CaptureSceneChanges)
                TickScene();

            if (settings.CaptureTimeScale)
                GameLabel.Set("time.scale", Time.timeScale, 0.01f);

            if (settings.CapturePerformance)
                TickPerformance(settings, time);

            if (settings.CaptureCameraMotion)
                TickCamera(settings, time);
        }

        static void TickScene()
        {
            var scene = SceneManager.GetActiveScene().name;
            if (scene == s_Scene)
                return;

            // The first observation is the starting scene, not a transition.
            if (s_Scene != null)
                GameLabel.Event("scene.changed", "from", s_Scene, "to", scene);

            s_Scene = scene;
            GameLabel.Set("scene", scene);
        }

        static void TickPerformance(LabeledRecorderSettings settings, double time)
        {
            var frameMs = Time.unscaledDeltaTime * 1000f;
            s_FrameMsSum += frameMs;
            s_FrameCount++;
            if (frameMs > s_WorstFrameMs)
                s_WorstFrameMs = frameMs;

            if (time < s_NextPerfSample)
                return;

            s_NextPerfSample = time + settings.PerformanceInterval;

            if (s_FrameCount == 0)
                return;

            if (GameLabel.IsRecording)
            {
                var averageMs = s_FrameMsSum / s_FrameCount;
                GameLabel.Sample("perf.fps", averageMs > 0f ? 1000f / averageMs : 0f);
                GameLabel.Sample("perf.hitchMs", s_WorstFrameMs);
            }

            s_FrameMsSum = 0f;
            s_FrameCount = 0;
            s_WorstFrameMs = 0f;
        }

        static void TickCamera(LabeledRecorderSettings settings, double time)
        {
            if (time < s_NextCameraSample)
                return;

            s_NextCameraSample = time + 1d / Mathf.Max(1f, settings.CameraSampleRate);

            var camera = Camera.main;
            if (camera == null)
            {
                s_HasCamera = false;
                return;
            }

            var transform = camera.transform;
            var position = transform.position;
            var rotation = transform.rotation;

            GameLabel.Set("cam.name", camera.name);

            if (s_HasCamera && GameLabel.IsRecording)
            {
                var dt = (float)(time - s_LastCameraTime);
                if (dt > 0.0001f)
                {
                    GameLabel.Sample("cam.pos", position);
                    GameLabel.Sample("cam.speed", Vector3.Distance(position, s_LastCameraPosition) / dt);
                    GameLabel.Sample("cam.turnRate", Quaternion.Angle(rotation, s_LastCameraRotation) / dt);
                }
            }

            s_HasCamera = true;
            s_LastCameraPosition = position;
            s_LastCameraRotation = rotation;
            s_LastCameraTime = time;
        }
    }
}
