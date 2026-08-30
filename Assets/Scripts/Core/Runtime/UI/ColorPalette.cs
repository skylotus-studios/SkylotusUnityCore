using UnityEngine;

namespace Skylotus.Core.UI
{
    [CreateAssetMenu(fileName = "ColorPalette", menuName = "Skylotus/UI/Color Palette")]
    public class ColorPalette : ScriptableObject
    {
        [Header("Brand Colors")]
        public Color primary = Color.white;
        public Color secondary = Color.white;
        public Color tertiary = Color.white;

        [Header("Text")]
        public Color textPrimary = Color.black;
        public Color textSecondary = Color.grey;

        [Header("Background")]
        public Color background = Color.white;
        public Color accent = Color.white;

        // Convenience swatch array — ordered to match the field declarations above.
        public Color[] Splotches => new Color[]
        {
            primary,
            secondary,
            tertiary,
            accent,
            textPrimary,
            textSecondary,
            background
        };
    }
}
