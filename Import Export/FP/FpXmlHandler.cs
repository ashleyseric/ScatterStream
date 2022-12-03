using System.Xml;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using Unity.Mathematics;
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using Cysharp.Threading.Tasks;
using System.Threading;

namespace AshleySeric.ScatterStream.ImportExport
{
    public class FpXmlHandler : MonoBehaviour
    {
        public struct XmlTypeIdentifier : IEquatable<XmlTypeIdentifier>
        {
            public int index;
            public string name;
            public string description;

            public override bool Equals(object obj)
            {
                return obj is XmlTypeIdentifier identifier && Equals(identifier);
            }

            public bool Equals(XmlTypeIdentifier other)
            {
                return index == other.index &&
                       name == other.name &&
                       description == other.description;
            }

            public override int GetHashCode()
            {
                return base.GetHashCode();
            }

            public override string ToString()
            {
                return base.ToString();
            }

            public static bool operator ==(XmlTypeIdentifier left, XmlTypeIdentifier right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(XmlTypeIdentifier left, XmlTypeIdentifier right)
            {
                return !(left == right);
            }
        }

        [Space]
        public GameObject panelRoot;
        public TMP_InputField xmlFileInputField;
        public Button scanXmlButton;
        public Button importButton;
        public TMP_Text importButtonLabel;
        public RectTransform listItemContainer;
        public FpXmlListItem xmlListItemPrefab;
        public ScatterStream streamToEdit;

        private CancellationTokenSource importPendingTaskCancellation;

        private void Awake()
        {
            scanXmlButton.onClick.AddListener(ScanXmlFile);
        }

        public void OpenPanel(ScatterStream streamToEdit)
        {
            this.streamToEdit = streamToEdit;
            importPendingTaskCancellation?.Cancel();

            CleanupListItems();
            panelRoot.SetActive(true);

            if (!string.IsNullOrEmpty(xmlFileInputField.text))
            {
                ScanXmlFile();
            }
        }

        public void ClosePanel()
        {
            importPendingTaskCancellation?.Cancel();
            CleanupListItems();
            panelRoot.SetActive(false);
        }

        private void CleanupListItems()
        {
            var childCount = listItemContainer.childCount;

            for (int i = 0; i < childCount; i++)
            {
                Destroy(listItemContainer.GetChild(i).gameObject);
            }
        }

        private async void ScanXmlFile()
        {
            importPendingTaskCancellation?.Cancel();
            importPendingTaskCancellation = new CancellationTokenSource();
            CleanupListItems();
            
            if (streamToEdit == null)
            {
                return;
            }
            
            bool importButtonPressed = false;
            importButton.onClick.RemoveAllListeners();

            await ImportXML(
                stream: streamToEdit,
                filePath: xmlFileInputField.text,
                assignXmlImportRelationships: async (typeCounts) =>
                {
                    var res = new Dictionary<XmlTypeIdentifier, ScatterItemPreset>();
                    int totalItems = 0;

                    foreach (var typeCountKvp in typeCounts)
                    {
                        totalItems += typeCountKvp.Value;
                        res[typeCountKvp.Key] = streamToEdit.presets.Presets[0];
                        // Draw a list item showing the item name + count with a 
                        // dropdown to assign a scatter item preset from our stream.
                        var listItem = Instantiate(xmlListItemPrefab, listItemContainer);
                        listItem.Setup(
                            stream: streamToEdit,
                            xmlItemLabel: $"{(string.IsNullOrWhiteSpace(typeCountKvp.Key.name) ? typeCountKvp.Key.name + ":" : "")} {typeCountKvp.Key.description}:  {typeCountKvp.Value}",
                            selectedPreset: null,
                            onPresetSelected: (preset) =>
                            {
                                res[typeCountKvp.Key] = preset;
                            }
                        );
                    }

                    importButton.onClick.AddListener(() =>
                    {
                        importButtonPressed = true;
                    });

                    importButtonLabel.text = $"Import {totalItems} items";

                    // Wait until we've confirmed our preset selection by clicking import or this task has been cancelled.
                    await UniTask.WaitUntil(() => importPendingTaskCancellation.IsCancellationRequested || importButtonPressed == true);
                    CleanupListItems();
                    // Hand back the assigned preset associations to be imported.
                    return res;
                });
        }

        /// <summary>
        /// Scans the provided file collecting metadata, then passes 
        /// that to <param>assignXmlImportRelationships</param>, then
        /// waits until receiving a reply before executing the import.
        /// </summary>
        /// <param name="success"></param>
        /// <param name="stream"></param>
        /// <param name="filePath"></param>
        /// <param name="assignXmlImportRelationships"></param>
        /// <param name="maxBatchSize"></param>
        /// <returns></returns>
        public static async Task<(bool success, Exception error)> ImportXML(
            ScatterStream stream,
            string filePath,
            Func<Dictionary<XmlTypeIdentifier, int>, Task<Dictionary<XmlTypeIdentifier, ScatterItemPreset>>> assignXmlImportRelationships,
            int maxBatchSize = 10000)
        {
            var typeCounts = new Dictionary<XmlTypeIdentifier, int>();
            var xmlDoc = new XmlDocument();

            try
            {
                xmlDoc.Load(filePath);
            }
            catch (XmlException e)
            {
                return (false, new Exception($"Aborting XML import, the selected file is not a valid XML document: {e}"));
            }

            // Collect metadata for each node in the XML file.
            var docRootChildren = xmlDoc.DocumentElement.ChildNodes;

            // Fetch metadata about which scatter item types exist in
            // the xml file and how many of each there are.
            foreach (XmlNode forestObjectNode in docRootChildren)
            {
                var children = forestObjectNode.ChildNodes;

                foreach (XmlNode itemNode in children)
                {
                    var identifier = new XmlTypeIdentifier();
                    int.TryParse(itemNode.Attributes["index"]?.InnerText, out identifier.index);
                    identifier.name = itemNode.Attributes["name"]?.InnerText;
                    identifier.description = itemNode.Attributes["description"]?.InnerText;

                    if (!typeCounts.ContainsKey(identifier))
                    {
                        typeCounts.Add(identifier, 1);
                    }
                    else
                    {
                        typeCounts[identifier] = typeCounts[identifier] + 1;
                    }
                }
            }

            // Present the metadata back to the caller and request scatter item preset relationships to each XML item type.
            // Async task allows the user to make UI selections etc before returning resulting assignments.
            var streamPresetIndexRelationships = await assignXmlImportRelationships(typeCounts);
            var presetBoundsCache = new Dictionary<XmlTypeIdentifier, Vector3>();
            var batch = new Dictionary<XmlTypeIdentifier, ImportExportUtility.ImportBatchData>();
            int currentBatchSize = 0;

            async Task ProcessBatch()
            {
                await ImportExportUtility.ImportBatch(stream, batch.Values);
                batch.Clear();
                currentBatchSize = 0;
            }

            // Parse the document again, this time collecting matrices and importing them in batches.
            foreach (XmlNode forestObjectNode in docRootChildren)
            {
                var children = forestObjectNode.ChildNodes;

                foreach (XmlNode itemNode in children)
                {
                    if (currentBatchSize >= maxBatchSize)
                    {
                        await ProcessBatch();
                    }

                    // Generate a type identifier for this node.
                    var identifier = new XmlTypeIdentifier();
                    int.TryParse(itemNode.Attributes["index"]?.InnerText, out identifier.index);
                    identifier.name = itemNode.Attributes["name"]?.InnerText;
                    identifier.description = itemNode.Attributes["description"]?.InnerText;

                    var scale = new float3(1f, 1f, 1f);
                    var sizeAttribute = itemNode.Attributes["size"];
                    var foundSize = false;

                    // Prefer size attribute if included.
                    if (sizeAttribute != null)
                    {
                        Vector3 boundsSize = default;

                        if (!presetBoundsCache.ContainsKey(identifier))
                        {
                            boundsSize = streamPresetIndexRelationships[identifier].GetBounds().size;
                            presetBoundsCache.Add(identifier, boundsSize);
                        }
                        else
                        {
                            boundsSize = presetBoundsCache[identifier];
                        }

                        foundSize = TryParseSizeAttribute(sizeAttribute, boundsSize, out scale);
                    }

                    if (!foundSize)
                    {
                        var scaleAttribute = itemNode.Attributes["scale"];

                        if (scaleAttribute != null)
                        {
                            // Otherwise fall back to scale attribute.
                            TryParseScaleAttributeValue(scaleAttribute, out scale);
                        }
                    }

                    if (TryParseXmlAttributeValue(
                            itemNode.Attributes["position"],
                            itemNode.Attributes["rotation"],
                            scale,
                            out var matrix
                        ))
                    {
                        // Add this matrix assigned to this node's type identifier.
                        if (!batch.ContainsKey(identifier))
                        {
                            batch.Add(identifier, new ImportExportUtility.ImportBatchData
                            {
                                instances = new List<GenericInstancePlacementData>(),
                                preset = streamPresetIndexRelationships[identifier]
                            });
                        }
                        else
                        {
                            batch[identifier].instances.Add(
                                new GenericInstancePlacementData
                                {
                                    localToStream = matrix,
                                    colour = new float4(1f, 1f, 1f, 1f)
                                }
                            );
                        }

                        currentBatchSize++;
                    }
                }
            }

            if (currentBatchSize > 0)
            {
                await ProcessBatch();
            }

            var parent = stream.parentTransform;
            var camera = stream.camera;

            stream.EndStream();
            stream.StartStream(camera, parent);

            return (true, null);
        }

        private static bool TryParseXmlAttributeValue(XmlAttribute positionAttribute, XmlAttribute rotationAttribute, Vector3 scale, out float4x4 output)
        {
            output = float4x4.zero;

            if (positionAttribute == null ||
                rotationAttribute == null ||
                // TODO: Fix rotation importing. Need to apply preset specific transforms as well.
                !TryParsePositionAttributeValue(positionAttribute, out float3 pos))
            // || !TryParseRotationAttributeValue(rotationAttribute, out quaternion rot))
            {
                return false;
            }

            // Temp workaournd the above rotation parsing not working correctly.
            var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            output = float4x4.TRS(pos, rot, scale);
            return true;
        }

        /// <summary>
        /// Returns true if attribute was successfully parsed as a valid float3.
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        private static bool TryParsePositionAttributeValue(XmlAttribute attribute, out float3 position)
        {
            var innerString = attribute.InnerText;
            // Strip the [ ] off each end.
            innerString = innerString.Substring(1, innerString.Length - 2);
            string[] split = innerString.Split(',');
            position = float3.zero;

            if (split.Length != 3)
            {
                return false;
            }

            if (!float.TryParse(split[0], out position.x)) { return false; }
            if (!float.TryParse(split[1], out position.z)) { return false; }
            if (!float.TryParse(split[2], out position.y)) { return false; }

            return true;
        }

        /// <summary>
        /// Returns true if attribute was successfully parsed as a valid float3.
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="scale"></param>
        /// <returns></returns>
        private static bool TryParseScaleAttributeValue(XmlAttribute attribute, out float3 scale)
        {
            var innerString = attribute.InnerText;
            // Strip the [ ] of each end.
            innerString = innerString.Substring(1, innerString.Length - 2);
            string[] split = innerString.Split(',');
            scale = new float3(1f, 1f, 1f);

            return split.Length == 3 &&
                   !float.TryParse(split[0], out scale.x) &&
                   !float.TryParse(split[1], out scale.y) &&
                   !float.TryParse(split[2], out scale.z);
        }

        /// <summary>
        /// Parsed scale is re-mapped to fit within the presetBoundsSize while maintaining axis ratios.
        /// Returns true if attribute was successfully parsed as a valid float3.
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="presetBoundsSize">Size to limit the </param>
        /// <param name="scale"></param>
        /// <returns></returns>
        private static bool TryParseSizeAttribute(XmlAttribute attribute, float3 presetBoundsSize, out float3 scale)
        {
            var innerString = attribute.InnerText;
            // Strip the [ ] of each end.
            innerString = innerString.Substring(1, innerString.Length - 2).Replace(" ", "");
            string[] split = innerString.Split(',');
            scale = new float3(1f, 1f, 1f);

            if (split.Length != 2)
            {
                return false;
            }

            float2 size;

            if (!float.TryParse(split[0], out size.x) ||
                !float.TryParse(split[1], out size.y))
            {
                return false;
            }

            // Work out the relative scale needed to attain the target max width / height.
            var horizontalScaleMultiplier = size.x / Mathf.Max(presetBoundsSize.x, presetBoundsSize.z);
            var verticalScaleMultiplier = size.y / presetBoundsSize.y;
            scale = new float3(horizontalScaleMultiplier, verticalScaleMultiplier, horizontalScaleMultiplier);
            return true;
        }

        /// <summary>
        /// [WARNING] Non functional. Should work in theory but seems there might be some conversion step I'm missing.
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="rotation"></param>
        /// <returns></returns>
        private static bool TryParseRotationAttributeValue(XmlAttribute attribute, out quaternion rotation)
        {
            rotation = quaternion.identity;

            // Attempt to convert the rotation matrix in the XML file (Matrix3x4 with the last column always zeroed out).
            string attributeString = attribute.InnerText.Replace(" ", "");
            string[] attributeSplit = attributeString.Substring(1, attributeString.Length - 2).Split(new string[] { "][" }, System.StringSplitOptions.None);

            if (attributeSplit.Length < 3)
            {
                return false;
            }

            string[] vectorSplit1 = attributeSplit[0].Split(',');
            string[] vectorSplit2 = attributeSplit[1].Split(',');
            string[] vectorSplit3 = attributeSplit[2].Split(',');

            if (vectorSplit1.Length != 3 ||
                vectorSplit2.Length != 3 ||
                vectorSplit3.Length != 3 ||
                // First float3 in the matrix.
                !float.TryParse(vectorSplit1[0], out float m00) ||
                !float.TryParse(vectorSplit1[1], out float m01) ||
                !float.TryParse(vectorSplit1[2], out float m02) ||
                // Second float3 in the matrix.
                !float.TryParse(vectorSplit2[0], out float m10) ||
                !float.TryParse(vectorSplit2[1], out float m11) ||
                !float.TryParse(vectorSplit2[2], out float m12) ||
                // Third float3 in the matrix.
                !float.TryParse(vectorSplit3[0], out float m20) ||
                !float.TryParse(vectorSplit3[1], out float m21) ||
                !float.TryParse(vectorSplit3[2], out float m22))
            {
                return false;
            }

            rotation = RotationMatrixToQuaternion(
                m00, m10, m20,
                m01, m11, m21,
                m02, m12, m22
            );

            return true;
        }

        /// <summary>
        /// Converts a rotation matrix to a <see cref="Quaternion"/>
        /// </summary>
        /// <returns></returns>
        private static Quaternion RotationMatrixToQuaternion
           (
               float m00, float m01, float m02,
               float m10, float m11, float m12,
               float m20, float m21, float m22
           )
        {
            // Logic source: http://www.euclideanspace.com/maths/geometry/rotations/conversions/matrixToQuaternion/
            float qx, qy, qz, qw;
            float tr = m00 + m11 + m22;

            if (tr > 0)
            {
                float S = Mathf.Sqrt(tr + 1.0f) * 2; // S=4*qw
                qw = 0.25f * S;
                qx = (m21 - m12) / S;
                qy = (m02 - m20) / S;
                qz = (m10 - m01) / S;
            }
            else if ((m00 > m11) & (m00 > m22))
            {
                float S = Mathf.Sqrt(1.0f + m00 - m11 - m22) * 2; // S=4*qx 
                qw = (m21 - m12) / S;
                qx = 0.25f * S;
                qy = (m01 + m10) / S;
                qz = (m02 + m20) / S;
            }
            else if (m11 > m22)
            {
                float S = Mathf.Sqrt(1.0f + m11 - m00 - m22) * 2; // S=4*qy
                qw = (m02 - m20) / S;
                qx = (m01 + m10) / S;
                qy = 0.25f * S;
                qz = (m12 + m21) / S;
            }
            else
            {
                float S = Mathf.Sqrt(1.0f + m22 - m00 - m11) * 2; // S=4*qz
                qw = (m10 - m01) / S;
                qx = (m02 + m20) / S;
                qy = (m12 + m21) / S;
                qz = 0.25f * S;
            }

            return new Quaternion(qx, qy, qz, qw);
        }
    }
}