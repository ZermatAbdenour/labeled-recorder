using UnityEditor;
using UnityEngine;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Where the sidecar label file is written.
    /// </summary>
    public enum LabelOutputLocation
    {
        /// <summary>Next to the recorded media, sharing its name. This is what keeps media and labels paired.</summary>
        NextToMedia = 0,
        /// <summary>In one folder, named after the media file.</summary>
        CustomFolder = 1
    }

    /// <summary>
    /// Project-wide configuration for label capture, stored in <c>ProjectSettings/LabeledRecorder.asset</c> so it
    /// is shared through version control and never reaches a build.
    /// </summary>
    [FilePath("ProjectSettings/LabeledRecorder.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class LabeledRecorderSettings : ScriptableSingleton<LabeledRecorderSettings>
    {
        [SerializeField] bool m_Enabled = true;
        [SerializeField] int m_BufferCapacity = 4096;
        [SerializeField] int m_MaxLabelsPerRecording = 500000;
        [SerializeField] LabelOutputLocation m_OutputLocation = LabelOutputLocation.NextToMedia;
        [SerializeField] string m_CustomFolder = "Recordings/Labels";
        [SerializeField] bool m_WriteStreamFile = true;
        [SerializeField] bool m_KeepStreamFile;
        [SerializeField] bool m_PrettyPrint = true;
        [SerializeField] bool m_VerboseLog;

        [SerializeField] bool m_CaptureSceneChanges = true;
        [SerializeField] bool m_CaptureTimeScale = true;
        [SerializeField] bool m_CapturePerformance = true;
        [SerializeField] float m_PerformanceInterval = 1f;
        [SerializeField] bool m_CaptureCameraMotion = true;
        [SerializeField] float m_CameraSampleRate = 10f;

        /// <summary>
        /// The master switch. When false nothing is hooked, no label file is written, and every
        /// <see cref="GameLabel"/> call in your game costs a single static <c>bool</c> read.
        /// </summary>
        public bool Enabled
        {
            get => m_Enabled;
            set => Assign(ref m_Enabled, value);
        }

        /// <summary>
        /// How many labels can be in flight between the game thread and the writer. Raise it only if a label file
        /// reports dropped labels. Rounded up to a power of two.
        /// </summary>
        public int BufferCapacity
        {
            get => m_BufferCapacity;
            set => Assign(ref m_BufferCapacity, Mathf.Clamp(value, 256, 1 << 20));
        }

        /// <summary>
        /// A ceiling on how many labels one recording keeps in memory. Past it labels are counted but discarded,
        /// and the label file says so.
        /// </summary>
        public int MaxLabelsPerRecording
        {
            get => m_MaxLabelsPerRecording;
            set => Assign(ref m_MaxLabelsPerRecording, Mathf.Max(1000, value));
        }

        /// <summary>Where to write the label file.</summary>
        public LabelOutputLocation OutputLocation
        {
            get => m_OutputLocation;
            set => Assign(ref m_OutputLocation, value);
        }

        /// <summary>
        /// The folder used when <see cref="OutputLocation"/> is <see cref="LabelOutputLocation.CustomFolder"/>,
        /// relative to the project root unless it is an absolute path.
        /// </summary>
        public string CustomFolder
        {
            get => m_CustomFolder;
            set => Assign(ref m_CustomFolder, value);
        }

        /// <summary>
        /// Append every label to a <c>.labels.jsonl</c> file as it happens, so a crash or a forced exit from play
        /// mode still leaves a usable record. Recommended.
        /// </summary>
        public bool WriteStreamFile
        {
            get => m_WriteStreamFile;
            set => Assign(ref m_WriteStreamFile, value);
        }

        /// <summary>Keep the <c>.labels.jsonl</c> stream file after the final label file is written.</summary>
        public bool KeepStreamFile
        {
            get => m_KeepStreamFile;
            set => Assign(ref m_KeepStreamFile, value);
        }

        /// <summary>Indent the label file. Turn this off for large recordings.</summary>
        public bool PrettyPrint
        {
            get => m_PrettyPrint;
            set => Assign(ref m_PrettyPrint, value);
        }

        /// <summary>Log every recording start, stop and file write to the Console.</summary>
        public bool VerboseLog
        {
            get => m_VerboseLog;
            set => Assign(ref m_VerboseLog, value);
        }

        /// <summary>Record scene loads and the active scene at the start of the recording.</summary>
        public bool CaptureSceneChanges
        {
            get => m_CaptureSceneChanges;
            set => Assign(ref m_CaptureSceneChanges, value);
        }

        /// <summary>Record changes to <c>Time.timeScale</c>, which reveals pauses and slow-motion.</summary>
        public bool CaptureTimeScale
        {
            get => m_CaptureTimeScale;
            set => Assign(ref m_CaptureTimeScale, value);
        }

        /// <summary>
        /// Record frame time as a signal, so an editing agent can avoid cutting to footage that stuttered.
        /// </summary>
        public bool CapturePerformance
        {
            get => m_CapturePerformance;
            set => Assign(ref m_CapturePerformance, value);
        }

        /// <summary>How many seconds between performance samples.</summary>
        public float PerformanceInterval
        {
            get => m_PerformanceInterval;
            set => Assign(ref m_PerformanceInterval, Mathf.Clamp(value, 0.1f, 60f));
        }

        /// <summary>
        /// Record the main camera's position, orientation and speed, which gives an agent a usable proxy for
        /// on-screen motion without any work on your side.
        /// </summary>
        public bool CaptureCameraMotion
        {
            get => m_CaptureCameraMotion;
            set => Assign(ref m_CaptureCameraMotion, value);
        }

        /// <summary>How many camera samples per second to take.</summary>
        public float CameraSampleRate
        {
            get => m_CameraSampleRate;
            set => Assign(ref m_CameraSampleRate, Mathf.Clamp(value, 1f, 120f));
        }

        /// <summary>
        /// Writes pending changes to <c>ProjectSettings/LabeledRecorder.asset</c>.
        /// </summary>
        public void Persist()
        {
            Save(true);
        }

        void Assign<T>(ref T field, T value)
        {
            if (Equals(field, value))
                return;

            field = value;
            Save(true);
        }
    }
}
