using UnityEngine;
using UnityEditor;
using Skylotus.Core.UI;

namespace Skylotus
{
    public class ColorPaletteWindow : EditorWindow
    {
        private ColorPalette _palette;
        private Vector2 _scroll;
        private const float SwatchSize = 75f;
        private const float Padding = 20f;

        // Field names matching your ScriptableObject, grouped by header
        private static readonly (string header, string[] fields)[] Groups =
        {
            ("Brand Colors", new[] { "primary", "secondary", "tertiary" }),
            ("Text",         new[] { "textPrimary", "textSecondary" }),
            ("Background",   new[] { "background", "accent" }),
        };

        [MenuItem("Skylotus/Color Palette Viewer")]
        public static void Open()
        {
            var win = GetWindow<ColorPaletteWindow>("Color Palette");
            win.minSize = new Vector2(280, 200);
        }

        private void OnGUI()
        {
            // --- margin wrapper ---
            EditorGUILayout.BeginVertical(new GUIStyle { padding = new RectOffset(20, 20, 6, 6) });

            // --- palette slot ---
            EditorGUILayout.Space(4);
            _palette = (ColorPalette)EditorGUILayout.ObjectField(
                "Palette", _palette, typeof(ColorPalette), false);

            if (_palette == null)
            {
                EditorGUILayout.HelpBox(
                    "Drag a ColorPalette asset into the slot above.", MessageType.Info);
                EditorGUILayout.EndVertical(); // margin wrapper
                return;
            }

            var so = new SerializedObject(_palette);
            so.Update();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            foreach (var (header, fields) in Groups)
            {
                EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();

                foreach (var fieldName in fields)
                {
                    var prop = so.FindProperty(fieldName);
                    if (prop == null) continue;

                    EditorGUILayout.BeginVertical(GUILayout.Width(SwatchSize + Padding));

                    // clickable swatch
                    Rect rect = GUILayoutUtility.GetRect(
                        SwatchSize, SwatchSize,
                        GUILayout.Width(SwatchSize), GUILayout.Height(SwatchSize));

                    Color current = prop.colorValue;
                    EditorGUI.DrawRect(rect, current);

                    // thin border so light colors are visible
                    Handles.DrawSolidRectangleWithOutline(
                        rect, Color.clear, new Color(0, 0, 0, 0.4f));

                    // click to open color picker
                    if (Event.current.type == EventType.MouseDown
                        && rect.Contains(Event.current.mousePosition))
                    {
                        // The color picker writes straight into the SerializedProperty
                        ColorPickerBridge.Show(prop, so, $"Edit {fieldName}");
                        Event.current.Use();
                    }

                    // label
                    var style = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        wordWrap = true
                    };
                    EditorGUILayout.LabelField(
                        ObjectNames.NicifyVariableName(fieldName),
                        style, GUILayout.Width(SwatchSize));

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(8);
            }

            // hex readout
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Hex Values", EditorStyles.boldLabel);
            foreach (var (_, fields) in Groups)
            {
                foreach (var fieldName in fields)
                {
                    var prop = so.FindProperty(fieldName);
                    if (prop == null) continue;
                    string hex = ColorUtility.ToHtmlStringRGB(prop.colorValue);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        ObjectNames.NicifyVariableName(fieldName), GUILayout.Width(120));
                    EditorGUILayout.SelectableLabel(
                        $"#{hex}", EditorStyles.textField, GUILayout.Height(18));
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical(); // margin wrapper
            so.ApplyModifiedProperties();
        }

        // Repaint while color picker is open so swatch updates live
        private void OnInspectorUpdate() => Repaint();
    }

    /// Helper to open Unity's built-in color picker against a SerializedProperty.
    internal static class ColorPickerBridge
    {
        public static void Show(SerializedProperty prop, SerializedObject so, string title)
        {
            Color original = prop.colorValue;
            // EditorGUILayout.ColorField opens the picker, but we can also
            // use the internal ColorPicker directly via reflection, or just
            // rely on a simple approach: a small popup.
            ColorPickerPopup.Open(original, title, c =>
            {
                prop.colorValue = c;
                so.ApplyModifiedProperties();
            });
        }
    }

    /// Tiny utility window that wraps a ColorField so Unity's picker opens.
    internal class ColorPickerPopup : EditorWindow
    {
        private Color _color;
        private string _title;
        private System.Action<Color> _onChange;
        private bool _initialized;

        public static void Open(
            Color initial, string title, System.Action<Color> onChange)
        {
            var win = CreateInstance<ColorPickerPopup>();
            win._color = initial;
            win._title = title;
            win._onChange = onChange;
            win.titleContent = new GUIContent(title);
            win.ShowUtility();
            win.minSize = win.maxSize = new Vector2(220, 60);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUI.BeginChangeCheck();
            _color = EditorGUILayout.ColorField(
                new GUIContent(_title), _color,
                showEyedropper: true, showAlpha: true, hdr: false);

            if (EditorGUI.EndChangeCheck())
                _onChange?.Invoke(_color);

            if (GUILayout.Button("Done"))
                Close();
        }

        private void OnDestroy() => _onChange?.Invoke(_color);
    }
}