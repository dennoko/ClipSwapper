#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ClipSwapper
{
    public class ClipSwapperWindow : EditorWindow
    {
        [Serializable]
        private class ClipUsage
        {
            public AnimationClip Clip;
            public HashSet<string> LayerNames = new HashSet<string>();
            public int ReferenceCount;
        }

        private AnimatorController _sourceController;
        private string _filterText = string.Empty;
        private Vector2 _scroll;
        private readonly Dictionary<AnimationClip, AnimationClip> _replacements = new Dictionary<AnimationClip, AnimationClip>();
        private readonly Dictionary<AnimationClip, ClipUsage> _usageMap = new Dictionary<AnimationClip, ClipUsage>();
        private List<ClipUsage> _usageList = new List<ClipUsage>();
        private string _newControllerName = string.Empty;
        private AnimatorController _lastScannedController;

        [MenuItem("Tools/Clip Swapper")]
        public static void Open()
        {
            // Open as a dockable editor tab (not a utility floating window)
            var win = GetWindow<ClipSwapperWindow>("Clip Swapper");
            win.minSize = new Vector2(600, 400);
            win.titleContent = new GUIContent("Clip Swapper");
            win.Show();
        }

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_sourceController == null))
            {
                DrawFilter();
                EditorGUILayout.Space(4);
                DrawUsageList();
                EditorGUILayout.Space();
                DrawSaveSection();
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Source Animator Controller", EditorStyles.boldLabel);
            var next = (AnimatorController)EditorGUILayout.ObjectField(_sourceController, typeof(AnimatorController), false);
            if (next != _sourceController)
            {
                _sourceController = next;
                if (_sourceController != null)
                {
                    // Suggest default name
                    _newControllerName = SuggestName(_sourceController.name);
                    ScanController();
                }
                else
                {
                    ClearScan();
                }
            }

            using (new EditorGUI.DisabledScope(_sourceController == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Scan / Refresh", GUILayout.Width(140)))
                {
                    ScanController();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawFilter()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Filter (Clip or Layer)", GUILayout.Width(160));
            var newFilter = EditorGUILayout.TextField(_filterText);
            EditorGUILayout.EndHorizontal();
            if (!string.Equals(newFilter, _filterText, StringComparison.Ordinal))
            {
                _filterText = newFilter;
                // No extra work needed; list is filtered on draw
            }
        }

        private void DrawUsageList()
        {
            if (_usageList == null || _usageList.Count == 0)
            {
                EditorGUILayout.HelpBox(_sourceController == null ? "Assign an Animator Controller to begin." : "No animation clips found in this Animator Controller.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Clips Found: {_usageList.Count}", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Original Clip", EditorStyles.miniBoldLabel, GUILayout.Width(position.width * 0.48f));
            EditorGUILayout.LabelField("Replacement Clip", EditorStyles.miniBoldLabel, GUILayout.Width(position.width * 0.48f));
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            var filter = _filterText ?? string.Empty;
            bool hasFilter = !string.IsNullOrEmpty(filter);

            // Group by layer name
            var groups = new SortedDictionary<string, List<ClipUsage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var usage in _usageList)
            {
                if (usage?.Clip == null) continue;
                foreach (var layer in usage.LayerNames)
                {
                    if (!groups.TryGetValue(layer, out var list))
                    {
                        list = new List<ClipUsage>();
                        groups[layer] = list;
                    }
                    list.Add(usage);
                }
            }

            foreach (var kv in groups)
            {
                var layerName = kv.Key;
                var list = kv.Value.Distinct().OrderBy(u => u.Clip.name).ToList();

                // Filtering rules: if layer matches, show whole group; else show only rows whose clip matches
                List<ClipUsage> rowsToShow;
                if (!hasFilter)
                {
                    rowsToShow = list;
                }
                else if (ContainsIC(layerName, filter))
                {
                    rowsToShow = list;
                }
                else
                {
                    rowsToShow = list.Where(u => ContainsIC(u.Clip.name, filter)).ToList();
                }

                if (rowsToShow.Count == 0) continue;

                bool headerShown = false;
                foreach (var usage in rowsToShow)
                {
                    // Layer name just above the first Original Clip row in this group
                    if (!headerShown)
                    {
                        EditorGUILayout.LabelField(new GUIContent(layerName, layerName), EditorStyles.miniBoldLabel);
                        headerShown = true;
                    }

                    EditorGUILayout.BeginHorizontal();

                    // Original clip (left column)
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(usage.Clip, typeof(AnimationClip), false, GUILayout.Width(position.width * 0.48f));
                    }

                    // Replacement (right column)
                    _replacements.TryGetValue(usage.Clip, out var current);
                    var next = (AnimationClip)EditorGUILayout.ObjectField(current, typeof(AnimationClip), false, GUILayout.Width(position.width * 0.48f));
                    if (next != current)
                    {
                        if (next == null)
                        {
                            _replacements.Remove(usage.Clip);
                        }
                        else
                        {
                            _replacements[usage.Clip] = next;
                        }
                    }

                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space(2);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSaveSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generate New Animator Controller", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("New Name", GUILayout.Width(80));
            _newControllerName = EditorGUILayout.TextField(_newControllerName);
            if (GUILayout.Button("Suggest", GUILayout.Width(80)))
            {
                _newControllerName = _sourceController != null ? SuggestName(_sourceController.name) : "NewController_Override";
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newControllerName) || _sourceController == null))
            {
                if (GUILayout.Button("Generate", GUILayout.Width(140), GUILayout.Height(26)))
                {
                    try
                    {
                        GenerateController();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Clip Swapper: Failed to generate controller.\n{ex}");
                        EditorUtility.DisplayDialog("Clip Swapper", "Failed to generate controller. See Console for details.", "OK");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void ScanController()
        {
            _lastScannedController = _sourceController;
            _usageMap.Clear();
            _usageList.Clear();
            _replacements.Clear();

            if (_sourceController == null) return;

            foreach (var layer in _sourceController.layers)
            {
                if (layer?.stateMachine == null) continue;
                CollectFromStateMachine(layer.stateMachine, layer.name);
            }

            _usageList = _usageMap.Values
                .OrderBy(u => u.Clip != null ? u.Clip.name : string.Empty)
                .ToList();
        }

        private void ClearScan()
        {
            _usageMap.Clear();
            _usageList.Clear();
            _replacements.Clear();
        }

        private void CollectFromStateMachine(AnimatorStateMachine sm, string layerName)
        {
            if (sm == null) return;

            foreach (var childState in sm.states)
            {
                var state = childState.state;
                if (state == null) continue;
                CollectFromMotion(state.motion, layerName);
            }

            foreach (var childSM in sm.stateMachines)
            {
                CollectFromStateMachine(childSM.stateMachine, layerName);
            }
        }

        private void CollectFromMotion(Motion motion, string layerName)
        {
            if (motion == null) return;

            if (motion is AnimationClip clip)
            {
                AddUsage(clip, layerName);
            }
            else if (motion is BlendTree bt)
            {
                CollectFromBlendTree(bt, layerName);
            }
        }

        private void CollectFromBlendTree(BlendTree bt, string layerName)
        {
            if (bt == null) return;

            foreach (var child in bt.children)
            {
                if (child.motion is AnimationClip c)
                {
                    AddUsage(c, layerName);
                }
                else if (child.motion is BlendTree childBT)
                {
                    CollectFromBlendTree(childBT, layerName);
                }
            }
        }

        private void AddUsage(AnimationClip clip, string layerName)
        {
            if (clip == null) return;
            if (!_usageMap.TryGetValue(clip, out var usage))
            {
                usage = new ClipUsage { Clip = clip, ReferenceCount = 0 };
                _usageMap.Add(clip, usage);
            }
            usage.ReferenceCount++;
            if (!string.IsNullOrEmpty(layerName)) usage.LayerNames.Add(layerName);
        }

        private void GenerateController()
        {
            if (_sourceController == null)
            {
                EditorUtility.DisplayDialog("Clip Swapper", "Assign a source Animator Controller first.", "OK");
                return;
            }

            var srcPath = AssetDatabase.GetAssetPath(_sourceController);
            if (string.IsNullOrEmpty(srcPath))
            {
                EditorUtility.DisplayDialog("Clip Swapper", "Could not determine source asset path.", "OK");
                return;
            }

            var dir = Path.GetDirectoryName(srcPath)?.Replace('\\', '/');
            var fileName = MakeValidFileName(_newControllerName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                EditorUtility.DisplayDialog("Clip Swapper", "Please enter a valid new controller name.", "OK");
                return;
            }

            var newPath = dir + "/" + fileName + ".controller";

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(newPath) != null)
            {
                var overwrite = EditorUtility.DisplayDialog("Clip Swapper", $"{newPath} already exists. Overwrite?", "Overwrite", "Cancel");
                if (!overwrite) return;
                AssetDatabase.DeleteAsset(newPath);
            }

            if (!AssetDatabase.CopyAsset(srcPath, newPath))
            {
                EditorUtility.DisplayDialog("Clip Swapper", "Failed to copy Animator Controller asset.", "OK");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var newCtrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(newPath);
            if (newCtrl == null)
            {
                EditorUtility.DisplayDialog("Clip Swapper", "Failed to load newly created Animator Controller.", "OK");
                return;
            }

            // Build mapping: only non-null and different
            var mapping = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var kvp in _replacements)
            {
                if (kvp.Key == null || kvp.Value == null) continue;
                if (kvp.Key == kvp.Value) continue;
                mapping[kvp.Key] = kvp.Value;
            }

            if (mapping.Count > 0)
            {
                ReplaceClipsInController(newCtrl, mapping);
                EditorUtility.SetDirty(newCtrl);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Selection.activeObject = newCtrl;
            EditorGUIUtility.PingObject(newCtrl);
            EditorUtility.DisplayDialog("Clip Swapper", "New Animator Controller generated successfully.", "OK");
        }

        private static void ReplaceClipsInController(AnimatorController ctrl, Dictionary<AnimationClip, AnimationClip> mapping)
        {
            if (ctrl == null || mapping == null || mapping.Count == 0) return;

            foreach (var layer in ctrl.layers)
            {
                if (layer?.stateMachine == null) continue;
                ReplaceInStateMachine(layer.stateMachine, mapping);
            }
        }

        private static void ReplaceInStateMachine(AnimatorStateMachine sm, Dictionary<AnimationClip, AnimationClip> mapping)
        {
            if (sm == null) return;

            foreach (var child in sm.states)
            {
                var state = child.state;
                if (state == null) continue;
                state.motion = ReplaceInMotion(state.motion, mapping);
            }

            foreach (var sub in sm.stateMachines)
            {
                ReplaceInStateMachine(sub.stateMachine, mapping);
            }
        }

        private static Motion ReplaceInMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> mapping)
        {
            if (motion == null) return null;

            if (motion is AnimationClip clip)
            {
                if (mapping.TryGetValue(clip, out var replacement) && replacement != null)
                {
                    return replacement;
                }
                return clip;
            }

            if (motion is BlendTree bt)
            {
                ReplaceInBlendTree(bt, mapping);
                return bt;
            }

            return motion;
        }

        private static void ReplaceInBlendTree(BlendTree bt, Dictionary<AnimationClip, AnimationClip> mapping)
        {
            if (bt == null) return;

            var children = bt.children;
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (child.motion is AnimationClip ac)
                {
                    if (mapping.TryGetValue(ac, out var rep) && rep != null)
                    {
                        child.motion = rep;
                        children[i] = child;
                    }
                }
                else if (child.motion is BlendTree nested)
                {
                    ReplaceInBlendTree(nested, mapping);
                }
            }

            bt.children = children; // assign back to apply changes
        }

        private static bool ContainsIC(string source, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            if (string.IsNullOrEmpty(source)) return false;
            return source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SuggestName(string originalName)
        {
            if (string.IsNullOrWhiteSpace(originalName)) return "NewController_Override";
            return originalName + "_Override";
        }

        private static string MakeValidFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            var pattern = "[" + Regex.Escape(invalid) + "]";
            var cleaned = Regex.Replace(name.Trim(), pattern, "_");
            // Unity asset names should not contain extension here; we'll add .controller outside
            return cleaned;
        }
    }
}
#endif
