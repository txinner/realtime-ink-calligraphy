using UnityEditor;
using UnityEngine;

namespace RealtimeInk.Editor
{
    [CustomEditor(typeof(RealtimeInkCanvas))]
    public sealed class RealtimeInkCanvasEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var ink = (RealtimeInkCanvas)target;
            EditorGUILayout.HelpBox("加载的是参数快照，不锁定字体；辅助开关、耗墨曲线、设备设置与画布内容保持。", MessageType.Info);
            for (int row = 0; row < 2; row++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int column = 0; column < 3; column++)
                {
                    var style = (InkStyle)(row * 3 + column);
                    if (GUILayout.Button(InkStyleLibrary.Name(style)))
                    {
                        Undo.RecordObject(ink, "Load Ink Style Snapshot");
                        ink.ApplyStyle(style); EditorUtility.SetDirty(ink);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space();
            DrawDefaultInspector();
        }
    }
}
