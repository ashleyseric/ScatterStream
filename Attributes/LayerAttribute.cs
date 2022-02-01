// This nifty attribute trick sourced from: https://answers.unity.com/questions/609385/type-for-layer-selection.html

using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;

[CustomPropertyDrawer(typeof(LayerAttribute))]
class LayerAttributeEditor : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        // Layer dropdown that only allows a single layer at a time.
        property.intValue = EditorGUI.LayerField(position, label, property.intValue);
        EditorGUI.EndProperty();
    }
}

#endif

/// <summary>
/// Attribute for selecting a single layer from a dropdown.
/// </summary>
public class LayerAttribute : PropertyAttribute { }