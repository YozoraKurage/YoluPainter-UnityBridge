using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        internal bool RemoveLibraryAsset(string relative)
        {
            RequireNoStrokeForResources();
            ResourceLibraryFolder.Resolve(PainterSettings.LibraryFolder, relative);
            if (!Dialogs.Confirm(L.Tr("Remove from My Library?"), L.Tr("Delete {0} from My Library? This affects every project using this folder. Copies in projects and placed layers stay unchanged. This cannot be undone.", relative), L.Tr("Delete"), L.Tr("Cancel"))) return false;
            ResourceLibraryFolder.Remove(PainterSettings.LibraryFolder, relative); RefreshAssetPanel(); selectedAsset = null; return true;
        }
        internal bool RenameLibraryAsset(string relative, string name)
        {
            RequireNoStrokeForResources();
            if (!Dialogs.Confirm(L.Tr("Rename in My Library?"), L.Tr("Rename {0} in My Library? Other projects keep their copies and their old source paths. This cannot be undone.", relative), L.Tr("Rename"), L.Tr("Cancel"))) return false;
            string next = ResourceLibraryFolder.Rename(PainterSettings.LibraryFolder, relative, name);
            RefreshAssetPanel(); selectedAsset = AssetKey(AssetSource.Library, next); return true;
        }
        void AssetContextMenu(string key, string name)
        {
            if (!TryParseAssetKey(key, out var source, out string id) || source != AssetSource.Library && source != AssetSource.Project) return;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L.Tr("Rename…")), false, () => AssetNameWindow.Open(name, next =>
            {
                if (this == null) return;
                if (source == AssetSource.Library) RenameLibraryAsset(id, next); else RenameResource(Guid.Parse(id), next);
            }));
            menu.AddItem(new GUIContent(L.Tr("Remove…")), false, () => TryAction(() =>
            {
                if (source == AssetSource.Library) RemoveLibraryAsset(id);
                else if (ImageResources.TryGetSmart(Guid.Parse(id), out _)) RemoveSmartResource(Guid.Parse(id));
                else RemoveResource(Guid.Parse(id));
            }));
            menu.ShowAsContext();
        }
    }
}
