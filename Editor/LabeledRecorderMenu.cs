using System.IO;
using UnityEditor;
using UnityEngine;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// The <b>Tools &gt; Labeled Recorder</b> menu.
    /// </summary>
    public static class LabeledRecorderMenu
    {
        const string k_Root = "Tools/Labeled Recorder/";
        const string k_ToggleItem = k_Root + "Enable Label Capture";

        [MenuItem(k_ToggleItem, priority = 100)]
        static void ToggleEnabled()
        {
            var settings = LabeledRecorderSettings.instance;
            settings.Enabled = !settings.Enabled;
            LabelCapture.Refresh();

            Debug.Log(settings.Enabled
                ? "Labeled Recorder: label capture enabled."
                : "Labeled Recorder: label capture disabled.");
        }

        [MenuItem(k_ToggleItem, validate = true)]
        static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(k_ToggleItem, LabeledRecorderSettings.instance.Enabled);
            return true;
        }

        [MenuItem(k_Root + "Settings...", priority = 200)]
        static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Labeled Recorder");
        }

        [MenuItem(k_Root + "Label File Format", priority = 300)]
        static void OpenFormatDocumentationItem()
        {
            OpenFormatDocumentation();
        }

        [MenuItem(k_Root + "Copy MCP Server Config", priority = 400)]
        static void CopyMcpConfigItem()
        {
            CopyMcpConfig();
        }

        [MenuItem(k_Root + "Reveal MCP Server Folder", priority = 401)]
        static void RevealMcpServer()
        {
            var folder = McpServerFolder();

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Debug.LogWarning("Labeled Recorder: could not locate the bundled MCP server folder.");
                return;
            }

            EditorUtility.RevealInFinder(folder + "/package.json");
        }

        /// <summary>
        /// Opens the label file format reference in the system's default application.
        /// </summary>
        public static void OpenFormatDocumentation()
        {
            var path = Path.Combine(PackageRoot() ?? string.Empty, "Documentation~/label-file-format.md");

            if (!File.Exists(path))
            {
                Debug.LogWarning($"Labeled Recorder: '{path}' is missing.");
                return;
            }

            EditorUtility.OpenWithDefaultApp(path);
        }

        /// <summary>
        /// Puts a ready-to-paste MCP client configuration block on the clipboard, pointing at the bundled server
        /// and at this project's folder as the place to look for recordings.
        /// </summary>
        public static void CopyMcpConfig()
        {
            var entry = Path.Combine(McpServerFolder() ?? string.Empty, "dist/index.js").Replace('\\', '/');
            var roots = LabelPaths.ProjectRoot();

            var json =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"labeled-recorder\": {\n" +
                "      \"command\": \"node\",\n" +
                "      \"args\": [\"" + entry.Replace("\"", "\\\"") + "\"],\n" +
                "      \"env\": {\n" +
                "        \"LABELED_RECORDER_ROOTS\": \"" + roots.Replace("\"", "\\\"") + "\"\n" +
                "      }\n" +
                "    }\n" +
                "  }\n" +
                "}";

            EditorGUIUtility.systemCopyBuffer = json;
            Debug.Log("Labeled Recorder: MCP server configuration copied to the clipboard.\n" + json);
        }

        /// <summary>
        /// The absolute path of the installed package, or null when it cannot be resolved.
        /// </summary>
        /// <returns>A path with forward slashes.</returns>
        public static string PackageRoot()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(LabeledRecorderMenu).Assembly);

            return package?.resolvedPath?.Replace('\\', '/');
        }

        /// <summary>
        /// The absolute path of the bundled MCP server, or null when it cannot be resolved.
        /// </summary>
        /// <returns>A path with forward slashes.</returns>
        public static string McpServerFolder()
        {
            var root = PackageRoot();
            return root == null ? null : root + "/McpServer~";
        }
    }
}
