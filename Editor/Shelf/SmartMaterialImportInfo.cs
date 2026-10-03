using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    internal sealed class SmartMaterialImportInfo : ScriptableObject
    {
        public int format, width, height, layerCount;
        public bool mask;
        public string displayName, channels, savedBy, error;
        public Texture2D thumbnail;
    }

}
