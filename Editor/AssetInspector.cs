using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
// ReSharper disable StringLiteralTypo
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

// ReSharper disable once CheckNamespace
namespace AssetInspector
{
    public class AssetInspectorWindow : EditorWindow
    {
        private const string PrefLastBuildLayoutFolder = "AssetInspector_LastBuildLayoutFolder";
        private const string PrefLastBuildLayoutPath = "AssetInspector_LastBuildLayoutPath";

        private static readonly GUIContent DependenciesFoldoutContent = new GUIContent("Dependencies",
            "Hierarchy of direct AssetDatabase.GetDependencies per asset; nested lists show transitive refs.");

        private static readonly GUIContent ExternalGuidsFoldoutContent = new GUIContent("External GUIDs",
            "fileID / guid pairs scanned from the serialized asset.");

        private static readonly GUIContent DependencyFilterFieldContent = new GUIContent("Dependency filter",
            "Shows only dependency rows whose file name or full project path contains this text (case-insensitive). Filters the list and exports; it does not change the scan.");

        private static readonly GUIContent ExternalGuidFilterFieldContent = new GUIContent("External GUID filter",
            "Shows only external GUID rows whose file name or path contains this text (case-insensitive). Filters the list; it does not change the scan.");

        private static readonly GUIContent CopyFilteredTsvContent = new GUIContent("Copy dependency table (TSV)",
            "Copies the currently filtered dependency rows to the clipboard as tab-separated values for spreadsheets.");

        private static readonly GUIContent SaveFilteredCsvContent = new GUIContent("Save filtered CSV…",
            "Saves the currently filtered dependency rows to a CSV file (UTF-8) at a path you choose.");

        private static readonly GUIContent RefreshAnalysisContent = new GUIContent("Refresh Analysis",
            "Re-runs dependency and external-GUID analysis on the same assets as the current results (not the Project selection).");

        private static readonly GUIContent ResetAnalysisContent = new GUIContent("< Back",
            "Clears results and returns to target selection. Your assets and GUID entries are kept.");

        private static readonly GUIContent RunAnalysisContent = new GUIContent("Run Analysis",
            "Scans the assets listed below: dependencies and external GUIDs.");

        private static readonly Regex Guid32Hex = new(@"^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

        private static readonly List<string> KeyWordsToIgnore = new()
        {
            "objectReference: {fileID:",
            "m_CorrespondingSourceObject: {fileID:",
            "m_PrefabInstance: {fileID:",
            "m_PrefabAsset: {fileID:",
            "m_GameObject: {fileID:",
            "m_Icon: {fileID:",
            "m_Father: {fileID:"
        };

        private static readonly List<string> KeyWordsToIgnoreInSceneAsset = new()
        {
            "m_OcclusionCullingData: {fileID:",
            "m_HaloTexture: {fileID:",
            "m_CustomReflection: {fileID:",
            "m_Sun: {fileID:",
            "m_LightmapParameters: {fileID:",
            "m_LightingDataAsset: {fileID:",
            "m_LightingSettings: {fileID:",
            "m_NavMeshData: {fileID:",
            "m_Icon: {fileID:",
            "m_StaticBatchRoot: {fileID:",
            "m_ProbeAnchor: {fileID:",
            "m_LightProbeVolumeOverride: {fileID:",
            "m_Cookie: {fileID:",
            "m_Flare: {fileID:",
            "m_TargetTexture: {fileID:"
        };

        private AddressablesData _addressablesData;
        private LiteBuildLayoutProvider _buildLayout;
        private string _buildLayoutPath;
        private string _dependencyFilter = "";
        private bool _emptyAnalysis;
        private bool _excludeCodeAssets = true;
        private string _externalGuidFilter = "";

        private readonly List<PendingTarget> _pendingTargets = new();

        private Vector2 _idleScrollPos;

        private Dictionary<Object, AssetReferencesData> _lastResults;

        private Vector2 _scrollPos = Vector2.zero;

        private Object[] _selectedObjects;
        private bool[] _selectedObjectsFoldouts;
        private bool _settingsFoldout;
        private bool _waitingAfterProjectChange;

        private sealed class PendingTarget
        {
            public Object Asset;
            public string GuidText = "";
            public bool GuidNotFound;
        }

        private void OnDestroy()
        {
            ShutdownWindowPersistingLayoutPrefs();
        }

        private void OnGUI()
        {
            GUILayout.BeginVertical();

            if (_waitingAfterProjectChange && _lastResults == null)
            {
                EditorGUILayout.HelpBox(
                    "Project asset database changed. Use Refresh Analysis to repeat the scan on the preserved asset list below, dismiss to continue editing targets, or use Tools › Inspect Assets and GUIDs after fixing imports.",
                    MessageType.Warning);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                var bgStale = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.45f, 0.82f, 0.52f);
                if (GUILayout.Button(RefreshAnalysisContent, GUILayout.Width(160)))
                {
                    RunAnalysisFromPending();
                }
                GUI.backgroundColor = bgStale;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Dismiss", GUILayout.Width(100)))
                    _waitingAfterProjectChange = false;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
                return;
            }

            if (_lastResults == null)
            {
                EnsureDefaultPendingRows();
                DrawIdlePickerPanel();
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal();
            var bgR = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.92f, 0.45f);
            if (GUILayout.Button(ResetAnalysisContent, GUILayout.Width(100)))
                ResetAnalysis();
            GUI.backgroundColor = bgR;
            GUILayout.FlexibleSpace();
            GUI.backgroundColor = new Color(0.45f, 0.82f, 0.52f);
            if (GUILayout.Button(RefreshAnalysisContent, GUILayout.Width(160)))
                RefreshAnalysisFromResults();
            GUI.backgroundColor = bgR;
            GUILayout.EndHorizontal();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_emptyAnalysis)
            {
                EditorGUILayout.HelpBox(
                    "No valid selection to analyze. Pick project assets, then refresh or reopen this window from the Assets menu.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings");
            if (_settingsFoldout)
            {
                EditorGUILayout.HelpBox("Hiding .cs only filters the table and exports; the scan is unchanged.",
                    MessageType.None);

                _excludeCodeAssets = GUILayout.Toggle(_excludeCodeAssets,
                    new GUIContent("Hide .cs in dependency list",
                        "Display filter only—no re-scan."));

                GUIUtilities.HorizontalLine();

                _dependencyFilter =
                    EditorGUILayout.DelayedTextField(DependencyFilterFieldContent, _dependencyFilter);
                _externalGuidFilter =
                    EditorGUILayout.DelayedTextField(ExternalGuidFilterFieldContent, _externalGuidFilter);

                GUIUtilities.HorizontalLine();

                GUILayout.Label(new GUIContent("Exports (filtered dependency rows)",
                        "TSV/CSV use whichever dependency rows match the filters and “Hide .cs” on this page."),
                    EditorStyles.miniBoldLabel);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(CopyFilteredTsvContent, GUILayout.Width(210)))
                    CopyFilteredDependenciesTsvToClipboard();
                if (GUILayout.Button(SaveFilteredCsvContent, GUILayout.Width(150)))
                    SaveFilteredDependenciesCsvDialog();
                GUILayout.EndHorizontal();

                GUIUtilities.HorizontalLine();

                EditorGUILayout.HelpBox(
                    "BuildLayout (optional): load the Addressables BuildLayout.txt. " +
                    "When loaded, each dependency and External GUID row is labeled with the bundle/group name from that file so you can verify cross-bundle references with inspected asset.",
                    MessageType.None);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Load BuildLayout.txt"),
                    GUILayout.Width(160)))
                    LoadBuildLayoutInteractive();
                if (_buildLayout != null && GUILayout.Button(new GUIContent("Clear BuildLayout",
                        "Discard the loaded layout"), GUILayout.Width(130)))
                    ClearBuildLayoutClicked();
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_buildLayoutPath))
                    GUILayout.Label(new GUIContent("Layout file: " + Path.GetFileName(_buildLayoutPath),
                        _buildLayoutPath), EditorStyles.miniLabel);
                else
                    GUILayout.Label("No BuildLayout loaded (optional bundle column).", EditorStyles.miniLabel);
            }

            var results = _lastResults;

            if (_selectedObjects == null ||
                _selectedObjectsFoldouts == null ||
                results.Count == 0 ||
                _selectedObjects.Length == 0)
            {
                EditorGUILayout.EndScrollView();
                GUILayout.EndVertical();
                return;
            }

            var singleSelection = _selectedObjects.Length == 1;

            for (var i = 0; i < _selectedObjectsFoldouts.Length; i++)
            {
                GUIUtilities.HorizontalLine();

                var refsData = results[_selectedObjects[i]];
                var hdr = $"{i + 1}. {Path.GetFileNameWithoutExtension(refsData.Path)} — Dependencies: {refsData.DependencyTreeRowCount} External GUIDs: {refsData.ExternalGuids.Count}";

                GUILayout.BeginHorizontal();
                if (singleSelection)
                {
                    var hue = GUI.color;
                    GUI.color = Color.green;
                    EditorGUILayout.LabelField(hdr, EditorStyles.boldLabel);
                    GUI.color = hue;
                    EditorGUILayout.ObjectField(_selectedObjects[i], typeof(Object), true);
                    GUILayout.EndHorizontal();
                }
                else
                {
                    var hue = GUI.color;
                    GUI.color = Color.green;
                    _selectedObjectsFoldouts[i] = EditorGUILayout.Foldout(_selectedObjectsFoldouts[i], hdr);
                    GUI.color = hue;
                    EditorGUILayout.ObjectField(_selectedObjects[i], typeof(Object), true);
                    GUILayout.EndHorizontal();

                    if (!_selectedObjectsFoldouts[i])
                        continue;
                }

                GUILayout.Space(4);

                EditorGUILayout.SelectableLabel($"Asset GUID: [{refsData.Guid}]",
                    GUILayout.Height(EditorGUIUtility.singleLineHeight * 1.2f));

                refsData.DependencyFoldout = EditorGUILayout.Foldout(refsData.DependencyFoldout,
                    DependenciesFoldoutContent);

                if (refsData.DependencyFoldout)
                    DrawDependencies(refsData);

                refsData.ExternalGuidsFoldout = EditorGUILayout.Foldout(refsData.ExternalGuidsFoldout,
                    ExternalGuidsFoldoutContent);

                if (refsData.ExternalGuidsFoldout)
                    DrawExternalGuids(refsData);
            }

            EditorGUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void EnsureDefaultPendingRows()
        {
            if (_pendingTargets.Count == 0)
                _pendingTargets.Add(new PendingTarget());
        }

        private static string TryNormalizeGuid32(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var s = Regex.Replace(raw.Trim(), @"[\s\-{}]", "");
            return s.Length == 32 && Guid32Hex.IsMatch(s) ? s.ToLowerInvariant() : null;
        }

        private bool HasPendingInput()
        {
            foreach (var t in _pendingTargets)
            {
                if (t.Asset != null)
                    return true;

                if (!string.IsNullOrWhiteSpace(t.GuidText))
                    return true;
            }

            return false;
        }

        private void SyncPendingFromObjects(IEnumerable<Object> objs)
        {
            _pendingTargets.Clear();
            if (objs != null)
            {
                foreach (var o in objs)
                {
                    if (o == null)
                        continue;

                    var path = AssetDatabase.GetAssetPath(o);
                    var g = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
                    _pendingTargets.Add(new PendingTarget { Asset = o, GuidText = g });
                }
            }

            EnsureDefaultPendingRows();
        }

        private Object[] CollectResolvedObjectsFromPending()
        {
            foreach (var p in _pendingTargets)
                p.GuidNotFound = false;

            var list = new List<Object>();
            var seen = new HashSet<string>();

            foreach (var t in _pendingTargets)
            {
                if (t.Asset != null)
                {
                    var path = AssetDatabase.GetAssetPath(t.Asset);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    var g = AssetDatabase.AssetPathToGUID(path);

                    if (string.IsNullOrEmpty(g) || !seen.Add(g))
                        continue;

                    var main = AssetDatabase.LoadMainAssetAtPath(path);

                    if (main != null)
                        list.Add(main);

                    continue;
                }

                if (string.IsNullOrWhiteSpace(t.GuidText))
                    continue;

                var gn = TryNormalizeGuid32(t.GuidText);

                if (gn == null)
                {
                    t.GuidNotFound = true;
                    continue;
                }

                var ap = AssetDatabase.GUIDToAssetPath(gn);

                if (string.IsNullOrEmpty(ap))
                {
                    t.GuidNotFound = true;
                    continue;
                }

                if (!seen.Add(gn))
                    continue;

                var asset = AssetDatabase.LoadMainAssetAtPath(ap);

                if (asset != null)
                    list.Add(asset);
            }

            return list.ToArray();
        }

        private void MergeDragDroppedObjectsIntoPending(Object[] dropped)
        {
            if (dropped == null || dropped.Length == 0)
                return;

            var seenGuids = new HashSet<string>();

            foreach (var p in _pendingTargets)
            {
                if (p.Asset == null)
                    continue;

                var ap = AssetDatabase.GetAssetPath(p.Asset);

                if (string.IsNullOrEmpty(ap))
                    continue;

                var g = AssetDatabase.AssetPathToGUID(ap);

                if (!string.IsNullOrEmpty(g))
                    seenGuids.Add(g);
            }

            foreach (var raw in dropped)
            {
                if (raw == null)
                    continue;

                var path = AssetDatabase.GetAssetPath(raw);

                if (string.IsNullOrEmpty(path))
                    continue;

                var gid = AssetDatabase.AssetPathToGUID(path);

                if (string.IsNullOrEmpty(gid) || !seenGuids.Add(gid))
                    continue;

                var main = AssetDatabase.LoadMainAssetAtPath(path);

                if (main == null)
                    continue;

                _pendingTargets.Add(new PendingTarget { Asset = main, GuidText = gid });
            }
        }

        private void DrawDragDropSurface()
        {
            var rect = GUILayoutUtility.GetRect(10f, 52f, GUILayout.ExpandWidth(true));

            var boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };

            GUI.Box(rect, new GUIContent("Drop project assets here", "Duplicates are skipped."), boxStyle);

            var e = Event.current;

            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;

            if (!rect.Contains(e.mousePosition))
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (e.type != EventType.DragPerform)
            {
                e.Use();
                return;
            }

            DragAndDrop.AcceptDrag();
            MergeDragDroppedObjectsIntoPending(DragAndDrop.objectReferences);
            e.Use();
            Repaint();
        }

        private void DrawIdlePickerPanel()
        {
            EditorGUILayout.HelpBox(
                "Select assets for analysis",
                MessageType.Info);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.enabled = HasPendingInput();
            var gb = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.45f, 0.82f, 0.52f);

            if (GUILayout.Button(RunAnalysisContent, GUILayout.MinWidth(220), GUILayout.Height(26)))
                RunAnalysisFromPending();

            GUI.backgroundColor = gb;
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            DrawDragDropSurface();

            GUILayout.Space(6);

            _idleScrollPos = EditorGUILayout.BeginScrollView(_idleScrollPos);
            EnsureDefaultPendingRows();

            for (var i = 0; i < _pendingTargets.Count;)
            {
                if (DrawPendingRow(i))
                {
                    _pendingTargets.RemoveAt(i);
                    EnsureDefaultPendingRows();
                    continue;
                }

                i++;
            }

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Add", GUILayout.Width(100)))
                _pendingTargets.Add(new PendingTarget());

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private static void SyncGuidTextFromAssetReference(PendingTarget t)
        {
            if (t.Asset == null)
                return;

            var path = AssetDatabase.GetAssetPath(t.Asset);
            if (string.IsNullOrEmpty(path))
                return;

            var gid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(gid))
                return;

            t.GuidText = gid;
            t.GuidNotFound = false;
        }

        private void ApplyCommittedGuidText(PendingTarget t)
        {
            var raw = (t.GuidText ?? "").Trim();

            if (string.IsNullOrEmpty(raw))
            {
                t.GuidNotFound = false;
                return;
            }

            var g = TryNormalizeGuid32(raw);
            if (g == null)
            {
                t.GuidNotFound = false;
                return;
            }

            t.GuidText = g;
            var path = AssetDatabase.GUIDToAssetPath(g);

            if (string.IsNullOrEmpty(path))
            {
                t.GuidNotFound = true;
                t.Asset = null;
                return;
            }

            t.GuidNotFound = false;
            var main = AssetDatabase.LoadMainAssetAtPath(path);

            if (main != null)
                t.Asset = main;
        }

        private bool DrawPendingRow(int index)
        {
            var target = _pendingTargets[index];
            var remove = false;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{index + 1}.", GUILayout.Width(56));

            EditorGUI.BeginChangeCheck();
            target.Asset = EditorGUILayout.ObjectField(GUIContent.none, target.Asset, typeof(Object), false);

            if (EditorGUI.EndChangeCheck())
                SyncGuidTextFromAssetReference(target);

            GUILayout.Label("GUID:", GUILayout.Width(40));
            var pb = GUI.backgroundColor;

            if (target.GuidNotFound)
                GUI.backgroundColor = new Color(1f, 0.65f, 0.65f);

            EditorGUI.BeginChangeCheck();
            target.GuidText = EditorGUILayout.DelayedTextField(GUIContent.none, target.GuidText);

            if (EditorGUI.EndChangeCheck())
                ApplyCommittedGuidText(target);

            GUI.backgroundColor = pb;

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_pendingTargets.Count <= 1))
            {
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                    remove = true;
            }

            GUILayout.EndHorizontal();

            if (target.GuidNotFound)
                EditorGUILayout.HelpBox("This GUID was not found in the project. The object reference was cleared.",
                    MessageType.Error);

            EditorGUILayout.EndVertical();

            return remove;
        }

        private void ResetAnalysis()
        {
            _waitingAfterProjectChange = false;
            _lastResults = null;
            _selectedObjects = null;
            _selectedObjectsFoldouts = null;
            _emptyAnalysis = false;
            _addressablesData = null;
            EnsureDefaultPendingRows();
        }

        private void RefreshAnalysisFromResults()
        {
            if (_selectedObjects is { Length: > 0 })
                RunAnalysisFromObjects(_selectedObjects);
        }

        private void RunAnalysisFromPending()
        {
            var resolved = CollectResolvedObjectsFromPending();

            if (resolved.Length == 0)
            {
                Repaint();
                return;
            }

            RunAnalysisFromObjects(resolved);
        }

        private void RunAnalysisFromObjects(Object[] cleaned)
        {
            _waitingAfterProjectChange = false;
            _addressablesData = new AddressablesData();

            if (cleaned == null || cleaned.Length == 0)
            {
                _emptyAnalysis = true;
                _selectedObjects = Array.Empty<Object>();
                _lastResults = null;
                _selectedObjectsFoldouts = null;
                EditorUtility.UnloadUnusedAssetsImmediate();
                Repaint();

                return;
            }

            _emptyAnalysis = false;
            _selectedObjects = cleaned;
            _lastResults = GetGuids(cleaned);
            SyncPendingFromObjects(_selectedObjects);

            var depsTotal = _lastResults.Values.Sum(r => r.DependencyTreeRowCount);
            var depsFoldoutOpen = cleaned.Length <= 5 && depsTotal < 100;

            foreach (var d in _lastResults.Values)
                d.DependencyFoldout = depsFoldoutOpen;

            ResizeFoldouts();
            TryReloadBuildLayoutFromPrefs();
            UpdateAssetsBundlesRelations();
            EditorUtility.UnloadUnusedAssetsImmediate();
            Repaint();
        }

        private void OnProjectChange()
        {
            MarkStaleAfterAssetDatabaseChange();
        }

        private string GetBundleNameByAssetPath(string assetPath)
        {
            return _buildLayout == null ? string.Empty : _buildLayout.GetBundleNameByAssetPath(assetPath);
        }

        private void UpdateAssetsBundlesRelations()
        {
            if (_buildLayout == null || _lastResults == null)
                return;

            foreach (var pair in _lastResults)
            {
                pair.Value.AddressableGroup = GetBundleNameByAssetPath(pair.Value.Path);

                foreach (var dependency in pair.Value.Dependencies)
                {
                    var bundleName = GetBundleNameByAssetPath(dependency.Path);
                    dependency.AddressablesGroup = bundleName;
                    dependency.IsInOtherAddressablesBundle =
                        dependency.AddressablesGroup != pair.Value.AddressableGroup;
                }

                foreach (var guid in pair.Value.ExternalGuids)
                {
                    var bundleName = GetBundleNameByAssetPath(guid.Path);
                    guid.AddressablesGroup = bundleName;
                    guid.IsBuildLayoutUsed = true;
                    guid.HasWarning = guid.AddressablesGroup != pair.Value.AddressableGroup;
                }

                pair.Value.SortDependencies();
            }
        }

        private void RevertBuildLayoutOverlay()
        {
            if (_lastResults == null || _addressablesData == null)
                return;

            foreach (var pair in _lastResults)
            {
                var main = GetExplicitAddressableGroup(pair.Value.Path);
                pair.Value.AddressableGroup = main;

                foreach (var dependency in pair.Value.Dependencies)
                {
                    dependency.AddressablesGroup = GetExplicitAddressableGroup(dependency.Path);
                    dependency.IsInOtherAddressablesBundle =
                        !string.IsNullOrEmpty(dependency.AddressablesGroup) &&
                        dependency.AddressablesGroup != main;
                }

                foreach (var guid in pair.Value.ExternalGuids)
                {
                    guid.AddressablesGroup = GetExplicitAddressableGroup(guid.Path);
                    guid.IsBuildLayoutUsed = false;
                    guid.HasWarning = !string.IsNullOrEmpty(guid.AddressablesGroup) &&
                                      guid.AddressablesGroup != pair.Value.AddressableGroup;
                }

                pair.Value.SortDependencies();
            }
        }

        [MenuItem("Assets/Inspect Asset and Dependencies", true)]
        private static bool ValidateInspectMenu()
        {
            return true;
        }

        [MenuItem("Assets/Inspect Asset and Dependencies", false, 20)]
        public static void FindReferences()
        {
            GetWindow<AssetInspectorWindow>("Asset Inspector").OpenFromAssetsMenu();
        }

        [MenuItem("Tools/Inspect Assets and GUIDs", false, 105)]
        public static void OpenInspectFromToolsMenu()
        {
            GetWindow<AssetInspectorWindow>("Asset Inspector").OpenIdle();
        }

        private void OpenIdle()
        {
            Show();
            EnsureDefaultPendingRows();
        }

        private void OpenFromAssetsMenu()
        {
            Show();

            var sel = Selection.objects;

            if (sel is { Length: > 0 })
            {
                SyncPendingFromObjects(sel.Where(o => o != null).ToArray());
                RunAnalysisFromPending();
            }
            else
            {
                EnsureDefaultPendingRows();
            }
        }

        private static bool RowMatchesAssetFilter(string q, string fileName, string fullPath)
        {
            var query = q?.Trim() ?? "";

            if (string.IsNullOrEmpty(query))

                return true;

            return fileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fullPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Color GetNeutralButtonTextColor()
        {
            var c = GUI.skin.button.normal.textColor;

            return c.a <= 0.01f ? new Color(.85f, .85f, .85f) : c;
        }

        private static void BuildDependencyBadgeTextAndColor(DependencyRegistry d,
            out Color accentColor,
            out string badgeSuffix)
        {
            badgeSuffix = "";

            if (!string.IsNullOrEmpty(d.AddressablesGroup))
                badgeSuffix += " [Bundle:" + d.AddressablesGroup + ']';

            var agr = d.AddressablesGroup ?? "";

            if (agr.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0)
                badgeSuffix += " [REMOTE]";

            else if (agr.IndexOf("built-in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     agr.IndexOf("builtin", StringComparison.OrdinalIgnoreCase) >= 0)
                badgeSuffix += " [BUILT-IN]";

            if (d.IsInOtherAddressablesBundle)
                badgeSuffix += " [CrossBundle]";

            if (d.IsBuiltinExtra)
                badgeSuffix += " [UnityBuiltIn]";

            if (d.IsInEditor)
                badgeSuffix += " [Editor]";

            if (d.IsInResources)
                badgeSuffix += " [Resources]";

            if (d.IsInPackage)
                badgeSuffix += " [Package]";

            if (!d.IsInAssetDatabase)
                badgeSuffix += " [NotInDatabase]";

            if (!d.IsInAssetDatabase)
                accentColor = Color.red;
            else if (d.IsInOtherAddressablesBundle)
                accentColor = new Color(1f, .82f, .2f);
            else
                accentColor = GetNeutralButtonTextColor();
        }

        private static Color GetExternalGuidRowColor(ExternalGuidRegistry row)
        {
            return row.HasWarning ? new Color(1f, .82f, .2f) : GetNeutralButtonTextColor();
        }

        private static void SelectOrAdditive(Object assetObject)
        {
            if (assetObject == null)
                return;

            var evt = Event.current;

            var additive = evt != null && (evt.control || evt.command);

            if (!additive)
            {
                Selection.activeObject = assetObject;
                return;
            }

            var list = new List<Object>(Selection.objects ?? Array.Empty<Object>());
            {
                foreach (var o in list)
                    if (o == assetObject)
                        return;
            }

            list.Add(assetObject);

            Selection.objects = list.ToArray();
        }

        private static string TruncateMiddle(string path, int maxChars)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxChars)
                return path;

            const string ellipsis = "...";
            var usable = Mathf.Max(8, maxChars - ellipsis.Length);
            var half = usable / 2;
            var takeEnd = usable - half;
            return path.Substring(0, half) + ellipsis + path.Substring(path.Length - takeEnd);
        }

        private void ResizeFoldouts()
        {
            var n = _selectedObjects.Length;
            var prev = _selectedObjectsFoldouts ?? Array.Empty<bool>();
            var next = new bool[n];

            for (var i = 0; i < n; i++)
                if (i < prev.Length)
                    next[i] = prev[i];

            if (n == 1 && prev.Length != 1)
                next[0] = true;

            _selectedObjectsFoldouts = next;
        }

        private void TryReloadBuildLayoutFromPrefs()
        {
            if (_lastResults == null || _lastResults.Count == 0)
                return;

            var path = EditorPrefs.GetString(PrefLastBuildLayoutPath, "");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            if (_buildLayout != null && path == _buildLayoutPath)
                return;

            TryLoadBuildLayoutAtPathSilent(path);
        }

        private void ClearBuildLayoutClicked()
        {
            _buildLayout = null;
            _buildLayoutPath = null;

            EditorPrefs.DeleteKey(PrefLastBuildLayoutPath);

            RevertBuildLayoutOverlay();
        }

        private void TryLoadBuildLayoutAtPathSilent(string fullPath)
        {
            try
            {
                _buildLayout = LiteBuildLayoutProvider.Load(fullPath);
                _buildLayoutPath = fullPath;

                var folder = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrEmpty(folder))
                    EditorPrefs.SetString(PrefLastBuildLayoutFolder, folder);

                EditorPrefs.SetString(PrefLastBuildLayoutPath, fullPath);
                UpdateAssetsBundlesRelations();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AssetInspector: Could not load BuildLayout '{fullPath}': {e.Message}");
            }
        }

        private static string DefaultBuildLayoutBrowseFolder()
        {
            var saved = EditorPrefs.GetString(PrefLastBuildLayoutFolder, "");

            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                return saved;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return projectRoot ?? ".";
        }

        private void LoadBuildLayoutInteractive()
        {
            var path = EditorUtility.OpenFilePanelWithFilters("Open BuildLayout.txt",
                DefaultBuildLayoutBrowseFolder(),
                new[] { "Text Files (*.txt)", "txt" });

            if (!string.IsNullOrEmpty(path))
                TryLoadBuildLayoutAtPathSilent(path);
        }

        private bool PassesDependencyFilters(DependencyRegistry dependency)
        {
            if (_excludeCodeAssets && dependency.Extension == ".cs")
                return false;

            var fileName = Path.GetFileName(dependency.Path ?? "");
            return RowMatchesAssetFilter(_dependencyFilter, fileName, dependency.Path ?? "");
        }

        private bool PassesExternalFilters(ExternalGuidRegistry row)
        {
            var fileName = Path.GetFileName(row.Path ?? "");
            return RowMatchesAssetFilter(_externalGuidFilter, fileName, row.Path ?? "");
        }

        private static string EscapeCsvField(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var mustQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            value = value.Replace("\"", "\"\"");
            return mustQuote ? $"\"{value}\"" : value;
        }

        private string BadgeTextForCsv(DependencyRegistry d)
        {
            BuildDependencyBadgeTextAndColor(d, out _, out var suffix);
            return suffix.TrimStart();
        }

        private static string TsvCell(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            
            raw = raw.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");

            return raw;
        }

        private void CopyFilteredDependenciesTsvToClipboard()
        {
            if (_lastResults == null)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("MainAssetPath\tMainGuid\tDependencyPath\tDependencyGuid\tBundle\tBadgeText");

            foreach (var pair in _lastResults)
            {
                foreach (var d in EnumerateDependenciesTreeOrderDfs(pair.Value.DependencyRoots))
                {
                    if (!PassesDependencyFilters(d))
                        continue;

                    sb.Append(TsvCell(pair.Value.Path)).Append('\t');
                    sb.Append(pair.Value.Guid).Append('\t');
                    sb.Append(TsvCell(d.Path)).Append('\t');
                    sb.Append(d.Id).Append('\t');
                    sb.Append(EscapeCsvField(d.AddressablesGroup ?? "")).Append('\t');
                    sb.AppendLine(TsvCell(BadgeTextForCsv(d)));
                }
            }

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void SaveFilteredDependenciesCsvDialog()
        {
            var dlg = EditorUtility.SaveFilePanel("Save filtered dependency rows",
                Directory.GetCurrentDirectory(), "asset-inspector-deps.csv", "csv");

            if (string.IsNullOrEmpty(dlg))
                return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("MainAssetPath,MainGuid,DependencyPath,DependencyGuid,Bundle,BadgeText");

                if (_lastResults != null)

                    foreach (var pair in _lastResults)
                    {
                        foreach (var d in EnumerateDependenciesTreeOrderDfs(pair.Value.DependencyRoots))
                        {
                            if (!PassesDependencyFilters(d))
                                continue;

                            sb.Append(EscapeCsvField(pair.Value.Path)).Append(',');
                            sb.Append(EscapeCsvField(pair.Value.Guid)).Append(',');
                            sb.Append(EscapeCsvField(d.Path)).Append(',');
                            sb.Append(EscapeCsvField(d.Id)).Append(',');
                            sb.Append(EscapeCsvField(d.AddressablesGroup ?? "")).Append(',');
                            sb.AppendLine(EscapeCsvField(BadgeTextForCsv(d)));
                        }
                    }

                File.WriteAllText(dlg, sb.ToString(), Encoding.UTF8);

                EditorUtility.DisplayDialog("Asset Inspector", "Saved CSV.", "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Asset Inspector", "CSV failed: " + ex.Message, "OK");
            }
        }

        private void MarkStaleAfterAssetDatabaseChange()
        {
            if (_selectedObjects is { Length: > 0 })
                SyncPendingFromObjects(_selectedObjects);

            _addressablesData = null;
            _selectedObjects = null;
            _selectedObjectsFoldouts = null;
            _lastResults = null;

            EnsureDefaultPendingRows();

            _waitingAfterProjectChange = CollectResolvedObjectsFromPending().Length > 0;

            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        private void ShutdownWindowPersistingLayoutPrefs()
        {
            _addressablesData = null;
            _selectedObjects = null;
            _selectedObjectsFoldouts = null;
            _lastResults = null;
            _buildLayout = null;
            _buildLayoutPath = null;
            _pendingTargets.Clear();
            EnsureDefaultPendingRows();

            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        private void DrawDependencies(AssetReferencesData refsData)
        {
            foreach (var root in refsData.DependencyRoots)
                DrawDependencyNode(root, 0);
        }

        private void DrawDependencyNode(DependencyTreeNode node, int depth)
        {
            var pass = PassesDependencyFilters(node.Registry);
            var hasChildren = node.Children.Count > 0;

            if (hasChildren)
            {
                if (!pass)
                {
                    foreach (var c in node.Children)
                        DrawDependencyNode(c, depth);

                    return;
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(depth * 16f);
                var foldRect = GUILayoutUtility.GetRect(18f, EditorGUIUtility.singleLineHeight + 4f,
                    GUILayout.Width(18f));
                node.FoldoutExpanded = EditorGUI.Foldout(foldRect, node.FoldoutExpanded, "", true);

                DrawDependencyRowControls(node.Registry);
                EditorGUILayout.EndHorizontal();

                if (node.FoldoutExpanded)
                {
                    foreach (var c in node.Children)
                        DrawDependencyNode(c, depth + 1);
                }

                return;
            }

            if (!pass)
                return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(depth * 16f);
            GUILayout.Space(18f);
            DrawDependencyRowControls(node.Registry);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDependencyRowControls(DependencyRegistry dependency)
        {
            var loaded = AssetDatabase.LoadMainAssetAtPath(dependency.Path);

            BuildDependencyBadgeTextAndColor(dependency, out var accent, out var badgeSuffix);

            var content = EditorGUIUtility.ObjectContent(loaded, dependency.Type);
            content.text = Path.GetFileName(dependency.Path ?? "") + badgeSuffix;
            content.tooltip = dependency.Path ?? "";

            GUI.color = accent;

            var align = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;

            var rowRect = GUILayoutUtility.GetRect(content, GUI.skin.button,
                GUILayout.Width(500),
                GUILayout.Height(EditorGUIUtility.singleLineHeight + 4f));

            if (GUI.Button(rowRect, content))
                SelectOrAdditive(loaded);

            GUI.skin.button.alignment = align;

            GUI.color = Color.white;

            if (GUILayout.Button(new GUIContent("GUID", "Copy GUID"), GUILayout.Width(42)))
                EditorGUIUtility.systemCopyBuffer = dependency.Id;

            if (GUILayout.Button(new GUIContent("Path", "Copy path"), GUILayout.Width(42)))
                EditorGUIUtility.systemCopyBuffer = dependency.Path;

            GUILayout.Label(new GUIContent(TruncateMiddle(dependency.Path ?? "", 56), dependency.Path ?? ""),
                GUILayout.MinWidth(80));
        }

        private static void DrawExternalGuidThinSeparator()
        {
            EditorGUILayout.Space(3f);
            var r = EditorGUILayout.GetControlRect(false, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.45f, 0.45f, 0.45f, 0.72f));
            EditorGUILayout.Space(3f);
        }

        private static void DrawExternalGuidGrayPrefixedLine(string rawLine, int oneBasedLineNumber)
        {
            if (string.IsNullOrEmpty(rawLine))
                return;

            var style = EditorStyles.miniLabel;
            var prevContent = GUI.contentColor;

            GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);

            var display = $"[{oneBasedLineNumber}] > " + rawLine;
            GUILayout.Label(new GUIContent(display, rawLine), style);

            GUI.contentColor = prevContent;
        }

        private static void DrawExternalGuidWhiteOccurrenceLine(string rawLine, int oneBasedLineNumber)
        {
            rawLine ??= string.Empty;

            var prevContent = GUI.contentColor;

            GUI.contentColor = Color.white;

            var display = $"[{oneBasedLineNumber}] " + rawLine;
            GUILayout.Label(new GUIContent(display, rawLine), EditorStyles.miniLabel);

            GUI.contentColor = prevContent;
        }

        private void DrawExternalGuids(AssetReferencesData refsData)
        {
            var isFirstPrinted = true;

            foreach (var row in refsData.ExternalGuids)
            {
                if (!PassesExternalFilters(row))
                    continue;

                if (!isFirstPrinted)
                    DrawExternalGuidThinSeparator();

                isFirstPrinted = false;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                var i0 = row.Line;

                if (!string.IsNullOrEmpty(row.SourceLineMinus2))
                    DrawExternalGuidGrayPrefixedLine(row.SourceLineMinus2, i0 - 1);

                if (!string.IsNullOrEmpty(row.SourceLineMinus1))
                    DrawExternalGuidGrayPrefixedLine(row.SourceLineMinus1, i0);

                DrawExternalGuidWhiteOccurrenceLine(row.SourceOccurrenceLine, i0 + 1);

                if (!string.IsNullOrEmpty(row.SourceLinePlus1))
                    DrawExternalGuidGrayPrefixedLine(row.SourceLinePlus1, i0 + 2);

                if (!string.IsNullOrEmpty(row.SourceLinePlus2))
                    DrawExternalGuidGrayPrefixedLine(row.SourceLinePlus2, i0 + 3);

                EditorGUILayout.BeginHorizontal();

                var loaded = AssetDatabase.LoadMainAssetAtPath(row.Path);
                var grp = row.AddressablesGroup;
                var grpPart = "";

                if (!string.IsNullOrEmpty(grp))
                    grpPart = row.IsBuildLayoutUsed ? $" [{grp}]" : $" [explicit:{grp}]";

                var content = EditorGUIUtility.ObjectContent(loaded, row.Type);
                content.text = $"[{row.Line}] {Path.GetFileName(row.Path)}{grpPart}";
                content.tooltip = $"{refsData.Path}:{row.Line}\nReferenced: {row.Path}";

                GUI.color = GetExternalGuidRowColor(row);

                var align = GUI.skin.button.alignment;

                GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                var rect = GUILayoutUtility.GetRect(content, GUI.skin.button,
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(EditorGUIUtility.singleLineHeight + 4f));

                if (GUI.Button(rect, content))
                    SelectOrAdditive(loaded);

                GUI.skin.button.alignment = align;
                GUI.color = Color.white;

                if (GUILayout.Button(new GUIContent("GUID", "Copy GUID: " + row.Id), GUILayout.Width(42)))
                    EditorGUIUtility.systemCopyBuffer = row.Id;

                if (GUILayout.Button(new GUIContent("Path", "Copy path"), GUILayout.Width(42)))
                    EditorGUIUtility.systemCopyBuffer = row.Path;

                GUILayout.Label(new GUIContent(TruncateMiddle(row.Path ?? "", 48), row.Path ?? ""),
                    GUILayout.MinWidth(80));

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
            }
        }

        private Dictionary<Object, AssetReferencesData> GetGuids(Object[] selectedObjects)
        {
            if (selectedObjects == null || selectedObjects.Length == 0)
                return new Dictionary<Object, AssetReferencesData>();

            GetGuidsInternal(selectedObjects, out var result);

            return result;
        }

        private void GetGuidsInternal(Object[] selectedObjects,
            out Dictionary<Object, AssetReferencesData> results)
        {
            results = new Dictionary<Object, AssetReferencesData>();

            var n = selectedObjects.Length;

            try
            {
                for (var i = 0; i < n; i++)
                {
                    var selectedObject = selectedObjects[i];
                    if (selectedObject == null)
                        continue;

                    EditorUtility.DisplayProgressBar("Asset Inspector",
                        $"Analyzing asset {i + 1}/{n}",
                        (i + 0.5f) / Mathf.Max(1, n));

                    var selectedObjectPath = AssetDatabase.GetAssetPath(selectedObject);
                    results.Add(selectedObject, GetDataForAsset(selectedObjectPath));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private AssetReferencesData GetDataForAsset(string assetPath)
        {
            string[] TryReadAsLines(string path)
            {
                if (Directory.Exists(path))
                    return Array.Empty<string>();

                try
                {
                    return File.ReadAllLines(path);
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    return Array.Empty<string>();
                }
            }

            bool IsValidType(string path, Type type)
            {
                if (type != null)
                {
                    if (type == typeof(DefaultAsset)) // DefaultAsset goes for e.g. folders.
                        return false;

                    return true;
                }

                Debug.LogWarning($"Invalid asset type found at {path}");
                return false;
            }

            bool CanAnalyzeType(Type type)
            {
                return type == typeof(GameObject) || type == typeof(SceneAsset)
                                                  || DerivesFromOrEqual(type, typeof(ScriptableObject));
            }

            static bool DerivesFromOrEqual(Type a, Type b)
            {
#if UNITY_WSA && ENABLE_DOTNET && !UNITY_EDITOR
                return b == a || b.GetTypeInfo().IsAssignableFrom(a.GetTypeInfo());
#else
                return b == a || b.IsAssignableFrom(a);
#endif
            }

            var regexFileAndGuid = new Regex(@"fileID: \d+, guid: [a-f0-9]" + "{" + "32" + "}");

            var mainAddressablesGroup = GetExplicitAddressableGroup(assetPath);

            var guid = AssetDatabase.GUIDFromAssetPath(assetPath);

            var guidStr = guid.ToString();

            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var validAssetType = IsValidType(assetPath, type);

            var refsData = new AssetReferencesData(assetPath, guidStr, mainAddressablesGroup);

            if (!validAssetType)
                return refsData;

            if (!CanAnalyzeType(type))
                return refsData;

            var lines = TryReadAsLines(assetPath);

            if (lines.Length > 0)
                for (var index = 0; index < lines.Length; index++)
                {
                    var lineOriginal = lines[index];
                    var line = lineOriginal;

                    var systemLine = false;

                    foreach (var keyword in KeyWordsToIgnore)
                        if (line.Contains(keyword))
                        {
                            systemLine = true;
                            break;
                        }

                    if (systemLine) continue;

                    if (type == typeof(SceneAsset))
                        foreach (var keyword in KeyWordsToIgnoreInSceneAsset)
                            if (line.Contains(keyword))
                            {
                                systemLine = true;
                                break;
                            }

                    if (systemLine) continue;

                    if (line.Contains("guid:"))
                    {
                        var guidMatches = regexFileAndGuid.Matches(line);

                        for (var i = 0; i < guidMatches.Count; i++)
                        {
                            var match = guidMatches[i];
                            var str = match.Value;

                            var externalGuid = str.Substring(str.Length - 32);

                            if (!externalGuid.StartsWith("0000000000"))
                            {
                                var guidAssetPath = AssetDatabase.GUIDToAssetPath(externalGuid);
                                var guidAssetType = AssetDatabase.GetMainAssetTypeAtPath(guidAssetPath);
                                var guidAddressablesGroup = GetExplicitAddressableGroup(guidAssetPath);

                                refsData.ExternalGuids.Add(new ExternalGuidRegistry(externalGuid, index,
                                    guidAssetPath, guidAssetType, guidAddressablesGroup, mainAddressablesGroup,
                                    index >= 2 ? lines[index - 2] : null,
                                    index >= 1 ? lines[index - 1] : null,
                                    lines[index],
                                    index + 1 < lines.Length ? lines[index + 1] : null,
                                    index + 2 < lines.Length ? lines[index + 2] : null));
                            }
                        }
                    }
                }

            var (roots, rowCount, maxDepth) = BuildDependencyTreeRoots(assetPath, mainAddressablesGroup);
            refsData.DependencyRoots = roots;
            refsData.DependencyTreeRowCount = rowCount;

            refsData.Dependencies.Clear();
            FillUniqueDependenciesDepthFirst(roots, refsData.Dependencies);
            refsData.SortDependencies();

            var collapseNested = rowCount > 30 || maxDepth > 3;
            SetNestedFoldoutsExpanded(roots, !collapseNested);

            return refsData;
        }

        private static void FillUniqueDependenciesDepthFirst(List<DependencyTreeNode> roots,
            ICollection<DependencyRegistry> target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Walk(DependencyTreeNode node)
            {
                if (seen.Add(node.Registry.Path))
                    target.Add(node.Registry);

                foreach (var child in node.Children)
                    Walk(child);
            }

            foreach (var r in roots)
                Walk(r);
        }

        private static IEnumerable<DependencyRegistry> EnumerateDependenciesTreeOrderDfs(
            IEnumerable<DependencyTreeNode> roots)
        {
            foreach (var root in roots)
                foreach (var reg in EnumerateNodeDfs(root))
                    yield return reg;
        }

        private static IEnumerable<DependencyRegistry> EnumerateNodeDfs(DependencyTreeNode node)
        {
            yield return node.Registry;

            foreach (var child in node.Children)
                foreach (var reg in EnumerateNodeDfs(child))
                    yield return reg;
        }

        private static void SetNestedFoldoutsExpanded(IEnumerable<DependencyTreeNode> roots, bool expanded)
        {
            foreach (var r in roots)
                SetNestedFoldoutsExpanded(r, expanded);
        }

        private static void SetNestedFoldoutsExpanded(DependencyTreeNode node, bool expanded)
        {
            if (node.Children.Count > 0)
                node.FoldoutExpanded = expanded;

            foreach (var c in node.Children)
                SetNestedFoldoutsExpanded(c, expanded);
        }

        private (List<DependencyTreeNode> roots, int rowCount, int maxDepth) BuildDependencyTreeRoots(
            string assetPath, string mainAddressablesGroup)
        {
            var roots = new List<DependencyTreeNode>();
            var rowCount = 0;
            var maxDepth = 0;

            var directPaths = AssetDatabase.GetDependencies(assetPath, false)
                .Where(p => p != assetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directPaths.Count == 0)
                return (roots, 0, 0);

            var siblingRegsByPath =
                directPaths.ToDictionary(p => p, p => CreateDependencyRegistry(p, mainAddressablesGroup));

            directPaths.Sort((a, b) => CompareDependencyRegistries(siblingRegsByPath[a], siblingRegsByPath[b]));

            var ancestorChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in directPaths)
            {
                var node = BuildDependencySubtreeRecursive(siblingRegsByPath[p], mainAddressablesGroup, ancestorChain, 1,
                    ref rowCount,
                    ref maxDepth);

                roots.Add(node);
            }

            return (roots, rowCount, maxDepth);
        }

        private DependencyTreeNode BuildDependencySubtreeRecursive(
            DependencyRegistry registry,
            string mainAddressablesGroup,
            HashSet<string> ancestorChain,
            int depthFromMain,
            ref int rowCount,
            ref int maxDepth)
        {
            var nodePath = registry.Path ?? "";

            rowCount++;
            maxDepth = Math.Max(maxDepth, depthFromMain);

            var node = new DependencyTreeNode(registry);

            ancestorChain.Add(nodePath);

            try
            {
                var childPaths = AssetDatabase.GetDependencies(nodePath, false)
                    .Where(p => p != nodePath && !ancestorChain.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (childPaths.Count == 0)
                    return node;

                var childRegsByPath =
                    childPaths.ToDictionary(p => p, p => CreateDependencyRegistry(p, mainAddressablesGroup));

                childPaths.Sort((a, b) => CompareDependencyRegistries(childRegsByPath[a], childRegsByPath[b]));

                foreach (var cp in childPaths)
                {
                    var child = BuildDependencySubtreeRecursive(childRegsByPath[cp], mainAddressablesGroup,
                        ancestorChain,
                        depthFromMain + 1, ref rowCount, ref maxDepth);

                    node.Children.Add(child);
                }
            }
            finally
            {
                ancestorChain.Remove(nodePath);
            }

            return node;
        }

        private DependencyRegistry CreateDependencyRegistry(string dependencyAssetPath, string mainAddressablesGroup)
        {
            var dependencyGuid = AssetDatabase.GUIDFromAssetPath(dependencyAssetPath);
            var dependencyGuidStr = dependencyGuid.ToString();
            var dependencyAssetType = AssetDatabase.GetMainAssetTypeAtPath(dependencyAssetPath);

            var dependencyAddressablesGroup = GetExplicitAddressableGroup(dependencyAssetPath);

            var isInOtherAddressablesBundle = !string.IsNullOrEmpty(dependencyAddressablesGroup)
                                              && mainAddressablesGroup != dependencyAddressablesGroup;

            var registry = new DependencyRegistry(dependencyGuidStr,
                dependencyAssetPath, dependencyAssetType, dependencyAddressablesGroup,
                isInOtherAddressablesBundle);

            var pathToSearch = dependencyAssetPath.Replace("\\", "/");

            registry.IsInPackage = pathToSearch.StartsWith("Packages/");
            registry.IsInResources = pathToSearch.Contains("/Resources/");
            registry.IsInEditor = pathToSearch.Contains("/Editor/")
                                  || pathToSearch.Contains("/Editor Default Resources/")
                                  || pathToSearch.Contains("/Editor Resources/");
            registry.IsBuiltinExtra = pathToSearch.Contains("unity_builtin");
            registry.IsInAssetDatabase = !dependencyGuid.Empty();

            return registry;
        }

        private static int CompareDependencyRegistries(DependencyRegistry a, DependencyRegistry b)
        {
            var c = b.IsInOtherAddressablesBundle.CompareTo(a.IsInOtherAddressablesBundle);
            if (c != 0) return c;

            c = b.IsInResources.CompareTo(a.IsInResources);
            if (c != 0) return c;

            c = b.IsBuiltinExtra.CompareTo(a.IsBuiltinExtra);
            if (c != 0) return c;

            c = b.IsInEditor.CompareTo(a.IsInEditor);
            if (c != 0) return c;

            c = b.IsInPackage.CompareTo(a.IsInPackage);
            if (c != 0) return c;

            c = b.IsInAssetDatabase.CompareTo(a.IsInAssetDatabase);
            if (c != 0) return c;

            return string.CompareOrdinal(b.Extension, a.Extension);
        }

        private string GetExplicitAddressableGroup(string assetPath)
        {
            if (_addressablesData == null)
                return string.Empty;

            if (_addressablesData.ReversedAssetsMap.TryGetValue(assetPath, out var groupName))
                return groupName ?? string.Empty;

            foreach (var directory in _addressablesData.ReversedFoldersAssetsMap.Keys)
                if (assetPath.StartsWith(directory))
                    return _addressablesData.ReversedFoldersAssetsMap[directory];

            return string.Empty;
        }

        private class AddressablesData
        {
            private static bool _reflectionWarningLogged;

            public AddressablesData()
            {
                try
                {
                    PopulateFromAddressablesReflection();
                }
                catch (Exception e)
                {
                    LogReflectionWarningOnce("loading Addressables group map", e);
                }
            }

            public Dictionary<string, string> ReversedAssetsMap { get; } = new();

            public Dictionary<string, string> ReversedFoldersAssetsMap { get; } = new();

            private static void LogReflectionWarningOnce(string context, Exception exception)
            {
                if (_reflectionWarningLogged)
                    return;

                _reflectionWarningLogged = true;
                Debug.LogWarning(
                    $"AssetInspector: Failed to detect Addressables via reflection while {context}: {exception}");
            }

            private void PopulateFromAddressablesReflection()
            {
                Type defaultObjectType = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    defaultObjectType = assembly.GetType(
                        "UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject",
                        false);

                    if (defaultObjectType != null)
                        break;
                }

                if (defaultObjectType == null)
                    return;

                var settingsProperty = defaultObjectType.GetProperty("Settings",
                    BindingFlags.Public | BindingFlags.Static);
                var settings = settingsProperty?.GetValue(null, null);
                if (settings == null)
                    return;

                var settingsType = settings.GetType();
                var groupsProperty = settingsType.GetProperty("groups",
                    BindingFlags.Public | BindingFlags.Instance);
                var groups = groupsProperty?.GetValue(settings) as IEnumerable;
                if (groups == null)
                    return;

                var duplicatePaths = new HashSet<string>();

                foreach (var groupObj in groups)
                {
                    if (groupObj == null)
                        continue;

                    var groupType = groupObj.GetType();
                    var groupName = groupType.GetProperty("Name",
                        BindingFlags.Public | BindingFlags.Instance)?.GetValue(groupObj) as string ?? string.Empty;

                    var entriesProperty = groupType.GetProperty("entries",
                        BindingFlags.Public | BindingFlags.Instance);
                    var entries = entriesProperty?.GetValue(groupObj) as IEnumerable;
                    if (entries == null)
                        continue;

                    foreach (var entryObj in entries)
                    {
                        if (entryObj == null)
                            continue;

                        var entryType = entryObj.GetType();
                        var path = entryType.GetProperty("AssetPath",
                            BindingFlags.Public | BindingFlags.Instance)?.GetValue(entryObj) as string;

                        if (string.IsNullOrEmpty(path))
                            continue;

                        var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);

                        if (isDirectory)
                        {
                            if (!ReversedFoldersAssetsMap.TryGetValue(path, out var prevFolderGroup))
                                ReversedFoldersAssetsMap[path] = groupName;
                            else if (!string.Equals(prevFolderGroup, groupName, StringComparison.Ordinal))
                                duplicatePaths.Add(path);
                        }
                        else
                        {
                            if (!ReversedAssetsMap.TryGetValue(path, out var prevAssetGroup))
                                ReversedAssetsMap[path] = groupName;
                            else if (!string.Equals(prevAssetGroup, groupName, StringComparison.Ordinal))
                                duplicatePaths.Add(path);
                        }
                    }
                }

                if (duplicatePaths.Count > 0)
                    Debug.LogWarning($"AssetInspector: Addressables map has {duplicatePaths.Count} path(s) " +
                                     "referenced from multiple groups (check Addressables Groups). Sample: " +
                                     string.Join(", ", duplicatePaths.Take(5)));
            }
        }

        private class DependencyRegistry
        {
            public DependencyRegistry(string id, string path, Type type,
                string addressablesGroup, bool isInOtherAddressablesBundle)
            {
                Id = id;
                Path = path;
                Extension = System.IO.Path.GetExtension(Path);
                Type = type;
                AddressablesGroup = addressablesGroup;
                IsInOtherAddressablesBundle = isInOtherAddressablesBundle;
            }

            // ReSharper disable once UnusedAutoPropertyAccessor.Global
            public string Id { get; }
            public string Path { get; }
            public string Extension { get; }
            public Type Type { get; }

            public string AddressablesGroup { get; set; }

            public bool IsInOtherAddressablesBundle { get; set; }

            public bool IsInResources { get; set; }
            public bool IsInEditor { get; set; }
            public bool IsBuiltinExtra { get; set; }
            public bool IsInAssetDatabase { get; set; }
            public bool IsInPackage { get; set; }
        }

        private class ExternalGuidRegistry
        {
            public ExternalGuidRegistry(string id, int line, string path, Type type, string addressablesGroup,
                string mainAssetAddressableGroup,
                string sourceLineMinus2, string sourceLineMinus1,
                string sourceOccurrenceLine,
                string sourceLinePlus1, string sourceLinePlus2)
            {
                Id = id;
                Line = line;

                Path = path;
                Type = type;
                AddressablesGroup = addressablesGroup;

                SourceLineMinus2 = sourceLineMinus2;
                SourceLineMinus1 = sourceLineMinus1;
                SourceOccurrenceLine = sourceOccurrenceLine;
                SourceLinePlus1 = sourceLinePlus1;
                SourceLinePlus2 = sourceLinePlus2;

                HasWarning = !string.IsNullOrEmpty(addressablesGroup)
                             && mainAssetAddressableGroup != addressablesGroup;
            }

            // ReSharper disable once UnusedAutoPropertyAccessor.Global
            public string Id { get; }
            public int Line { get; }

            public string Path { get; }
            public Type Type { get; }

            public string AddressablesGroup { get; set; }
            public bool IsBuildLayoutUsed { get; set; }

            /// <summary>Two lines above the GUID occurrence line in the analyzed asset.</summary>
            public string SourceLineMinus2 { get; }

            public string SourceLineMinus1 { get; }

            /// <summary>The analyzed asset line containing the GUID reference.</summary>
            public string SourceOccurrenceLine { get; }

            public string SourceLinePlus1 { get; }
            public string SourceLinePlus2 { get; }

            public bool HasWarning { get; set; }
        }

        private sealed class DependencyTreeNode
        {
            public DependencyTreeNode(DependencyRegistry registry)
            {
                Registry = registry;
            }

            public DependencyRegistry Registry { get; }
            public List<DependencyTreeNode> Children { get; } = new();
            public bool FoldoutExpanded { get; set; } = true;
        }

        private class AssetReferencesData
        {
            public AssetReferencesData(string path, string guid, string addressableGroup)
            {
                Path = path;
                Guid = guid;
                AddressableGroup = addressableGroup;
            }

            public string Path { get; }
            public string Guid { get; }
            public string AddressableGroup { get; set; }

            public bool DependencyFoldout { get; set; }
            public bool ExternalGuidsFoldout { get; set; }

            public List<ExternalGuidRegistry> ExternalGuids { get; } = new();
            public List<DependencyRegistry> Dependencies { get; private set; } = new();

            /// <summary>Recursive dependency hierarchy (direct dependencies as roots).</summary>
            public List<DependencyTreeNode> DependencyRoots { get; set; } = new();

            /// <summary>Total rows in dependency tree nodes (counts duplicate paths).</summary>
            public int DependencyTreeRowCount { get; set; }

            public void SortDependencies()
            {
                Dependencies = Dependencies
                    .OrderByDescending(d => d.IsInOtherAddressablesBundle)
                    .ThenByDescending(d => d.IsInResources)
                    .ThenByDescending(d => d.IsBuiltinExtra)
                    .ThenByDescending(d => d.IsInEditor)
                    .ThenByDescending(d => d.IsInPackage)
                    .ThenByDescending(d => d.IsInAssetDatabase)
                    .ThenByDescending(d => d.Extension)
                    .ToList();
            }
        }

        public class LiteBuildLayoutProvider
        {
            private readonly Dictionary<string, string> _assetPathToBundle = new(StringComparer.OrdinalIgnoreCase);

            private LiteBuildLayoutProvider()
            {
            }

            public static LiteBuildLayoutProvider Load(string path)
            {
                var text = File.ReadAllText(path);
                var provider = new LiteBuildLayoutProvider();

                var lines = new List<string>();
                foreach (var line in text.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);

                for (var n = 0; n < lines.Count; ++n)
                {
                    var line = lines[n];

                    if (line.StartsWith("BuiltIn Bundles", StringComparison.Ordinal))
                    {
                        ReadBundles(ref n, provider, lines);
                        continue;
                    }

                    if (line.StartsWith("Group ", StringComparison.Ordinal)) ReadBundles(ref n, provider, lines);
                }

                return provider;
            }

            public string GetBundleNameByAssetPath(string assetPath)
            {
                return _assetPathToBundle.TryGetValue(assetPath, out var bundleName) ? bundleName : string.Empty;
            }

            private static void ReadBundles(ref int index, LiteBuildLayoutProvider provider, List<string> lines)
            {
                var containerIndent = GetIndent(lines[index]);
                index++;

                for (; index < lines.Count; ++index)
                {
                    var l = lines[index];
                    var lineIndent = GetIndent(l);
                    if (lineIndent <= containerIndent)
                    {
                        index--;
                        return;
                    }

                    if (!l.StartsWith("\tArchive")) continue;

                    var bundleName = ExtractName(l);
                    var bundleIndent = lineIndent;

                    for (index++; index < lines.Count; ++index)
                    {
                        if (GetIndent(lines[index]) <= bundleIndent)
                            break;

                        var trimmed = lines[index].TrimStart();
                        if (trimmed.StartsWith("Explicit Assets", StringComparison.OrdinalIgnoreCase))
                            ReadExplicitAssets(ref index, provider, bundleName, lines);
                    }

                    index--;
                }
            }

            private static void ReadExplicitAssets(ref int index, LiteBuildLayoutProvider provider,
                string bundleName, List<string> lines)
            {
                var assetsIndent = GetIndent(lines[index]);
                index++;

                for (; index < lines.Count; ++index)
                {
                    var l = lines[index];
                    var lineIndent = GetIndent(l);
                    if (lineIndent <= assetsIndent)
                    {
                        index--;
                        return;
                    }

                    var assetName = ExtractName(l);
                    if (!string.IsNullOrEmpty(assetName)) provider._assetPathToBundle[assetName] = bundleName;

                    var assetIndent = lineIndent;
                    for (index++; index < lines.Count; ++index)
                        if (GetIndent(lines[index]) <= assetIndent)
                            break;

                    index--;
                }
            }

            private static string ExtractName(string line)
            {
                var lastParen = line.LastIndexOf(')');
                var firstParen = lastParen;

                var count = 1;
                for (var n = lastParen - 1; n >= 0; --n)
                {
                    if (line[n] == ')') count++;
                    if (line[n] == '(') count--;
                    if (count == 0)
                    {
                        firstParen = n;
                        break;
                    }
                }

                if (firstParen != lastParen && firstParen > 0)
                    line = line.Substring(0, firstParen);

                return line.Trim();
            }

            private static int GetIndent(string s)
            {
                var count = 0;
                foreach (var t in s)
                    if (t == '\t') count++;
                    else break;

                return count;
            }
        }

        private static class GUIUtilities
        {
            private static void HorizontalLine(
                int marginTop,
                int marginBottom,
                int height,
                Color color
            )
            {
                EditorGUILayout.BeginHorizontal();
                var rect = EditorGUILayout.GetControlRect(
                    false,
                    height,
                    new GUIStyle { margin = new RectOffset(0, 0, marginTop, marginBottom) }
                );

                EditorGUI.DrawRect(rect, color);
                EditorGUILayout.EndHorizontal();
            }

            public static void HorizontalLine(
                int marginTop = 5,
                int marginBottom = 5,
                int height = 2
            )
            {
                HorizontalLine(marginTop, marginBottom, height, new Color(0.5f, 0.5f, 0.5f, 1));
            }
        }
    }
}