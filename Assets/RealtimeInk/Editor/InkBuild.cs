using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RealtimeInk.Editor
{
    public static class InkBuild
    {
        [MenuItem("Tools/Realtime Ink/Validate and Build Windows")]
        public static void ValidateAndBuildWindows()
        {
            InkValidation.Run();
            BuildWindows();
        }

        [MenuItem("Tools/Realtime Ink/Build Windows")]
        public static void BuildWindows()
        {
            InkDemoBuilder.EnsurePipeline();
            string scenePath = InkDemoBuilder.Root + "Demo/InkDemo.unity";
            if (!File.Exists(scenePath)) InkDemoBuilder.CreateDemoScene();
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var ink = UnityEngine.Object.FindFirstObjectByType<RealtimeInkCanvas>();
            if (!ink) throw new InvalidOperationException("Demo scene has no RealtimeInkCanvas.");
            var panel = ink.GetComponent<InkDemoPanel>();
            if (!panel) panel = ink.gameObject.AddComponent<InkDemoPanel>();
            panel.ink = ink;
            ink.ApplyStyle(InkStyle.Regular);
            // 演示场景采用当前版本的通用铺毫初值；预设加载本身不改写这个字段。
            ink.behavior.endpointDeposit = new InkBehaviorSettings().endpointDeposit;
            EditorSceneManager.SaveScene(scene);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.companyName = "RealtimeInk";
            PlayerSettings.productName = "Realtime Ink Calligraphy";
            PlayerSettings.bundleVersion = "0.3.0";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Directory.CreateDirectory("Build/Windows");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath }, target = BuildTarget.StandaloneWindows64,
                locationPathName = "Build/Windows/RealtimeInk.exe", options = BuildOptions.None
            });
            string summary = report.summary.result + " | Unity " + Application.unityVersion + " | " +
                report.summary.totalSize + " bytes | " + report.summary.totalTime + " | errors=" + report.summary.totalErrors;
            Directory.CreateDirectory("Validation"); File.WriteAllText("Validation/build-summary.txt", summary);
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException(summary);
            ExportSources();
            Debug.Log("INK_BUILD " + summary);
        }

        [MenuItem("Tools/Realtime Ink/Export Source Package")]
        public static void ExportSources()
        {
            if (File.Exists("README.md")) File.Copy("README.md", InkDemoBuilder.Root + "README.md", true);
            if (File.Exists("PARAMETERS.md")) File.Copy("PARAMETERS.md", InkDemoBuilder.Root + "PARAMETERS.md", true);
            if (File.Exists("EDITING.md")) File.Copy("EDITING.md", InkDemoBuilder.Root + "EDITING.md", true);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            AssetDatabase.ExportPackage("Assets/RealtimeInk", "RealtimeInk.unitypackage", ExportPackageOptions.Recurse);
            Debug.Log("INK_EXPORT RealtimeInk.unitypackage");
        }
    }
}
