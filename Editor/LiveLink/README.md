# Live Link

Live Link opens a model of the Unity scene in the standalone YoluPainter and brings the exported textures back to its
materials. The two programs do not stay connected: Unity puts a *request* (a JSON file) in a folder that both know, the
standalone picks it up, and when the user exports, the standalone puts a *reply* in the same folder. Only file paths and small
values travel; meshes and pixels do not (the standalone reads the FBX and texture files itself). The format of the files is
specified in the standalone's repository: [docs/LIVELINK.md](https://github.com/YozoraKurage/YoluPainter/blob/main/docs/LIVELINK.md).
This page describes what the Unity side does.

## Open in YoluPainter

Right-click a GameObject in the Hierarchy ▸ **Open in YoluPainter** (also under **GameObject**), or the button of
**YozoLab ▸ YoluPainter ▸ Live Link** (the window's target follows the selection). Pressing it again sends the same target again:
the standalone applies the pose, BlendShapes, material values and visibility to the document it already has open.

- The request lists the renderers under the target (`MeshRenderer` and `SkinnedMeshRenderer`, including disabled ones with their
  visibility). Only a renderer whose mesh comes from an `.fbx` is sent; the others are listed with a reason in the window and in the
  request (`mesh_not_from_fbx`). When nothing can be sent, no request is put.
- For each FBX placed under the target: its absolute path and GUID, and the importer settings the standalone needs to read it as Unity
  does (`globalScale`, `useFileScale`, `bakeAxisConversion`, `importBlendShapes`, `preserveHierarchy`). The same FBX placed twice is two
  entries.
- Nodes are named by their path inside the imported model (names joined by `/` from the model's root; Unity drops the single top node of
  an FBX when `preserveHierarchy` is off). A scene Transform is matched to its node in this order: the Prefab connection to the FBX
  (`GetCorrespondingObjectFromOriginalSource`); for an unpacked skinned mesh, the position in `SkinnedMeshRenderer.bones` (the same list of
  the FBX's renderer with the same mesh), so reparented bones still match; then by name under the Transform of the node's parent. A renderer
  whose node or bones cannot be matched is not sent (`bone_not_found`, or `ambiguous_bone` when siblings share the name).
- Every matched node gets its local value relative to the Transform of its parent in the FBX, whatever the scene hierarchy is. The root
  of an FBX placed under the target gets its value relative to the target; when the target is the FBX's root (or inside it), the root is
  not sent and the standalone keeps the FBX's own value (Unity moves the transform of a collapsed single top node to the Prefab root, so
  an identity there would lose it).
- Materials are sent once each, keyed by the asset (`guid:…/fileid:…`; a material that is not an asset by its `GlobalObjectId`, or by `instance:<InstanceID>` when that is empty, as in a preview or unsaved scene; an exported texture finds it again only in the editor session that sent the request): the
  shader's name, GUID, package name and version (empty for a shader in `Assets` or a built-in one), the enabled keywords, the render
  queue, every finite property value, and for each 2D texture property the absolute path of the image file with its GUID, the
  importer's sRGB and normal-map settings, and the tiling and offset. A texture that has no image file (a `RenderTexture`, a texture
  inside another asset, a generated one) is sent with a `null` path, its sRGB setting from its format and no normal-map flag. Nothing is
  read from the pixels and nothing is written to the material, texture or importer.
- `target.export_dir` is `<export folder>/<target name>`; the export folder is `Assets/YoluPainter` unless changed in
  **Edit ▸ Preferences ▸ YoluPainter**.

## The folder and starting the standalone

- The folder is `%LOCALAPPDATA%\YoluPainter\LiveLink` on Windows, `~/Library/Application Support/YoluPainter/LiveLink` on macOS and
  `${XDG_DATA_HOME:-~/.local/share}/YoluPainter/LiveLink` elsewhere, or the absolute path in the environment variable
  `YOLUPAINTER_LIVELINK_DIR`. Folders Unity creates there are made private (0700) on macOS and Linux. Files are written under a `.tmp`
  name and put in place in one step when complete (an existing file is replaced, never removed first).
- When the standalone's `presence.json` was updated less than 6 seconds ago, the request is only put. Otherwise the standalone is
  started with `--livelink` first (without waiting; it picks the request up once it runs). A standalone started in the last minute that
  is still running is not started again. An earlier request of this project for the same target that nobody picked up yet is removed.
- Where the standalone is: on Windows, the location the installer wrote (`InstallLocation` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YoluPainter`, the file `yolupainter.exe` in it); otherwise the path in
  Preferences. When neither is known, a file dialog asks once and the choice is kept in Preferences.

## Replies

- The `outbox` folder is read every second while the Live Link window is open and while this project has sent requests (an export can
  come long after the request, also after the editor was restarted). Only replies to this project's requests are read (the ids are kept
  in `Library/YoluPainter/LiveLink/requests.tsv`), and a reply is deleted once read. Replies are read in the order they were written (`<id>-<n>.json`, by the number `n`). Nothing is read during Play mode, compilation or an
  asset refresh.
- The window's dot is green while the standalone runs, amber while one started from here has not written `presence.json` yet, and
  grey otherwise; its tooltip also gives the last state (sent, opened, refused, exported or not sent) with the time. Below it the window
  lists the renderers Unity could not send, the reasons the standalone gave in `opened` and `refused`, and why Unity stopped (no
  renderer to send, the folder cannot be used, the request cannot be written, the standalone cannot be started, too large).
- `exported`: each PNG inside this project's `Assets` folder is imported and its importer is set to the sRGB and normal-map settings of
  the reply. A PNG outside `Assets` is not imported and is listed with the reason. Then a window lists what would change (material,
  property, previous texture → new texture): **Apply All**, **Apply Selected** or **Don't Apply**. Applying is one undo step and saves the
  assets. A material inside an FBX or a package cannot be changed and is listed with the reason.

## What it touches

Reading the scene, the FBX importers and the materials writes nothing. Writing happens only in the Live Link folder, in
`Library/YoluPainter/LiveLink`, when importing exported PNGs (their importer settings), and when the user applies textures to materials.
