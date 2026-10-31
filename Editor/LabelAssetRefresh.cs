using System.Collections.Generic;
using UnityEditor;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// Imports label files that land inside the project's <c>Assets</c> folder, so they show up in the Project
    /// window without a manual refresh.
    /// </summary>
    /// <remarks>
    /// Imports are deferred to the next Editor update rather than run during play mode, because importing assets
    /// mid-frame can stall the running game.
    /// </remarks>
    public static class LabelAssetRefresh
    {
        static readonly List<string> s_Pending = new List<string>();
        static bool s_Scheduled;

        /// <summary>
        /// Queues a written label file for import, if it lives inside the project.
        /// </summary>
        /// <param name="absolutePath">The absolute path of the file that was written.</param>
        public static void Queue(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return;

            var relative = LabelPaths.ToProjectRelative(absolutePath);
            if (!relative.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                return;

            s_Pending.Add(relative);

            if (s_Scheduled)
                return;

            s_Scheduled = true;
            EditorApplication.delayCall += Flush;
        }

        static void Flush()
        {
            EditorApplication.delayCall -= Flush;
            s_Scheduled = false;

            for (var i = 0; i < s_Pending.Count; i++)
                AssetDatabase.ImportAsset(s_Pending[i], ImportAssetOptions.Default);

            s_Pending.Clear();
        }
    }
}
