using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// The <b>Project Settings &gt; Labeled Recorder</b> page.
    /// </summary>
    static class LabeledRecorderSettingsProvider
    {
        const string k_Path = "Project/Labeled Recorder";

        static readonly GUIContent k_Enabled = new GUIContent("Enable Label Capture",
            "Write a sidecar label file next to every Recorder capture. When off, nothing is hooked and every " +
            "GameLabel call in your game costs a single bool read.");

        static readonly GUIContent k_BufferCapacity = new GUIContent("Buffer Capacity",
            "How many labels can be in flight between the game thread and the writer. Raise this only if a " +
            "label file reports dropped labels.");

        static readonly GUIContent k_MaxLabels = new GUIContent("Max Labels Per Recording",
            "A ceiling on how many labels one recording keeps in memory.");

        static readonly GUIContent k_Location = new GUIContent("Output Location",
            "Next To Media keeps the label file paired with its video, which is what the MCP server expects.");

        static readonly GUIContent k_Folder = new GUIContent("Folder",
            "Relative to the project root unless the path is absolute.");

        static readonly GUIContent k_Stream = new GUIContent("Write Stream File",
            "Append every label to a .labels.jsonl file as it happens, so a crash still leaves a usable record.");

        static readonly GUIContent k_KeepStream = new GUIContent("Keep Stream File",
            "Keep the .labels.jsonl file after the finished .labels.json has been written.");

        static readonly GUIContent k_Pretty = new GUIContent("Pretty Print",
            "Indent the label file. Turn this off for very long recordings.");

        static readonly GUIContent k_Verbose = new GUIContent("Verbose Logging",
            "Log every recording start, stop and file write to the Console.");

        static readonly GUIContent k_Scene = new GUIContent("Scene Changes",
            "Record the active scene and every scene transition.");

        static readonly GUIContent k_TimeScale = new GUIContent("Time Scale",
            "Record changes to Time.timeScale, which is how pauses and slow motion show up.");

        static readonly GUIContent k_Perf = new GUIContent("Frame Pacing",
            "Record average frame rate and worst frame time, so an editing agent can avoid cutting to footage " +
            "that stuttered.");

        static readonly GUIContent k_PerfInterval = new GUIContent("Sample Interval (s)", "Seconds between frame pacing samples.");

        static readonly GUIContent k_Camera = new GUIContent("Camera Motion",
            "Record the main camera's position, speed and turn rate, a usable proxy for how much is happening " +
            "on screen.");

        static readonly GUIContent k_CameraRate = new GUIContent("Samples Per Second", "How often to sample the camera.");

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            var keywords = new HashSet<string>(new[]
            {
                "labeled", "recorder", "label", "labels", "timecode", "trailer", "shorts", "mcp", "metadata"
            });

            return new SettingsProvider(k_Path, SettingsScope.Project)
            {
                label = "Labeled Recorder",
                keywords = keywords,
                guiHandler = _ => Draw()
            };
        }

        static void Draw()
        {
            var settings = LabeledRecorderSettings.instance;

            EditorGUIUtility.labelWidth = 220f;
            EditorGUILayout.Space(6f);

            EditorGUI.BeginChangeCheck();
            var enabled = EditorGUILayout.Toggle(k_Enabled, settings.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                settings.Enabled = enabled;
                LabelCapture.Refresh();
            }

            EditorGUILayout.Space(4f);
            DrawStatus(settings);
            EditorGUILayout.Space(8f);

            using (new EditorGUI.DisabledScope(!settings.Enabled))
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                settings.OutputLocation = (LabelOutputLocation)EditorGUILayout.EnumPopup(k_Location, settings.OutputLocation);

                if (settings.OutputLocation == LabelOutputLocation.CustomFolder)
                {
                    EditorGUI.indentLevel++;
                    settings.CustomFolder = EditorGUILayout.TextField(k_Folder, settings.CustomFolder);
                    EditorGUI.indentLevel--;
                }

                settings.WriteStreamFile = EditorGUILayout.Toggle(k_Stream, settings.WriteStreamFile);

                using (new EditorGUI.DisabledScope(!settings.WriteStreamFile))
                {
                    EditorGUI.indentLevel++;
                    settings.KeepStreamFile = EditorGUILayout.Toggle(k_KeepStream, settings.KeepStreamFile);
                    EditorGUI.indentLevel--;
                }

                settings.PrettyPrint = EditorGUILayout.Toggle(k_Pretty, settings.PrettyPrint);

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Automatic Channels", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "These need no code in your game. They are what makes a recording useful to an editing " +
                    "agent before you have labelled anything yourself.", MessageType.None);

                settings.CaptureSceneChanges = EditorGUILayout.Toggle(k_Scene, settings.CaptureSceneChanges);
                settings.CaptureTimeScale = EditorGUILayout.Toggle(k_TimeScale, settings.CaptureTimeScale);

                settings.CapturePerformance = EditorGUILayout.Toggle(k_Perf, settings.CapturePerformance);
                using (new EditorGUI.DisabledScope(!settings.CapturePerformance))
                {
                    EditorGUI.indentLevel++;
                    settings.PerformanceInterval = EditorGUILayout.Slider(k_PerfInterval, settings.PerformanceInterval, 0.1f, 10f);
                    EditorGUI.indentLevel--;
                }

                settings.CaptureCameraMotion = EditorGUILayout.Toggle(k_Camera, settings.CaptureCameraMotion);
                using (new EditorGUI.DisabledScope(!settings.CaptureCameraMotion))
                {
                    EditorGUI.indentLevel++;
                    settings.CameraSampleRate = EditorGUILayout.Slider(k_CameraRate, settings.CameraSampleRate, 1f, 60f);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
                settings.BufferCapacity = EditorGUILayout.IntField(k_BufferCapacity, settings.BufferCapacity);
                settings.MaxLabelsPerRecording = EditorGUILayout.IntField(k_MaxLabels, settings.MaxLabelsPerRecording);
                settings.VerboseLog = EditorGUILayout.Toggle(k_Verbose, settings.VerboseLog);
            }

            EditorGUILayout.Space(12f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Label File Format", GUILayout.Height(22f)))
                    LabeledRecorderMenu.OpenFormatDocumentation();

                if (GUILayout.Button("Copy MCP Server Config", GUILayout.Height(22f)))
                    LabeledRecorderMenu.CopyMcpConfig();
            }
        }

        static void DrawStatus(LabeledRecorderSettings settings)
        {
            if (!settings.Enabled)
            {
                EditorGUILayout.HelpBox("Label capture is off. Recordings will be written without a label file.",
                    MessageType.None);
                return;
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Ready. Enter play mode and start a recording from the Recorder window; " +
                    "the label file appears next to the video when the recording stops.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(LabelCapture.IsCapturing
                ? $"Labelling {LabelCapture.ActiveRecordingCount} recording(s)."
                : "In play mode, waiting for a recording to start.", MessageType.Info);
        }
    }
}
