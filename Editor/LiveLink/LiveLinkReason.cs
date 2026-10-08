namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>送れなかった・断られた理由の言葉（頼みの <c>refused[].reason</c>・返事の <c>problems[].reason</c>）と、その日英の文。
    /// 知らない言葉はそのまま見せる。</summary>
    internal static class LiveLinkReason
    {
        public const string MeshNotFromFbx = "mesh_not_from_fbx";
        public const string BoneNotFound = "bone_not_found";
        public const string AmbiguousBone = "ambiguous_bone";
        public const string UnsupportedImport = "unsupported_import";
        public const string FbxUnreadable = "fbx_unreadable";
        public const string TextureUnreadable = "texture_unreadable";
        public const string TooLarge = "too_large";
        public const string FormatUnknown = "format_unknown";
        public const string Busy = "busy";
        public const string Declined = "declined";

        public static readonly string[] All = { MeshNotFromFbx, BoneNotFound, AmbiguousBone, UnsupportedImport, FbxUnreadable, TextureUnreadable, TooLarge, FormatUnknown, Busy, Declined };

        // Unity の側だけの理由（書き出しを取り込む・当てるとき。受け渡しの言葉ではない）
        public const string OutsideAssets = "outside_assets";
        public const string NotImported = "not_imported";
        public const string MaterialNotFound = "material_not_found";
        public const string MaterialReadOnly = "material_read_only";
        public const string NoSuchProperty = "no_such_property";

        /// <summary>今の言語の短い文。</summary>
        public static string Text(string word)
        {
            switch (word)
            {
                case MeshNotFromFbx: return L.Tr("The mesh is not from an FBX");
                case BoneNotFound: return L.Tr("A bone is not in the FBX");
                case AmbiguousBone: return L.Tr("Bones with the same name");
                case UnsupportedImport: return L.Tr("Bake Axis Conversion is not supported");
                case FbxUnreadable: return L.Tr("The FBX cannot be read");
                case TextureUnreadable: return L.Tr("A texture cannot be read");
                case TooLarge: return L.Tr("Too large");
                case FormatUnknown: return L.Tr("Not understood by this YoluPainter");
                case Busy: return L.Tr("YoluPainter is busy");
                case Declined: return L.Tr("Opening was canceled in YoluPainter");
                case OutsideAssets: return L.Tr("Outside the Assets folder");
                case NotImported: return L.Tr("The file cannot be imported");
                case MaterialNotFound: return L.Tr("The material is not found");
                case MaterialReadOnly: return L.Tr("The material is read-only");
                case NoSuchProperty: return L.Tr("The material has no such property");
                default: return word ?? "";
            }
        }
    }
}
