using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RealtimeInk.Editor
{
    public static class InkDemoBuilder
    {
        public const string Root = "Assets/RealtimeInk/";

        [MenuItem("Tools/Realtime Ink/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            EnsurePipeline();
            Directory.CreateDirectory(Root + "Demo");
            // Unity 禁止在尚未命名的场景旁新建 additive scene；先完整保存到新路径，绝不丢弃对象。
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene existing = SceneManager.GetSceneAt(i);
                if (existing.isLoaded && string.IsNullOrEmpty(existing.path))
                {
                    string preserved = AssetDatabase.GenerateUniqueAssetPath(Root + "Demo/PreservedScene.unity");
                    if (!EditorSceneManager.SaveScene(existing, preserved))
                        throw new System.IO.IOException("无法保存未命名场景：" + preserved);
                    Debug.Log("已保留原未命名场景：" + preserved);
                }
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(UniversalAdditionalCameraData));
            Camera camera = cameraObject.GetComponent<Camera>();
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0, 0, -10);
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            var uiObject = new GameObject("Ink UI", typeof(Canvas), typeof(CanvasScaler));
            Canvas canvas = uiObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var marginObject = new GameObject("Paper Margin", typeof(RectTransform));
            marginObject.transform.SetParent(uiObject.transform, false);
            var margin = marginObject.GetComponent<RectTransform>();
            margin.anchorMin = Vector2.zero; margin.anchorMax = Vector2.one;
            margin.offsetMin = new Vector2(24, 24); margin.offsetMax = new Vector2(-24, -24);
            var paperObject = new GameObject("Paper", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            paperObject.transform.SetParent(margin, false);
            var fitter = paperObject.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fitter.aspectRatio = 1f;
            RawImage display = paperObject.GetComponent<RawImage>();
            display.raycastTarget = false;
            var writerObject = new GameObject("Realtime Ink", typeof(RealtimeInkCanvas), typeof(InkDemoControls), typeof(InkDemoPanel));
            var ink = writerObject.GetComponent<RealtimeInkCanvas>();
            AssignReferences(ink, display);
            writerObject.GetComponent<InkDemoControls>().ink = ink;
            writerObject.GetComponent<InkDemoPanel>().ink = ink;
            ink.ApplyStyle(InkStyle.Regular);
            string scenePath = AssetDatabase.GenerateUniqueAssetPath(Root + "Demo/InkDemo.unity");
            EditorSceneManager.SaveScene(scene, scenePath);
            if (Application.isBatchMode)
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            Selection.activeGameObject = writerObject;
            Debug.Log("Ink demo: " + scenePath + ". Play 后鼠标/触屏书写；1–6 风格，H/U/B 独立笔法，C 清纸、R 蘸墨、D 调试通道。");
        }

        internal static void AssignReferences(RealtimeInkCanvas ink, RawImage display)
        {
            ink.display = display;
            ink.dropletCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "Shaders/DropletBrush.compute");
            ink.diffusionCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "Shaders/InkDiffusion.compute");
            ink.compositeShader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "Shaders/InkComposite.shader");
        }

        internal static void EnsurePipeline()
        {
            // 只为没有 SRP 的演示工程创建管线；已有 URP 项目保留自己的 Renderer。
            if (GraphicsSettings.defaultRenderPipeline == null)
            {
                Directory.CreateDirectory(Root + "Demo");
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, AssetDatabase.GenerateUniqueAssetPath(Root + "Demo/InkRenderer.asset"));
                var pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, AssetDatabase.GenerateUniqueAssetPath(Root + "Demo/InkURP.asset"));
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
            }
            AssetDatabase.SaveAssets();
        }
    }
}
