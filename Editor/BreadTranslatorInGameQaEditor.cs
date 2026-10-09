using UnityEditor;
using UnityEngine;

namespace BreadTranslator.Unity.Editor
{
    [CustomEditor(typeof(BreadTranslatorInGameQa))]
    public sealed class BreadTranslatorInGameQaEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Runtime credentials must come from a platform keystore callback or process environment. They are never stored in this component or project assets.", MessageType.Info);
            if (GUILayout.Button("Flush private QA queue"))
            {
                var sdk = (BreadTranslatorInGameQa)target;
                if (Application.isPlaying) sdk.StartCoroutine(sdk.Flush((sent, deferred) => Debug.Log($"BreadTranslator QA flush: sent={sent}, deferred={deferred}")));
                else EditorUtility.DisplayDialog("BreadTranslator", "Queue flush requires Play Mode so runtime credential providers and networking are available.", "OK");
            }
        }
    }
}
