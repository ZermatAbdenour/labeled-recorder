using System.IO;
using UnityEditor.Recorder.Labeling;
using UnityEngine;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Works out where the sidecar label files for a recording belong.
    /// </summary>
    public static class LabelPaths
    {
        /// <summary>The suffix of the finished label file.</summary>
        public const string FinalSuffix = ".labels.json";

        /// <summary>The suffix of the append-as-you-go stream file.</summary>
        public const string StreamSuffix = ".labels.jsonl";

        /// <summary>
        /// Derives the base name shared by a recording's media and label files.
        /// </summary>
        /// <param name="view">The session whose output to describe.</param>
        /// <returns>
        /// The media path with its extension removed, and, for an image sequence, with the trailing frame number
        /// removed too, so a <c>shot_0001.png</c> sequence yields <c>shot</c> rather than one file per frame.
        /// </returns>
        public static string MediaBase(RecorderSessionView view)
        {
            var path = view.OutputPath;
            if (string.IsNullOrEmpty(path))
                return null;

            path = path.Replace('\\', '/');

            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);

            if (view.IsImageSequence)
                name = StripFrameNumber(name);

            return string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
        }

        /// <summary>
        /// Builds the final and streaming label file paths for a recording.
        /// </summary>
        /// <param name="view">The session whose output to describe.</param>
        /// <param name="settings">The project settings, which decide the output folder.</param>
        /// <param name="finalPath">Receives the path of the finished label file.</param>
        /// <param name="streamPath">Receives the path of the append-as-you-go file.</param>
        /// <returns>False when the recording has no resolvable output path.</returns>
        public static bool TryResolve(RecorderSessionView view, LabeledRecorderSettings settings,
            out string finalPath, out string streamPath)
        {
            finalPath = null;
            streamPath = null;

            var mediaBase = MediaBase(view);
            if (string.IsNullOrEmpty(mediaBase))
                return false;

            if (settings.OutputLocation == LabelOutputLocation.CustomFolder)
            {
                var folder = settings.CustomFolder;
                if (string.IsNullOrEmpty(folder))
                    folder = "Recordings/Labels";

                if (!Path.IsPathRooted(folder))
                    folder = Path.Combine(ProjectRoot(), folder);

                mediaBase = Path.Combine(folder, Path.GetFileName(mediaBase)).Replace('\\', '/');
            }

            finalPath = mediaBase + FinalSuffix;
            streamPath = mediaBase + StreamSuffix;
            return true;
        }

        /// <summary>
        /// The folder that contains the project's <c>Assets</c> directory.
        /// </summary>
        /// <returns>An absolute path with forward slashes.</returns>
        public static string ProjectRoot()
        {
            var assets = Application.dataPath.Replace('\\', '/');
            var cut = assets.LastIndexOf('/');
            return cut > 0 ? assets.Substring(0, cut) : assets;
        }

        /// <summary>
        /// Makes a path relative to the project root when it sits inside it, for a friendlier label file.
        /// </summary>
        /// <param name="absolute">The absolute path to shorten.</param>
        /// <returns>A project-relative path, or the input unchanged when it lies outside the project.</returns>
        public static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute))
                return absolute;

            var normalized = absolute.Replace('\\', '/');
            var root = ProjectRoot();

            return normalized.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(root.Length + 1)
                : normalized;
        }

        static string StripFrameNumber(string name)
        {
            var end = name.Length;
            while (end > 0 && char.IsDigit(name[end - 1]))
                end--;

            // Only treat it as a frame number if some of the name survives.
            if (end == 0 || end == name.Length)
                return name;

            while (end > 1 && (name[end - 1] == '_' || name[end - 1] == '-' || name[end - 1] == '.'))
                end--;

            return name.Substring(0, end);
        }
    }
}
