using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace BlendshapeAnalyzer.Editor
{
    public class AvatarBlendshapeAnalyzer : EditorWindow
    {
        private GameObject _avatarRoot;
        private List<MeshData> _analyzedMeshes = new List<MeshData>();
        private bool _hasAnalyzed = false;
        private Vector2 _scrollPosition;

        [MenuItem("Tools/K1TTY/Blendshape Optimizer")]
        public static void ShowWindow()
        {
            var window = GetWindow<AvatarBlendshapeAnalyzer>();
            window.titleContent = new GUIContent("Blendshape Analyzer");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            if (Selection.activeGameObject != null)
            {
                if (Selection.activeGameObject.GetComponent("VRCAvatarDescriptor") != null ||
                    Selection.activeGameObject.GetComponent<Animator>() != null)
                {
                    _avatarRoot = Selection.activeGameObject;
                }
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            _avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", _avatarRoot, typeof(GameObject), true);
            
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Analyze Avatar", GUILayout.Height(30)))
            {
                AnalyzeAvatar();
            }
            if (_hasAnalyzed && _avatarRoot != null)
            {
                if (GUILayout.Button("Optimize Avatar", GUILayout.Height(30)))
                {
                    OptimizeAvatar();
                }
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            if (_hasAnalyzed)
            {
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

                foreach (var meshData in _analyzedMeshes)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    EditorGUILayout.BeginHorizontal();
                    string status = meshData.isUsed ? "" : " [UNUSED MESH - WILL BE DELETED]";
                    meshData.isExpanded = EditorGUILayout.Foldout(meshData.isExpanded, $"{meshData.renderer.name} ({meshData.relativePath}){status}", true);
                    
                    GUILayout.FlexibleSpace();
                    
                    if (GUILayout.Button("Select", GUILayout.Width(60)))
                    {
                        Selection.activeGameObject = meshData.renderer.gameObject;
                        EditorGUIUtility.PingObject(meshData.renderer.gameObject);
                    }
                    if (meshData.unusedBlendshapes.Count > 0 && GUILayout.Button("Copy Unused", GUILayout.Width(95)))
                    {
                        EditorGUIUtility.systemCopyBuffer = meshData.unusedBlendshapesText;
                        Debug.Log($"Copied unused blendshapes of {meshData.renderer.name} to clipboard.");
                    }
                    EditorGUILayout.EndHorizontal();

                    if (meshData.isExpanded)
                    {
                        EditorGUILayout.Space(5);
                        
                        EditorGUILayout.LabelField($"Unused Blendshapes ({meshData.unusedBlendshapes.Count}):", EditorStyles.boldLabel);
                        if (meshData.unusedBlendshapes.Count > 0)
                        {
                            float height = Mathf.Min(150, Mathf.Max(40, meshData.unusedBlendshapes.Count * 15));
                            EditorGUILayout.TextArea(meshData.unusedBlendshapesText, GUILayout.Height(height));
                        }
                        else
                        {
                            EditorGUILayout.LabelField("None");
                        }

                        EditorGUILayout.Space(5);

                        EditorGUILayout.LabelField($"Used Blendshapes ({meshData.usedBlendshapes.Count}):", EditorStyles.boldLabel);
                        if (meshData.usedBlendshapes.Count > 0)
                        {
                            float height = Mathf.Min(150, Mathf.Max(40, meshData.usedBlendshapes.Count * 15));
                            EditorGUILayout.TextArea(meshData.usedBlendshapesText, GUILayout.Height(height));
                        }
                        else
                        {
                            EditorGUILayout.LabelField("None");
                        }
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(5);
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void AnalyzeAvatar()
        {
            if (_avatarRoot == null) return;

            try
            {
                EditorUtility.DisplayProgressBar("Analyzing Avatar", "Gathering meshes and controllers...", 0f);

                _analyzedMeshes.Clear();

                // 1. Gather all SkinnedMeshRenderers
                var smrs = _avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);

                // 2. Gather all Animator Controllers and Standalone Clips
                List<ControllerSource> controllerSources = new List<ControllerSource>();
                List<StandaloneClipSource> standaloneClipSources = new List<StandaloneClipSource>();
                List<Tuple<string, Renderer, bool>> vrcfDirectBlendshapes = new List<Tuple<string, Renderer, bool>>();

                // VRChat Descriptor
                Component vrcDescriptor = _avatarRoot.GetComponent("VRCAvatarDescriptor");
                if (vrcDescriptor != null)
                {
                    controllerSources.AddRange(GetVRCControllers(vrcDescriptor));
                }

                // Child/Root Animators
                var animators = _avatarRoot.GetComponentsInChildren<Animator>(true);
                foreach (var animator in animators)
                {
                    if (animator.runtimeAnimatorController != null)
                    {
                        string sourceName = animator.transform == _avatarRoot.transform 
                            ? "Root Animator" 
                            : $"Child Animator on '{animator.name}'";

                        if (!controllerSources.Any(s => s.controller == animator.runtimeAnimatorController && s.rootTransform == animator.transform))
                        {
                            controllerSources.Add(new ControllerSource
                            {
                                controller = animator.runtimeAnimatorController,
                                rootTransform = animator.transform,
                                sourceName = sourceName
                            });
                        }
                    }
                }

                // Modular Avatar & VRCFury Scanning
                var components = _avatarRoot.GetComponentsInChildren<Component>(true);
                List<Component> vrcFuryComponents = new List<Component>();

                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string compTypeName = comp.GetType().Name;

                    if (compTypeName.Contains("ModularAvatarMergeAnimator"))
                    {
                        var animatorProp = comp.GetType().GetProperty("animator");
                        var animatorField = comp.GetType().GetField("animator");
                        RuntimeAnimatorController rac = null;
                        if (animatorProp != null) rac = animatorProp.GetValue(comp) as RuntimeAnimatorController;
                        else if (animatorField != null) rac = animatorField.GetValue(comp) as RuntimeAnimatorController;

                        if (rac != null && !controllerSources.Any(s => s.controller == rac))
                        {
                            controllerSources.Add(new ControllerSource
                            {
                                controller = rac,
                                rootTransform = comp.transform,
                                sourceName = $"Modular Avatar MergeAnimator on '{comp.gameObject.name}'"
                            });
                        }
                    }
                    else if (compTypeName.Equals("VRCFury", StringComparison.Ordinal) || compTypeName.Contains("VRCFury"))
                    {
                        vrcFuryComponents.Add(comp);
                        
                        List<RuntimeAnimatorController> extractedControllers = new List<RuntimeAnimatorController>();
                        List<AnimationClip> extractedClips = new List<AnimationClip>();
                        
                        ExtractFromVRCFury(comp, extractedControllers, extractedClips, vrcfDirectBlendshapes);

                        foreach (var ctrl in extractedControllers)
                        {
                            if (ctrl != null && !controllerSources.Any(s => s.controller == ctrl))
                            {
                                controllerSources.Add(new ControllerSource
                                {
                                    controller = ctrl,
                                    rootTransform = comp.transform,
                                    sourceName = $"VRCFury Controller on '{comp.name}'"
                                });
                            }
                        }

                        foreach (var clip in extractedClips)
                        {
                            if (clip != null)
                            {
                                standaloneClipSources.Add(new StandaloneClipSource
                                {
                                    clip = clip,
                                    rootTransform = comp.transform,
                                    sourceName = $"VRCFury Action on '{comp.name}'"
                                });
                            }
                        }
                    }
                }

                // 3. Extract and cache all unique clips
                Dictionary<AnimationClip, EditorCurveBinding[]> clipBindingsCache = new Dictionary<AnimationClip, EditorCurveBinding[]>();
                HashSet<AnimationClip> uniqueClips = new HashSet<AnimationClip>();

                foreach (var source in controllerSources)
                {
                    var clips = GetClipsFromController(source.controller);
                    foreach (var clip in clips)
                    {
                        if (clip != null) uniqueClips.Add(clip);
                    }
                }
                foreach (var source in standaloneClipSources)
                {
                    if (source.clip != null) uniqueClips.Add(source.clip);
                }

                int clipIdx = 0;
                int clipCount = uniqueClips.Count;
                foreach (var clip in uniqueClips)
                {
                    float progress = (float)clipIdx / Mathf.Max(1, clipCount);
                    EditorUtility.DisplayProgressBar("Analyzing Avatar", $"Parsing clip curves: {clip.name} ({clipIdx + 1}/{clipCount})...", progress);
                    
                    try
                    {
                        clipBindingsCache[clip] = AnimationUtility.GetCurveBindings(clip);
                    }
                    catch
                    {
                        clipBindingsCache[clip] = new EditorCurveBinding[0];
                    }
                    clipIdx++;
                }

                // 4. Process each mesh
                for (int mIdx = 0; mIdx < smrs.Length; mIdx++)
                {
                    var smr = smrs[mIdx];
                    if (smr.sharedMesh == null) continue;

                    float meshProgress = (float)mIdx / smrs.Length;
                    EditorUtility.DisplayProgressBar("Analyzing Avatar", $"Analyzing Mesh: {smr.name}...", meshProgress);

                    var meshData = new MeshData
                    {
                        renderer = smr,
                        relativePath = GetRelativePath(_avatarRoot.transform, smr.transform),
                        isExpanded = false
                    };

                    int bsCount = smr.sharedMesh.blendShapeCount;
                    for (int i = 0; i < bsCount; i++)
                    {
                        string bsName = smr.sharedMesh.GetBlendShapeName(i);
                        bool isUsed = false;

                        // A. Check descriptor (LipSync/Eyelids)
                        if (vrcDescriptor != null)
                        {
                            if (AnalyzeVRCDescriptor(vrcDescriptor, smr, bsName, i))
                            {
                                isUsed = true;
                            }
                        }

                        // B. Check VRCFury Direct Blendshape Actions
                        if (!isUsed)
                        {
                            foreach (var directBs in vrcfDirectBlendshapes)
                            {
                                if (directBs.Item1.Equals(bsName, StringComparison.Ordinal))
                                {
                                    if (directBs.Item3 || directBs.Item2 == smr)
                                    {
                                        isUsed = true;
                                        break;
                                    }
                                }
                            }
                        }

                        // C. Check controller animation clips
                        if (!isUsed)
                        {
                            foreach (var source in controllerSources)
                            {
                                var controllerClips = GetClipsFromController(source.controller);
                                string expectedPathLocal = GetRelativePath(source.rootTransform, smr.transform);
                                string expectedPathRoot = GetRelativePath(_avatarRoot.transform, smr.transform);

                                foreach (var clip in controllerClips)
                                {
                                    if (clip == null) continue;

                                    if (clipBindingsCache.TryGetValue(clip, out var bindings))
                                    {
                                        foreach (var binding in bindings)
                                        {
                                            bool isCorrectPath = binding.path.Equals(expectedPathLocal, StringComparison.Ordinal) ||
                                                                 binding.path.Equals(expectedPathRoot, StringComparison.Ordinal);

                                            if (isCorrectPath)
                                            {
                                                meshData.isReferencedByAnimation = true;
                                                if (binding.propertyName.Equals($"blendShape.{bsName}", StringComparison.Ordinal))
                                                {
                                                    isUsed = true;
                                                }
                                            }
                                        }
                                    }
                                    if (isUsed) break;
                                }
                                if (isUsed) break;
                            }
                        }

                        // D. Check standalone VRCFury clips
                        if (!isUsed)
                        {
                            foreach (var source in standaloneClipSources)
                            {
                                string expectedPathLocal = GetRelativePath(source.rootTransform, smr.transform);
                                string expectedPathRoot = GetRelativePath(_avatarRoot.transform, smr.transform);

                                if (clipBindingsCache.TryGetValue(source.clip, out var bindings))
                                {
                                    foreach (var binding in bindings)
                                    {
                                        bool isCorrectPath = binding.path.Equals(expectedPathLocal, StringComparison.Ordinal) ||
                                                             binding.path.Equals(expectedPathRoot, StringComparison.Ordinal);

                                        if (isCorrectPath)
                                        {
                                            meshData.isReferencedByAnimation = true;
                                            if (binding.propertyName.Equals($"blendShape.{bsName}", StringComparison.Ordinal))
                                            {
                                                isUsed = true;
                                            }
                                        }
                                    }
                                }
                                if (isUsed) break;
                            }
                        }

                        if (isUsed)
                        {
                            meshData.usedBlendshapes.Add(bsName);
                        }
                        else
                        {
                            meshData.unusedBlendshapes.Add(bsName);
                        }
                    }

                    // Determine if the entire mesh is active/referenced/used
                    bool isMeshUsed = (smr.sharedMesh.blendShapeCount == 0) ||  // Keep meshes with no blendshapes
                                      smr.gameObject.activeSelf || 
                                      meshData.usedBlendshapes.Count > 0 || 
                                      meshData.isReferencedByAnimation;

                    if (!isMeshUsed)
                    {
                        foreach (var vrcfComp in vrcFuryComponents)
                        {
                            if (IsRendererReferencedInVRCFury(vrcfComp, smr))
                            {
                                isMeshUsed = true;
                                break;
                            }
                        }
                    }

                    meshData.isUsed = isMeshUsed;
                    meshData.unusedBlendshapesText = string.Join("\n", meshData.unusedBlendshapes);
                    meshData.usedBlendshapesText = string.Join("\n", meshData.usedBlendshapes);

                    _analyzedMeshes.Add(meshData);
                }

                _hasAnalyzed = true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void OptimizeAvatar()
        {
            if (_avatarRoot == null || !_hasAnalyzed) return;

            string avatarFolderName = _avatarRoot.name.Replace(" ", "_");
            string rootPath = "Assets/K1TTY/OptimizedMesh/" + avatarFolderName;
            if (!AssetDatabase.IsValidFolder("Assets/K1TTY"))
                AssetDatabase.CreateFolder("Assets", "K1TTY");
            if (!AssetDatabase.IsValidFolder("Assets/K1TTY/OptimizedMesh"))
                AssetDatabase.CreateFolder("Assets/K1TTY", "OptimizedMesh");
            if (!AssetDatabase.IsValidFolder(rootPath))
                AssetDatabase.CreateFolder("Assets/K1TTY/OptimizedMesh", avatarFolderName);

            try
            {
                EditorUtility.DisplayProgressBar("Optimizing Avatar", "Duplicating avatar...", 0.1f);

                GameObject optimizedAvatar = Instantiate(_avatarRoot);
                optimizedAvatar.name = _avatarRoot.name + "_Optimized";
                Undo.RegisterCreatedObjectUndo(optimizedAvatar, "Generate Optimized Avatar");

                var newSMRs = optimizedAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);

                // Map relative path to new SMR
                Dictionary<string, SkinnedMeshRenderer> pathToNewSMR = new Dictionary<string, SkinnedMeshRenderer>();
                foreach (var newSMR in newSMRs)
                {
                    string path = GetRelativePath(optimizedAvatar.transform, newSMR.transform);
                    pathToNewSMR[path] = newSMR;
                }

                for (int i = 0; i < _analyzedMeshes.Count; i++)
                {
                    var meshData = _analyzedMeshes[i];
                    float progress = 0.1f + 0.8f * ((float)i / _analyzedMeshes.Count);
                    EditorUtility.DisplayProgressBar("Optimizing Avatar", $"Processing Mesh: {meshData.renderer.name}...", progress);

                    if (pathToNewSMR.TryGetValue(meshData.relativePath, out var targetSMR))
                    {
                        if (!meshData.isUsed)
                        {
                            // Delete completely unused meshes
                            Undo.DestroyObjectImmediate(targetSMR.gameObject);
                            continue;
                        }

                        // Strip unused blendshapes from the mesh
                        if (targetSMR.sharedMesh != null && meshData.unusedBlendshapes.Count > 0)
                        {
                            // Save original weights by name FIRST
                            Dictionary<string, float> originalWeights = new Dictionary<string, float>();
                            int oldBsCount = targetSMR.sharedMesh.blendShapeCount;
                            for (int bsIdx = 0; bsIdx < oldBsCount; bsIdx++)
                            {
                                string bsName = targetSMR.sharedMesh.GetBlendShapeName(bsIdx);
                                originalWeights[bsName] = targetSMR.GetBlendShapeWeight(bsIdx);
                            }

                            // Build a safety set: used blendshapes PLUS any standard VRChat viseme
                            // names present on this mesh, so voice never breaks.
                            var keepSet = new HashSet<string>(meshData.usedBlendshapes);
                            int meshBsCount = targetSMR.sharedMesh.blendShapeCount;
                            for (int bsIdx = 0; bsIdx < meshBsCount; bsIdx++)
                            {
                                string bsName = targetSMR.sharedMesh.GetBlendShapeName(bsIdx);
                                if (VRChatRequiredVisemes.Contains(bsName))
                                    keepSet.Add(bsName);
                            }

                            string assetPath = $"{rootPath}/{targetSMR.sharedMesh.name}_optimized.asset";
                            Mesh optimizedMesh = CreateOptimizedMesh(targetSMR.sharedMesh, keepSet, assetPath, originalWeights);

                            // Assign optimized mesh
                            targetSMR.sharedMesh = optimizedMesh;

                            // Apply weights back to matching blendshapes
                            int newBsCount = optimizedMesh.blendShapeCount;
                            for (int bsIdx = 0; bsIdx < newBsCount; bsIdx++)
                            {
                                string bsName = optimizedMesh.GetBlendShapeName(bsIdx);
                                if (originalWeights.TryGetValue(bsName, out float weight))
                                {
                                    targetSMR.SetBlendShapeWeight(bsIdx, weight);
                                }
                            }
                        }
                    }
                }

                // FBX Exporter requires an absolute filesystem path
                string fbxRelativePath = $"{rootPath}/{optimizedAvatar.name}.fbx";
                string fbxAbsolutePath = System.IO.Path.GetFullPath(fbxRelativePath);
                bool fbxExported = ExportToFbxIfPossible(optimizedAvatar, fbxAbsolutePath);
                string fbxSavePath = fbxRelativePath;

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (fbxExported)
                {
                    EditorUtility.DisplayDialog("Success", $"Optimized copy created and exported to FBX!\nPath: {fbxSavePath}", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Success", $"Optimized copy created in Scene!\nMesh assets saved to: {rootPath}\n(Note: Install Unity's 'FBX Exporter' package to automatically export as an FBX file)", "OK");
                }

                Selection.activeGameObject = optimizedAvatar;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlendshapeAnalyzer] Error during optimization: {ex}");
                EditorUtility.DisplayDialog("Error", $"An error occurred during optimization:\n{ex.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static Mesh CreateOptimizedMesh(Mesh sourceMesh, HashSet<string> usedBlendshapes, string savePath, Dictionary<string, float> originalWeights = null)
        {
            Mesh newMesh = new Mesh();
            newMesh.name = sourceMesh.name + "_Optimized";

            Vector3[] vertices = sourceMesh.vertices;
            Vector3[] normals = sourceMesh.normals;
            Vector4[] tangents = sourceMesh.tangents;

            // Bake unused blendshapes with non-zero weights into the mesh basis
            if (originalWeights != null)
            {
                int sourceBsCount = sourceMesh.blendShapeCount;
                for (int i = 0; i < sourceBsCount; i++)
                {
                    string bsName = sourceMesh.GetBlendShapeName(i);
                    
                    // If this blendshape is unused but has a non-zero weight, bake it into the basis
                    if (!usedBlendshapes.Contains(bsName) && originalWeights.TryGetValue(bsName, out float weight) && weight != 0f)
                    {
                        int frameCount = sourceMesh.GetBlendShapeFrameCount(i);
                        if (frameCount > 0)
                        {
                            Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
                            Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
                            Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];
                            
                            // Get the frame data (usually frame 0 for 100% weight)
                            sourceMesh.GetBlendShapeFrameVertices(i, frameCount - 1, deltaVertices, deltaNormals, deltaTangents);
                            
                            // Apply the blendshape deformation proportional to its weight (normalized to 0-1)
                            float normalizedWeight = weight / 100f;
                            for (int v = 0; v < vertices.Length; v++)
                            {
                                vertices[v] += deltaVertices[v] * normalizedWeight;
                                normals[v] += deltaNormals[v] * normalizedWeight;
                                // Tangents are Vector4 where XYZ is tangent direction and W is binormal direction
                                tangents[v] += new Vector4(deltaTangents[v].x, deltaTangents[v].y, deltaTangents[v].z, 0) * normalizedWeight;
                            }
                        }
                    }
                }
            }

            newMesh.vertices = vertices;
            newMesh.normals = normals;
            newMesh.tangents = tangents;
            newMesh.uv = sourceMesh.uv;
            newMesh.uv2 = sourceMesh.uv2;
            newMesh.uv3 = sourceMesh.uv3;
            newMesh.uv4 = sourceMesh.uv4;
            newMesh.colors = sourceMesh.colors;
            newMesh.boneWeights = sourceMesh.boneWeights;
            newMesh.bindposes = sourceMesh.bindposes;

            newMesh.subMeshCount = sourceMesh.subMeshCount;
            for (int i = 0; i < sourceMesh.subMeshCount; i++)
            {
                newMesh.SetIndices(sourceMesh.GetIndices(i), sourceMesh.GetTopology(i), i);
            }

            int usedBsCount = sourceMesh.blendShapeCount;
            for (int i = 0; i < usedBsCount; i++)
            {
                string bsName = sourceMesh.GetBlendShapeName(i);
                if (usedBlendshapes.Contains(bsName))
                {
                    int frameCount = sourceMesh.GetBlendShapeFrameCount(i);
                    for (int f = 0; f < frameCount; f++)
                    {
                        float weight = sourceMesh.GetBlendShapeFrameWeight(i, f);
                        Vector3[] deltaVertices = new Vector3[sourceMesh.vertexCount];
                        Vector3[] deltaNormals = new Vector3[sourceMesh.vertexCount];
                        Vector3[] deltaTangents = new Vector3[sourceMesh.vertexCount];
                        
                        sourceMesh.GetBlendShapeFrameVertices(i, f, deltaVertices, deltaNormals, deltaTangents);
                        newMesh.AddBlendShapeFrame(bsName, weight, deltaVertices, deltaNormals, deltaTangents);
                    }
                }
            }

            newMesh.bounds = sourceMesh.bounds;

            AssetDatabase.CreateAsset(newMesh, savePath);
            return newMesh;
        }

        private static bool ExportToFbxIfPossible(GameObject obj, string absoluteSavePath)
        {
            try
            {
                // Assembly name for com.unity.formats.fbx package
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.Equals("Unity.Formats.Fbx.Editor", StringComparison.Ordinal));

                if (assembly == null)
                {
                    Debug.LogWarning("[BlendshapeAnalyzer] FBX Exporter assembly 'Unity.Formats.Fbx.Editor' not found. Is com.unity.formats.fbx installed?");
                    return false;
                }

                var exporterType = assembly.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter");
                if (exporterType == null)
                {
                    Debug.LogWarning("[BlendshapeAnalyzer] Could not find type UnityEditor.Formats.Fbx.Exporter.ModelExporter.");
                    return false;
                }

                var exportMethod = exporterType.GetMethod(
                    "ExportObject",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(string), typeof(UnityEngine.Object) },
                    null);

                if (exportMethod == null)
                {
                    Debug.LogWarning("[BlendshapeAnalyzer] Could not find ModelExporter.ExportObject(string, Object) method.");
                    return false;
                }

                var result = exportMethod.Invoke(null, new object[] { absoluteSavePath, obj }) as string;
                bool success = !string.IsNullOrEmpty(result);
                if (success)
                    Debug.Log($"[BlendshapeAnalyzer] FBX exported to: {absoluteSavePath}");
                else
                    Debug.LogWarning($"[BlendshapeAnalyzer] FBX Exporter returned null/empty path — export may have failed.");
                return success;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BlendshapeAnalyzer] Failed to export FBX via reflection: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (root == child) return "";

            List<string> pathParts = new List<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                pathParts.Add(current.name);
                current = current.parent;
            }

            pathParts.Reverse();
            return string.Join("/", pathParts);
        }

        private static HashSet<AnimationClip> GetClipsFromController(RuntimeAnimatorController runtimeController)
        {
            var clips = new HashSet<AnimationClip>();
            if (runtimeController == null) return clips;

            foreach (var clip in runtimeController.animationClips)
            {
                if (clip != null) clips.Add(clip);
            }

            AnimatorController ac = runtimeController as AnimatorController;
            if (ac == null && runtimeController is AnimatorOverrideController overrideController)
            {
                ac = overrideController.runtimeAnimatorController as AnimatorController;
            }

            if (ac != null)
            {
                foreach (var layer in ac.layers)
                {
                    if (layer != null && layer.stateMachine != null)
                    {
                        ExtractClipsFromStateMachine(layer.stateMachine, clips);
                    }
                }
            }

            return clips;
        }

        private static void ExtractClipsFromStateMachine(AnimatorStateMachine stateMachine, HashSet<AnimationClip> clips)
        {
            if (stateMachine == null) return;

            foreach (var childState in stateMachine.states)
            {
                if (childState.state != null && childState.state.motion != null)
                {
                    ExtractClipsFromMotion(childState.state.motion, clips);
                }
            }

            foreach (var childStateMachine in stateMachine.stateMachines)
            {
                if (childStateMachine.stateMachine != null)
                {
                    ExtractClipsFromStateMachine(childStateMachine.stateMachine, clips);
                }
            }
        }

        private static void ExtractClipsFromMotion(Motion motion, HashSet<AnimationClip> clips)
        {
            if (motion == null) return;

            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
            }
            else if (motion is BlendTree blendTree)
            {
                foreach (var child in blendTree.children)
                {
                    if (child.motion != null)
                    {
                        ExtractClipsFromMotion(child.motion, clips);
                    }
                }
            }
        }

        // The 15 standard VRChat viseme blendshape names required by VRChat for voice lip sync.
        // These must be preserved on the designated VisemeMeshRenderer regardless of animation usage.
        private static readonly HashSet<string> VRChatRequiredVisemes = new HashSet<string>(StringComparer.Ordinal)
        {
            "vrc.v_sil",
            "vrc.v_pp",
            "vrc.v_ff",
            "vrc.v_th",
            "vrc.v_dd",
            "vrc.v_kk",
            "vrc.v_ch",
            "vrc.v_ss",
            "vrc.v_nn",
            "vrc.v_rr",
            "vrc.v_aa",
            "vrc.v_e",
            "vrc.v_ih",
            "vrc.v_oh",
            "vrc.v_ou"
        };

        private static bool AnalyzeVRCDescriptor(Component descriptor, SkinnedMeshRenderer smr, string blendShapeName, int blendShapeIndex)
        {
            if (descriptor == null) return false;
            Type type = descriptor.GetType();

            // Check LipSync
            try
            {
                var lipSyncField = type.GetField("lipSync");
                if (lipSyncField != null)
                {
                    var lipSyncVal = lipSyncField.GetValue(descriptor);
                    if (lipSyncVal != null)
                    {
                        int lipSyncEnumVal = Convert.ToInt32(lipSyncVal);

                        if (lipSyncEnumVal == 3) // VisemeBlendShape
                        {
                            // Check configured viseme array
                            var visemeBlendShapesField = type.GetField("VisemeBlendShapes");
                            if (visemeBlendShapesField != null)
                            {
                                string[] visemes = visemeBlendShapesField.GetValue(descriptor) as string[];
                                if (visemes != null && visemes.Contains(blendShapeName)) return true;
                            }

                            // Always protect all standard VRChat viseme names on the designated viseme renderer
                            var visemeSMRField = type.GetField("VisemeMeshRenderer");
                            if (visemeSMRField != null)
                            {
                                var visemeSMR = visemeSMRField.GetValue(descriptor) as SkinnedMeshRenderer;
                                if (visemeSMR == smr && VRChatRequiredVisemes.Contains(blendShapeName)) return true;
                            }
                        }
                        else if (lipSyncEnumVal == 1) // JawFlap
                        {
                            var jawFlapField = type.GetField("jawFlapBlendshapeName");
                            if (jawFlapField != null)
                            {
                                string jawFlapName = jawFlapField.GetValue(descriptor) as string;
                                if (jawFlapName == blendShapeName) return true;
                            }
                        }
                        else if (lipSyncEnumVal == 2) // VisemeParameterOnly - still protect names on viseme mesh
                        {
                            var visemeSMRField = type.GetField("VisemeMeshRenderer");
                            if (visemeSMRField != null)
                            {
                                var visemeSMR = visemeSMRField.GetValue(descriptor) as SkinnedMeshRenderer;
                                if (visemeSMR == smr && VRChatRequiredVisemes.Contains(blendShapeName)) return true;
                            }
                        }
                    }
                }
            }
            catch { }

            // Check Eyelids
            try
            {
                var eyelidTypeField = type.GetField("eyelidType");
                if (eyelidTypeField != null)
                {
                    var eyelidTypeVal = eyelidTypeField.GetValue(descriptor);
                    if (eyelidTypeVal != null)
                    {
                        int eyelidTypeEnumVal = Convert.ToInt32(eyelidTypeVal);
                        if (eyelidTypeEnumVal == 2) // Blendshapes
                        {
                            var eyelidsSkinnedMeshField = type.GetField("eyelidsSkinnedMesh");
                            var eyelidsBlendshapesField = type.GetField("eyelidsBlendshapes");
                            if (eyelidsSkinnedMeshField != null && eyelidsBlendshapesField != null)
                            {
                                var eyelidsSMR = eyelidsSkinnedMeshField.GetValue(descriptor) as SkinnedMeshRenderer;
                                int[] eyelidIndices = eyelidsBlendshapesField.GetValue(descriptor) as int[];
                                if (eyelidsSMR == smr && eyelidIndices != null && eyelidIndices.Contains(blendShapeIndex)) return true;
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static List<ControllerSource> GetVRCControllers(Component descriptor)
        {
            List<ControllerSource> sources = new List<ControllerSource>();
            if (descriptor == null) return sources;

            Type type = descriptor.GetType();
            
            Action<string, string> extractLayers = (fieldName, sourceNamePrefix) =>
            {
                try
                {
                    var field = type.GetField(fieldName);
                    if (field != null)
                    {
                        var array = field.GetValue(descriptor) as Array;
                        if (array != null)
                        {
                            for (int i = 0; i < array.Length; i++)
                            {
                                var layer = array.GetValue(i);
                                if (layer == null) continue;
                                
                                var animCtrlField = layer.GetType().GetField("animatorController");
                                var typeField = layer.GetType().GetField("type");
                                
                                if (animCtrlField != null)
                                {
                                    var ctrl = animCtrlField.GetValue(layer) as RuntimeAnimatorController;
                                    if (ctrl != null)
                                    {
                                        string layerTypeName = "Custom";
                                        if (typeField != null)
                                        {
                                            var typeVal = typeField.GetValue(layer);
                                            layerTypeName = typeVal.ToString();
                                        }
                                        
                                        sources.Add(new ControllerSource
                                        {
                                            controller = ctrl,
                                            rootTransform = descriptor.transform,
                                            sourceName = $"{sourceNamePrefix} ({layerTypeName})"
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BlendshapeAnalyzer] Failed to read VRC descriptor field '{fieldName}': {ex.Message}");
                }
            };

            extractLayers("baseAnimationLayers", "VRC Base Layer");
            extractLayers("specialAnimationLayers", "VRC Special Layer");

            return sources;
        }

        // Reflection extraction of VRCFury components
        private static void ExtractFromVRCFury(
            Component vrcFuryComponent, 
            List<RuntimeAnimatorController> controllers, 
            List<AnimationClip> clips, 
            List<Tuple<string, Renderer, bool>> directBlendshapes)
        {
            if (vrcFuryComponent == null) return;
            HashSet<object> visited = new HashSet<object>();
            ScanObject(vrcFuryComponent, controllers, clips, directBlendshapes, visited);
        }

        private static void ScanObject(
            object obj, 
            List<RuntimeAnimatorController> controllers, 
            List<AnimationClip> clips, 
            List<Tuple<string, Renderer, bool>> directBlendshapes,
            HashSet<object> visited)
        {
            if (obj == null) return;
            if (obj is UnityEngine.Object unityObj && unityObj == null) return;

            if (!obj.GetType().IsValueType)
            {
                if (visited.Contains(obj)) return;
                visited.Add(obj);
            }

            if (obj is RuntimeAnimatorController rac)
            {
                if (rac != null && !controllers.Contains(rac)) controllers.Add(rac);
                return;
            }
            if (obj is AnimationClip clip)
            {
                if (clip != null && !clips.Contains(clip)) clips.Add(clip);
                return;
            }

            Type type = obj.GetType();

            // Check if GuidWrapper
            if (type.BaseType != null && type.BaseType.IsGenericType && type.BaseType.GetGenericTypeDefinition().Name.Contains("GuidWrapper"))
            {
                var objRefField = type.GetField("objRef", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (objRefField != null)
                {
                    var val = objRefField.GetValue(obj);
                    if (val != null)
                    {
                        if (val is RuntimeAnimatorController racVal)
                        {
                            if (!controllers.Contains(racVal)) controllers.Add(racVal);
                        }
                        else if (val is AnimationClip clipVal)
                        {
                            if (!clips.Contains(clipVal)) clips.Add(clipVal);
                        }
                    }
                }
            }

            // Check if BlendShapeAction
            if (type.Name.Equals("BlendShapeAction", StringComparison.Ordinal) || type.Name.Contains("BlendShapeAction"))
            {
                string bsName = "";
                Renderer rend = null;
                bool allRends = true;

                var bsField = type.GetField("blendShape", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (bsField != null) bsName = bsField.GetValue(obj) as string;

                var rendField = type.GetField("renderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (rendField != null) rend = rendField.GetValue(obj) as Renderer;

                var allRendsField = type.GetField("allRenderers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (allRendsField != null)
                {
                    var val = allRendsField.GetValue(obj);
                    if (val is bool bVal) allRends = bVal;
                }

                if (!string.IsNullOrEmpty(bsName))
                {
                    directBlendshapes.Add(new Tuple<string, Renderer, bool>(bsName, rend, allRends));
                }
            }

            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                try
                {
                    foreach (var item in enumerable)
                    {
                        ScanObject(item, controllers, clips, directBlendshapes, visited);
                    }
                }
                catch {}
                return;
            }

            if ((type.IsClass || (type.IsValueType && !type.IsPrimitive)) && 
                !(obj is string) && 
                !(obj is UnityEngine.Object))
            {
                var fields = type.GetFields(System.Reflection.BindingFlags.Public | 
                                            System.Reflection.BindingFlags.NonPublic | 
                                            System.Reflection.BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        var val = f.GetValue(obj);
                        ScanObject(val, controllers, clips, directBlendshapes, visited);
                    }
                    catch {}
                }
            }
        }

        private static bool IsRendererReferencedInVRCFury(Component vrcFuryComponent, Renderer renderer)
        {
            if (vrcFuryComponent == null) return false;
            HashSet<object> visited = new HashSet<object>();
            return ScanObjectForReference(vrcFuryComponent, renderer, visited);
        }

        private static bool ScanObjectForReference(object obj, object target, HashSet<object> visited)
        {
            if (obj == null) return false;
            if (obj is UnityEngine.Object unityObj && unityObj == null) return false;
            if (obj == target) return true;

            if (obj is GameObject go && target is Component comp && comp.gameObject == go) return true;

            if (!obj.GetType().IsValueType)
            {
                if (visited.Contains(obj)) return false;
                visited.Add(obj);
            }

            if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
            {
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (ScanObjectForReference(item, target, visited)) return true;
                    }
                }
                catch {}
                return false;
            }

            Type type = obj.GetType();
            if ((type.IsClass || (type.IsValueType && !type.IsPrimitive)) && 
                !(obj is string) && 
                !(obj is UnityEngine.Object))
            {
                var fields = type.GetFields(System.Reflection.BindingFlags.Public | 
                                            System.Reflection.BindingFlags.NonPublic | 
                                            System.Reflection.BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        var val = f.GetValue(obj);
                        if (ScanObjectForReference(val, target, visited)) return true;
                    }
                    catch {}
                }
            }

            return false;
        }

        private class ControllerSource
        {
            public RuntimeAnimatorController controller;
            public Transform rootTransform;
            public string sourceName;
        }

        private class StandaloneClipSource
        {
            public AnimationClip clip;
            public Transform rootTransform;
            public string sourceName;
        }

        private class MeshData
        {
            public SkinnedMeshRenderer renderer;
            public string relativePath;
            public bool isExpanded;
            public bool isUsed;
            public bool isReferencedByAnimation;
            public List<string> usedBlendshapes = new List<string>();
            public List<string> unusedBlendshapes = new List<string>();
            public string usedBlendshapesText = "";
            public string unusedBlendshapesText = "";
        }
    }
}
