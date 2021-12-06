/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace AshleySeric.ScatterStream
{
    /// <summary>
    /// UI for managing and editing streams.
    /// </summary>
    public class ScatterStreamInterface : MonoBehaviour
    {
        private const string BRUSH_NORMAL_SHADER_GLOBAL_NAME = "_BrushNormal";

        public new Camera camera;
        public Transform parentTransform;
        public ScatterStream[] streams;
        [Header("Brush")]
        public Transform brushCursorPrefab;
        public Slider brushDiameterSlider;
        public Slider brushSpacingSlider;
        [Header("Streams")]
        public Transform streamListItemContainer;
        public StreamListItem streamListItemPrefab;
        [Header("Preset Thumbnails")]
        public Transform itemThumbnailContainer;
        public ScatterPresetThumbnail thumbnailPrefab;

        private Transform previousParentTransform = null;
        private Transform brushCursor;
        private DecalProjector cursorProjector;
        private ScatterStream editingStream;
        private Dictionary<ScatterItemPreset, ScatterPresetThumbnail> presetThumbnails = new Dictionary<ScatterItemPreset, ScatterPresetThumbnail>();
        private Dictionary<ScatterStream, StreamListItem> streamListItems = new Dictionary<ScatterStream, StreamListItem>();

        public bool DrawDebugs
        {
            set
            {
                TileDrawInstanced.drawDebugs = value;
            }
            get
            {
                return TileDrawInstanced.drawDebugs;
            }
        }

        private void Awake()
        {
            // Instantiate brush prefab.
            brushCursor = Instantiate(brushCursorPrefab);
            cursorProjector = brushCursor.GetComponentInChildren<DecalProjector>();

            // TODO: Better handling of stream initialisation so they can be swapped at runtime and maybe run in the editor.
            foreach (var stream in streams)
            {
                stream.CreateEntityPrefabsForAuthoring();
            }

            editingStream = streams.Length > 0 ? streams[0] : null;
            ReloadStreamListItems();
            OnStreamSelected_Handler(editingStream);
        }

        private void OnEnable()
        {
            foreach (var stream in streams)
            {
                stream.StartStream(camera, parentTransform);
            }

            if (streams.Length > 0)
            {
                editingStream = streams[0];
                ScatterStream.EditingStream = editingStream;
            }

            brushDiameterSlider?.onValueChanged.AddListener(BrushDiameterSlider_ChangeHandler);
            brushSpacingSlider?.onValueChanged.AddListener(BrushSpacingSlider_ChangeHandler);
        }

        private void OnDisable()
        {
            foreach (var stream in streams)
            {
                stream.EndStream();
            }

            editingStream.EndStream();
            brushDiameterSlider?.onValueChanged.RemoveListener(BrushDiameterSlider_ChangeHandler);
            brushSpacingSlider?.onValueChanged.RemoveListener(BrushSpacingSlider_ChangeHandler);
        }

        private void Update()
        {
            if (previousParentTransform != parentTransform)
            {
                // Update any active streams with the new parent transform.
                foreach (var stream in ScatterStream.ActiveStreams)
                {
                    stream.Value.parentTransform = parentTransform;
                }
                previousParentTransform = parentTransform;
            }
        }

        private void LateUpdate()
        {
            bool isPointerOverUI = UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            bool isEitherShiftKeyHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool isEitherControlKeyHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            // Let the ECS systems know which stream to edit.
            ScatterStream.EditingStream = editingStream;

            if (ScatterStream.EditingStream != null)
            {
                // Ensure sliders are up to date.
                brushSpacingSlider.SetValueWithoutNotify(editingStream.brushConfig.spacing);
                brushDiameterSlider.SetValueWithoutNotify(editingStream.brushConfig.diameter);

                // Don't register brush shortcut inputs if the cursor is over any UI elements.
                if (!isPointerOverUI)
                {
                    if (ScatterStream.EditingStream != null)
                    {
                        if (math.abs(Input.mouseScrollDelta.y) > 0.1f)
                        {
                            if (isEitherControlKeyHeld)
                            {
                                // Adjust spacing with scroll wheel.
                                ScatterStream.EditingStream.brushConfig.spacing = Mathf.Max(
                                    0.01f,
                                    ScatterStream.EditingStream.brushConfig.spacing - Input.mouseScrollDelta.y * Time.deltaTime * ScatterStream.EditingStream.brushConfig.spacing * 4f
                                );
                                brushSpacingSlider?.SetValueWithoutNotify(ScatterStream.EditingStream.brushConfig.spacing);
                            }
                            else if (!isEitherShiftKeyHeld)
                            {
                                // Adjust diameter with scroll wheel.
                                ScatterStream.EditingStream.brushConfig.diameter = Mathf.Max(
                                    0.01f,
                                    ScatterStream.EditingStream.brushConfig.diameter + Input.mouseScrollDelta.y * Time.deltaTime * ScatterStream.EditingStream.brushConfig.diameter * 4f
                                );
                                brushDiameterSlider?.SetValueWithoutNotify(ScatterStream.EditingStream.brushConfig.diameter);
                            }
                        }
                    }
                }
            }

            // Update cursor to match brush state.
            brushCursor.gameObject.SetActive(!isEitherShiftKeyHeld && Painter.didBrushHitSurface && !isPointerOverUI);
            brushCursor.position = Painter.brushPosition;
            brushCursor.up = Painter.brushNormal;
            brushCursor.localScale = editingStream.brushConfig.diameter * Vector3.one;

            if (cursorProjector != null)
            {
                cursorProjector.size = Vector3.one;
                // Always 0.5 height offset due to the parent being scaled with brush diameter.
                cursorProjector.transform.localPosition = new Vector3(0, 0.2f, 0);
                cursorProjector.size = new Vector3(editingStream.brushConfig.diameter, editingStream.brushConfig.diameter, editingStream.brushConfig.diameter * 1.25f);
            }

            // Tell the brush shader it's normal direction.
            Shader.SetGlobalVector(BRUSH_NORMAL_SHADER_GLOBAL_NAME, (Vector3)Painter.brushNormal);
        }

        private void OnStreamSelected_Handler(ScatterStream stream)
        {
            // Swap the editing stream over.
            editingStream = stream;
            ReloadPresetThumbnails(stream);

            // Refresh brush sliders.
            brushDiameterSlider?.SetValueWithoutNotify(stream.brushConfig.diameter);
            brushSpacingSlider?.SetValueWithoutNotify(stream.brushConfig.spacing);

            // Select the Stream item in the list.
            presetThumbnails[stream.presets.Presets[0]].button.onClick?.Invoke();
        }

        public void ReloadStreamListItems()
        {
            // Remove all existing thumbnails.
            foreach (var keyValue in streamListItems)
            {
                GameObject.Destroy(keyValue.Value.gameObject);
            }
            streamListItems.Clear();

            for (int i = 0; i < streams.Length; i++)
            {
                var stream = streams[i];
                var thumbGo = Instantiate(streamListItemPrefab, streamListItemContainer);
                thumbGo.hideFlags = HideFlags.DontSave;
                var listItem = thumbGo.GetComponent<StreamListItem>();

                int iCaptured = i;
                listItem.button.onClick.AddListener(() => OnStreamSelected_Handler(stream));
                listItem.label.text = stream.name;
                streamListItems.Add(stream, listItem);
            }
        }

        public void ReloadPresetThumbnails(ScatterStream stream)
        {
            // Remove all existing thumbnails.
            foreach (var keyValue in presetThumbnails)
            {
                GameObject.Destroy(keyValue.Value.gameObject);
            }
            presetThumbnails.Clear();

            for (int i = 0; i < stream.presets.Presets.Length; i++)
            {
                var preset = stream.presets.Presets[i];
                var thumbGo = Instantiate(thumbnailPrefab, itemThumbnailContainer);
                thumbGo.hideFlags = HideFlags.DontSave;
                var thumb = thumbGo.GetComponent<ScatterPresetThumbnail>();

                int iCaptured = i;
                thumb.button.onClick.AddListener(() => PresetThumbnailClicked_Handler(preset, iCaptured));
                thumb.label.text = preset.name;
                thumb.image.texture = preset.thumbnail;
                presetThumbnails.Add(preset, thumb);
            }
        }

        private void PresetThumbnailClicked_Handler(ScatterItemPreset preset, int indexInStreamPresets)
        {
            foreach (var keyValue in presetThumbnails)
            {
                keyValue.Value.button.interactable = true;
                keyValue.Value.selectedHighlight.gameObject.SetActive(false);
            }

            presetThumbnails[preset].button.interactable = false;
            presetThumbnails[preset].selectedHighlight.gameObject.SetActive(true);
            Painter.selectedPresetIndex = indexInStreamPresets;
        }

        private void BrushDiameterSlider_ChangeHandler(float value)
        {
            if (editingStream != null)
            {
                editingStream.brushConfig.diameter = value;
            }
        }

        private void BrushSpacingSlider_ChangeHandler(float value)
        {
            if (editingStream != null)
            {
                editingStream.brushConfig.spacing = value;
            }
        }
    }
}