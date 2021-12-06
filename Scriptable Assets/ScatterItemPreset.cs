/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Mathematics;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine.Rendering;
using Cysharp.Threading.Tasks;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AshleySeric.ScatterStream
{
    [System.Serializable]
    [CreateAssetMenu(fileName = "Scatter Item", menuName = "Scatter Stream/Scatter Item", order = 0)]
    public class ScatterItemPreset : ScriptableObject
    {
        private const string BILLBOARD_CAPTURE_CAMERA_RESOURCES_PATH = "ScatterStream/BillboardCaptureCamera";
        private static readonly Vector3 BILLBOARD_CAPTURE_POSITION = new Vector3(-5000, 0, 0);
        private const int BILLBOARD_UPRIGHT_COUNT = 6;

        public Texture2D thumbnail;
        public GameObject entityPrefab;
        public float3 positionOffset;
        public float3 scaleMultiplier;
        public quaternion rotationOffset;
        public List<ScatterLod> levelsOfDetail;
        public BillboardMode billboardMode = BillboardMode.GeneratedBillboard;
        [Header("Billboard")]
        public Mesh billboardMesh;
        public Texture2D billboardTexture;
        public Material billboardMaterial;
        public int billboardResolution = 1024;
        public static ScatterItemPreset CreateFromPrefab(GameObject prefab, BillboardMode billboardMode, bool includeInactiveChildren = false)
        {
            var trans = prefab.transform;
            var resultPreset = CreateInstance<ScatterItemPreset>();
            resultPreset.entityPrefab = prefab;
            resultPreset.positionOffset = trans.position;
            resultPreset.rotationOffset = trans.rotation;
            resultPreset.scaleMultiplier = trans.lossyScale;
            resultPreset.levelsOfDetail = new List<ScatterLod>();

            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                foreach (var lod in lods)
                {
                    if (lod.renderers == null || lod.renderers.Length == 0)
                    {
                        continue;
                    }

                    resultPreset.levelsOfDetail.Add(new ScatterLod
                    {
                        drawDistance = lodGroup.size / lod.screenRelativeTransitionHeight,
                        renderables = lod.renderers.Select((x) =>
                        {
                            var filter = x.GetComponent<MeshFilter>();

                            // Skip invalid renderers.
                            if (filter != null && filter.sharedMesh != null || x.sharedMaterials.Length == 0)
                            {
                                return new ScatterRenderable
                                {
                                    mesh = filter.sharedMesh,
                                    materials = x.sharedMaterials
                                };
                            }
                            return default;
                        }).ToList(),
                        densityMultiplier = 1f
                    });
                }
            }
            else
            {
                var renderers = prefab.GetComponentsInChildren<MeshRenderer>(includeInactiveChildren);

                // Add a single lod.
                resultPreset.levelsOfDetail.Add(new ScatterLod
                {
                    drawDistance = 250f,
                    renderables = new List<ScatterRenderable>()
                });

                // Populate single lod with all renderers found.
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    var filter = renderer.GetComponent<MeshFilter>();
                    var sharedMats = renderer.sharedMaterials;

                    // Skip invalid renderers.
                    if (filter == null || filter.sharedMesh == null || sharedMats.Length == 0)
                    {
                        continue;
                    }

                    var mats = new Material[sharedMats.Length];

                    for (int j = 0; j < mats.Length; j++)
                    {
                        mats[j] = sharedMats[j];
                    }

                    resultPreset.levelsOfDetail[0].renderables.Add(new ScatterRenderable
                    {
                        mesh = filter.sharedMesh,
                        materials = mats
                    });
                }
            }

            if (billboardMode == BillboardMode.GeneratedBillboard)
            {
                var billboardData = GenerateBillboard(resultPreset, resultPreset.entityPrefab);
                resultPreset.billboardTexture = billboardData.billboardTexture;
                resultPreset.billboardMesh = billboardData.billboardMesh;
                resultPreset.billboardMaterial = billboardData.billboardMaterial;
                resultPreset.levelsOfDetail.Add(new ScatterLod
                {
                    drawDistance = 10000,
                    renderables = new List<ScatterRenderable>{
                        new ScatterRenderable
                        {
                            mesh = billboardData.billboardMesh,
                            materials = new Material[]{ billboardData.billboardMaterial}
                        }
                    }
                });
            }

            // TODO: Handle runtime prefabs here, currently only setting them up properly
            //       in the editor class by saving to assets after this method.
            return resultPreset;
        }

        public static (Texture2D billboardTexture, Material billboardMaterial, Mesh billboardMesh) GenerateBillboard(ScatterItemPreset preset, GameObject source)
        {
            // TODO: Automatically determine ideal UV layout depending on the shape of the combined bounds.

            // Make sure the highest level of detail has valid renderables.
            if (preset.levelsOfDetail == null ||
                preset.levelsOfDetail[0].renderables == null ||
                preset.levelsOfDetail[0].renderables.Any(x =>
                        x.mesh == null ||
                        x.materials == null ||
                        x.materials.Any(x => x == null
                    )
                )
            )
            {
                return (null, null, null);
            }

            // +4 for up facing quad.
            var totalQuadCount = BILLBOARD_UPRIGHT_COUNT + 4;
            var vertCount = totalQuadCount * 4;
            var verts = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            // +6 for up facing quad.
            var tris = new int[totalQuadCount * 6];

            // Setup billboard texture ready to be rendered into.
            var renderTexFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
            var billboardTexture = new Texture2D(preset.billboardResolution, preset.billboardResolution, TextureFormat.RGBA32, 1, true)
            {
                name = $"{preset.name}_billboardTexture"
            };

            // Setup a camera to use for capturing each side of the billboard.
            var cam = Instantiate(Resources.Load<GameObject>(BILLBOARD_CAPTURE_CAMERA_RESOURCES_PATH)).GetComponentInChildren<Camera>(); //new GameObject("Scatter Preset Billboard Capture Camera").AddComponent<Camera>();
            cam.gameObject.hideFlags = HideFlags.DontSave;
            cam.orthographic = true;

            var renderTex = RenderTexture.GetTemporary(
                                Mathf.CeilToInt(preset.billboardResolution),
                                Mathf.CeilToInt(preset.billboardResolution),
                                0,
                                renderTexFormat
                            );

            // GL.Clear(true, true, Color.green);
            cam.targetTexture = renderTex;
            cam.Render();
            cam.targetTexture = null;
            RenderTexture.active = renderTex;
            billboardTexture.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0, false);
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(renderTex);

            Matrix4x4 prefabTransform = preset.entityPrefab != null ?
                Matrix4x4.TRS(preset.entityPrefab.transform.position + BILLBOARD_CAPTURE_POSITION, preset.entityPrefab.transform.rotation, preset.entityPrefab.transform.lossyScale) :
                Matrix4x4.TRS(BILLBOARD_CAPTURE_POSITION, quaternion.identity, Vector3.one);

            var renderableCount = preset.levelsOfDetail[0].renderables.Count;
            var centerOfMass = GetCenterOfMass(preset.levelsOfDetail[0].renderables);

            #region Upright Facing Quad

            // Combine all first LOD bounds.
            var bounds = preset.levelsOfDetail[0].renderables[0].mesh.bounds;
            for (int i = 0; i < renderableCount; i++)
            {
                bounds.Encapsulate(preset.levelsOfDetail[0].renderables[0].mesh.bounds);
            }

            // Set height based on the available width for each upright.
            var largestHorizontalExtent = math.max(bounds.extents.x, bounds.extents.z);
            var largestVerticalExtent = bounds.extents.y;
            var aspectRatio = largestVerticalExtent / largestHorizontalExtent;
            var uprightUvWidth = 1f / BILLBOARD_UPRIGHT_COUNT;
            var uprightUvHeight = (aspectRatio * uprightUvWidth);

            // TODO: Also check if we can fit another row of uprights in at a bigger size.
            if (uprightUvHeight > 0.75f)
            {
                // Rescale so we don't clip through the up facing quad's uvs.
                var scaleReductionFactor = 1f - ((uprightUvHeight - 0.75f) / uprightUvHeight);
                uprightUvWidth *= scaleReductionFactor;
                uprightUvHeight *= scaleReductionFactor;
            }

            var uprightPixelWidth = uprightUvWidth * (float)preset.billboardResolution;

            // Generate flat up facing quad.
            var upFacingHeight = centerOfMass.y;// + bounds.extents.y;
            verts[0] = new Vector3(-largestHorizontalExtent, upFacingHeight, -largestHorizontalExtent); // Bottom left (top down).
            uvs[0] = new Vector2(0f, 0.75f);
            verts[1] = new Vector3(-largestHorizontalExtent, upFacingHeight, largestHorizontalExtent); // Top left (top down).
            uvs[1] = new Vector2(0f, 1f);
            verts[2] = new Vector3(largestHorizontalExtent, upFacingHeight, largestHorizontalExtent); // Top right (top down).
            uvs[2] = new Vector2(0.25f, 1f);
            verts[3] = new Vector3(largestHorizontalExtent, upFacingHeight, -largestHorizontalExtent); // Bottom right (top down).
            uvs[3] = new Vector2(0.25f, 0.75f);

            // Work out where to render into the billboard texture.
            var pixelDimensionsInBillboard = new Vector2(
                Mathf.FloorToInt((float)preset.billboardResolution * (uvs[2].x - uvs[0].x)),
                Mathf.FloorToInt((float)preset.billboardResolution * (uvs[2].y - uvs[0].y))
            );

            var pixelRectInBillboard = new Rect(0, preset.billboardResolution - pixelDimensionsInBillboard.y, pixelDimensionsInBillboard.x, pixelDimensionsInBillboard.y);
            renderTex = RenderTexture.GetTemporary(
                            Mathf.FloorToInt(pixelDimensionsInBillboard.x),
                            Mathf.FloorToInt(pixelDimensionsInBillboard.y),
                            0,
                            renderTexFormat
                        );

            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.transform.position = BILLBOARD_CAPTURE_POSITION + new Vector3(0, bounds.extents.y + 6, 0);
            cam.orthographicSize = largestHorizontalExtent;
            cam.aspect = 1f;
            cam.nearClipPlane = 5;
            cam.farClipPlane = bounds.extents.y + 11;

            // Redraw highest quality lod. Seems to be necessary after every camera move.
            DrawHighestQualityLod(preset, prefabTransform, cam);

            // Render into the render texture.
            // TODO: Also render normals pass into a billboard normals texture.
            cam.targetTexture = renderTex;
            cam.Render();
            RenderTexture.active = renderTex;

            // Read the render texture into the preset billboard texture at the UV coords for this quad.
            billboardTexture.ReadPixels(
                new Rect(0, 0, renderTex.width, renderTex.height),
                math.max(0, Mathf.FloorToInt(pixelRectInBillboard.position.x)),
                math.max(0, Mathf.FloorToInt(pixelRectInBillboard.position.y))
            );

            // Clear render texture.
            RenderTexture.active = null;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(renderTex);

            // Set all normals facing up.
            var norm = Vector3.up;
            normals[0] = norm;
            normals[1] = norm;
            normals[2] = norm;
            normals[3] = norm;

            // 1st triangle.
            tris[0] = 0; // Bottom left.
            tris[1] = 1; // Top left.
            tris[2] = 2; // Top right.

            // 2nd triangle.
            tris[3] = 2; // Top right.
            tris[4] = 3; // Bottom right.
            tris[5] = 0; // Bottom left.

            var vertIndexOffset = 4;
            var triIndexOffset = 6;

            #endregion

            #region Upright Geometry

            // Generate upright star geometry.
            for (int i = 0; i < BILLBOARD_UPRIGHT_COUNT; i++)
            {
                var rotY = ((float)i / BILLBOARD_UPRIGHT_COUNT) * 360f;
                var rot = Quaternion.Euler(0, rotY, 0f);
                var rightDir = rot * Vector3.right;

                // Set all normals facing local backwards.
                norm = rot * Vector3.back;
                normals[vertIndexOffset] = norm;
                normals[vertIndexOffset + 1] = norm;
                normals[vertIndexOffset + 2] = norm;
                normals[vertIndexOffset + 3] = norm;
                var topOffset = Vector3.up * bounds.extents.y * 2f;

                // Bottom left.
                var pos = -rightDir * largestHorizontalExtent;
                verts[vertIndexOffset] = pos;

                // Top left.
                pos = -rightDir * largestHorizontalExtent;
                pos += topOffset;
                verts[vertIndexOffset + 1] = pos;

                // Top right.
                pos = rightDir * largestHorizontalExtent;
                pos += topOffset;
                verts[vertIndexOffset + 2] = pos;

                // Bottom right.
                pos = rightDir * largestHorizontalExtent;
                verts[vertIndexOffset + 3] = pos;

                // 1st triangle.
                tris[triIndexOffset] = vertIndexOffset; // Bottom left.
                tris[triIndexOffset + 1] = vertIndexOffset + 1; // Top left.
                tris[triIndexOffset + 2] = vertIndexOffset + 2; // Top right.

                // 2nd triangle.
                tris[triIndexOffset + 3] = vertIndexOffset + 2; // Top right.
                tris[triIndexOffset + 4] = vertIndexOffset + 3; // Bottom right.
                tris[triIndexOffset + 5] = vertIndexOffset; // Bottom left.

                // Set uvs.
                var uvXMin = uprightUvWidth * i; // Normalised bottom left X in UV space.
                var uvXMax = uvXMin + uprightUvWidth;
                uvs[vertIndexOffset] = new Vector2(uvXMin, 0); // Bottom left.
                uvs[vertIndexOffset + 1] = new Vector2(uvXMin, uprightUvHeight); // Top left.
                uvs[vertIndexOffset + 2] = new Vector2(uvXMax, uprightUvHeight); // Top right.
                uvs[vertIndexOffset + 3] = new Vector2(uvXMax, 0); // Bottom left.

                // Render portion of billboard texture for this quad.
                pixelDimensionsInBillboard = new Vector2(
                    Mathf.FloorToInt((float)preset.billboardResolution * (uvs[vertIndexOffset + 2].x - uvs[vertIndexOffset].x)),
                    Mathf.FloorToInt((float)preset.billboardResolution * (uvs[vertIndexOffset + 2].y - uvs[vertIndexOffset].y))
                );

                var xPixelMin = math.max(0, uprightPixelWidth * i);
                pixelRectInBillboard = new Rect(xPixelMin, 0, pixelDimensionsInBillboard.x, pixelDimensionsInBillboard.y);
                renderTex = RenderTexture.GetTemporary(
                                Mathf.FloorToInt(pixelRectInBillboard.width),
                                Mathf.FloorToInt(pixelRectInBillboard.height),
                                0,
                                renderTexFormat
                            );

                cam.transform.rotation = rot;
                cam.transform.position =
                    BILLBOARD_CAPTURE_POSITION +
                    (norm * (largestHorizontalExtent + 6)) + // Move back in normal direction.
                    bounds.center; // Center on object.

                cam.orthographicSize = math.max(largestHorizontalExtent, largestVerticalExtent);// * 0.5f;// largestExtent * 0.5f;
                cam.aspect = pixelDimensionsInBillboard.x / pixelDimensionsInBillboard.y;
                cam.nearClipPlane = 5;
                cam.farClipPlane = (largestHorizontalExtent * 2) + 8;

                // Redraw highest lod. Seems to be necessary after every camera move.
                DrawHighestQualityLod(preset, prefabTransform, cam);

                // Render into the render texture.
                cam.targetTexture = renderTex;
                cam.Render();
                RenderTexture.active = renderTex;

                // Read the render texture into the preset billboard texture at the UV coords for this quad.
                billboardTexture.ReadPixels(
                    source: new Rect(0, 0, renderTex.width, renderTex.height),
                    destX: math.max(0, Mathf.FloorToInt(pixelRectInBillboard.position.x)),
                    destY: math.max(0, Mathf.FloorToInt(pixelRectInBillboard.position.y)),
                    recalculateMipMaps: i == BILLBOARD_UPRIGHT_COUNT - 1 // Calculate mip maps on the last capture.
                );
                RenderTexture.active = null;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTex);

                // Increment for the next loop.
                vertIndexOffset += 4;
                triIndexOffset += 6;
            }

            billboardTexture.Apply();

            #endregion

            var billboardMesh = new Mesh();
            billboardMesh.name = $"{preset.name}_billboardMesh";
            billboardMesh.SetVertices(verts);
            billboardMesh.SetNormals(normals);
            billboardMesh.SetUVs(0, uvs);
            billboardMesh.SetTriangles(tris, 0);
            billboardMesh.UploadMeshData(false);

            var billboardMaterial = CoreUtils.CreateEngineMaterial(ShaderConstants.BILLBOARD_SHADER_NAME);
            billboardMaterial.enableInstancing = true;
            billboardMaterial.SetTexture(ShaderConstants.BILLBOARD_TEXTURE_PROP, billboardTexture);

#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                Destroy(cam.gameObject);
            }
            else
            {
                DestroyImmediate(cam.gameObject, false);
            }
#else
                Destroy(cam.gameObject);
#endif
            return (billboardTexture, billboardMaterial, billboardMesh);
        }

        /// <summary>
        /// Draw all meshes in the closest level of detail using Graphics.DrawMesh.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="prefabTransform"></param>
        /// <param name="cam"></param>
        private static void DrawHighestQualityLod(ScatterItemPreset preset, Matrix4x4 prefabTransform, Camera cam)
        {
            foreach (var renderable in preset.levelsOfDetail[0].renderables)
            {
                var mats = renderable.materials;
                // Draw this preset for the capture camera.
                for (int j = 0; j < mats.Length; j++)
                {
                    Graphics.DrawMesh(renderable.mesh, prefabTransform, mats[j], 0, cam, j);
                }
            }
        }

        public static Vector3 GetCenterOfMass(List<ScatterRenderable> renderables)
        {
            float largetsDistSqr = 0f;
            var length = renderables.Sum(x => x.mesh.vertices.Length); ;
            var total = Vector3.zero;

            // Find the avergage position of all renderable vertices.
            for (int i = 0; i < renderables.Count; i++)
            {
                var verts = renderables[i].mesh.vertices;

                for (int j = 1; j < verts.Length; j++)
                {
                    var vert = verts[j];
                    total += vert;

                    largetsDistSqr = math.max(largetsDistSqr, math.lengthsq(vert));
                }
            }

            return new Vector3(total.x / length, total.y / length, total.z / length);
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(ScatterItemPreset))]
    public class ScatterItemPresetEditor : Editor
    {
        GameObject selectedGameobject = null;
        private bool creatingFromAsset = false;

        public override void OnInspectorGUI()
        {
            var preset = target as ScatterItemPreset;

            GUILayout.Space(20);

            // Draw thumbnail centered.
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                // Cheeky trick using an empty label to get a rect for DrawPreviewTexture
                // that plays nicely with EditorGUiLayout auto layout adjustments.
                EditorGUILayout.LabelField("", GUILayout.Height(256), GUILayout.Width(256));
                if (preset.thumbnail != null)
                {
                    EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetLastRect(), preset.thumbnail);
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(20);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                selectedGameobject = (GameObject)EditorGUILayout.ObjectField("Generate From Prefab", selectedGameobject, typeof(GameObject), false);
                var wasGuiEnabled = GUI.enabled;
                GUI.enabled = !creatingFromAsset && selectedGameobject != null;

                if (GUILayout.Button($"Import", EditorStyles.miniButtonRight, GUILayout.Width(50)) &&
                    EditorUtility.DisplayDialog(
                        title: $"Import from {selectedGameobject.name}'s hierarchy?",
                        message: "This will replace the contents of this preset " +
                                "with values generated by traversing the hierarchy of the selected object.",
                        ok: "Yes",
                        cancel: "Oops, no thanks")
                    )
                {
                    CreateFromPrefabButton_Handler(preset);
                }

                GUI.enabled = wasGuiEnabled;
            }

            GUILayout.Space(20);

            // Draw billboard texture.
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope())
                {
                    // Cheeky trick using an empty label to get a rect for DrawPreviewTexture
                    // that plays nicely with EditorGUiLayout auto layout adjustments.
                    EditorGUILayout.LabelField("", GUILayout.Height(256), GUILayout.Width(256));
                    
                    if (preset.billboardTexture != null)
                    {
                        EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetLastRect(), preset.billboardTexture);
                    }

                    if (GUILayout.Button("GenerateBillboard", GUILayout.Height(25)) && preset.entityPrefab != null)
                    {
                        GenerateBillboard(preset);
                    }
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(20);
            base.OnInspectorGUI();
        }

        private void GenerateBillboard(ScatterItemPreset preset)
        {
            bool[] lodsUsingBillboardData = new bool[preset.levelsOfDetail.Count];
            var prefabRenderersUsingBillboardData = new List<MeshRenderer>();

            // Keep track of what's referencing billboard data so we can swap it out once generated.
            for (int i = 0; i < preset.levelsOfDetail.Count; i++)
            {
                lodsUsingBillboardData[i] = preset.levelsOfDetail[i].renderables[0].mesh == preset.billboardMesh;
            }

            if (preset.entityPrefab != null)
            {
                prefabRenderersUsingBillboardData = preset.entityPrefab.GetComponentsInChildren<MeshRenderer>().Where(x => x.sharedMaterials[0] == preset.billboardMaterial).ToList();
            }

            var presetAssetPath = AssetDatabase.GetAssetPath(preset);
            var assetsAtPath = AssetDatabase.LoadAllAssetRepresentationsAtPath(presetAssetPath);

            // Remove any existing billboard sub-assets.
            foreach (var subAsset in assetsAtPath)
            {
                if (subAsset != preset)
                {
                    AssetDatabase.RemoveObjectFromAsset(subAsset);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var billboardData = ScatterItemPreset.GenerateBillboard(preset, preset.entityPrefab);
            preset.billboardMesh = billboardData.billboardMesh;
            preset.billboardTexture = billboardData.billboardTexture;
            preset.billboardMaterial = billboardData.billboardMaterial;
            preset.billboardMaterial.hideFlags = HideFlags.None;

            EditorUtility.SetDirty(preset);

            // Swap out billboard data in renderables.
            for (int i = 0; i < lodsUsingBillboardData.Length; i++)
            {
                if (lodsUsingBillboardData[i])
                {
                    var renderable = preset.levelsOfDetail[i].renderables[0];
                    renderable.mesh = preset.billboardMesh;
                    renderable.materials = new Material[] { preset.billboardMaterial };
                    preset.levelsOfDetail[i].renderables[0] = renderable;
                }
            }

            if (preset.entityPrefab != null)
            {
                // Swap out billboard data in the entity prefab.
                foreach (var rend in prefabRenderersUsingBillboardData)
                {
                    var meshFilter = rend.GetComponent<MeshFilter>();
                    if (meshFilter)
                    {
                        meshFilter.sharedMesh = preset.billboardMesh;
                        rend.sharedMaterials = new Material[] { preset.billboardMaterial };
                    }
                }
                EditorUtility.SetDirty(preset.entityPrefab);
            }

            // Assign sub assets and refresh the asset database.
            AssetDatabase.AddObjectToAsset(preset.billboardTexture, preset);
            AssetDatabase.AddObjectToAsset(preset.billboardMesh, preset);
            AssetDatabase.AddObjectToAsset(preset.billboardMaterial, preset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private void CreateFromPrefabButton_Handler(ScatterItemPreset preset)
        {
            creatingFromAsset = true;
            var newPreset = ScatterItemPreset.CreateFromPrefab(selectedGameobject, preset.billboardMode);
            var presetAssetPath = AssetDatabase.GetAssetPath(preset);
            var assetsAtPath = AssetDatabase.LoadAllAssetRepresentationsAtPath(presetAssetPath);

            // Remove any existing billboard data.
            foreach (var subAsset in assetsAtPath)
            {
                if (subAsset != preset)
                {
                    AssetDatabase.RemoveObjectFromAsset(subAsset);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            preset.entityPrefab = newPreset.entityPrefab;
            preset.positionOffset = newPreset.positionOffset;
            preset.rotationOffset = newPreset.rotationOffset;
            preset.scaleMultiplier = newPreset.scaleMultiplier;
            preset.levelsOfDetail = newPreset.levelsOfDetail;
            preset.billboardTexture = newPreset.billboardTexture;
            preset.billboardMesh = newPreset.billboardMesh;
            preset.billboardMaterial = newPreset.billboardMaterial;
            EditorUtility.SetDirty(preset);

            // Assign sub assets so we can serialise them.
            AssetDatabase.AddObjectToAsset(preset.billboardTexture, preset);
            AssetDatabase.AddObjectToAsset(preset.billboardMesh, preset);
            preset.billboardMaterial.hideFlags = HideFlags.None;
            AssetDatabase.AddObjectToAsset(preset.billboardMaterial, preset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Spawn a copy of the gameobject so we can save it as our own prefab.
            var spawnedPrefab = Instantiate(newPreset.entityPrefab);
            if (preset.billboardMode == BillboardMode.GeneratedBillboard)
            {
                var billboardRend = new GameObject("Billboard").AddComponent<MeshRenderer>();
                billboardRend.material = preset.billboardMaterial;
                billboardRend.gameObject.AddComponent<MeshFilter>().sharedMesh = preset.billboardMesh;

                // TODO: Setup correct draw distances here.
                var lodGroup = spawnedPrefab.GetComponent<LODGroup>();
                if (lodGroup == null)
                {
                    // If no lod group setup yet, add all existing renderers
                    // in the prefab as the closest LOD and the billboard as farthest.
                    lodGroup = spawnedPrefab.AddComponent<LODGroup>();
                    var renderers = spawnedPrefab.GetComponentsInChildren<MeshRenderer>();
                    billboardRend.transform.SetParent(spawnedPrefab.transform, false);
                    var lods = new LOD[2];
                    lods[0].renderers = renderers;
                    lods[0].screenRelativeTransitionHeight = 0.03f;
                    lods[1].renderers = new Renderer[] { billboardRend };
                    lods[1].screenRelativeTransitionHeight = 0.0001f;
                    lodGroup.SetLODs(lods);
                }
                else
                {
                    billboardRend.transform.SetParent(spawnedPrefab.transform, false);
                    var lods = new LOD[lodGroup.lodCount + 1];
                    lods[lods.Length - 1].renderers = new Renderer[] { billboardRend };
                    lods[lods.Length - 1].screenRelativeTransitionHeight = 0.0001f;
                    lodGroup.SetLODs(lods);
                }

            }

            var assetPath = AssetDatabase.GetAssetPath(preset);

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                // Save the prefab and assign it.
                preset.entityPrefab = PrefabUtility.SaveAsPrefabAsset(spawnedPrefab, $"{Path.GetDirectoryName(assetPath)}/{Path.GetFileNameWithoutExtension(assetPath)}_ScatterPrefab.prefab");
            }

            DestroyImmediate(spawnedPrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            creatingFromAsset = false;
        }
    }
#endif
}
