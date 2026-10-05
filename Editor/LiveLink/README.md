# Live Link

Live Link shows the textures painted in the standalone YoluPainter on a model in the Unity scene, with its real materials
(lilToon, Standard, …). One action does it all: right-click a GameObject in the Hierarchy ▸ **Open in YoluPainter** (also under
**GameObject**), or the large button of **YozoLab ▸ YoluPainter ▸ Live Link** (it works on the selection).

## Open in YoluPainter

- Available when the selected object is in the scene (not a Prefab asset) and has an active `MeshRenderer` or `SkinnedMeshRenderer`
  with a mesh. Otherwise the menu item is greyed out, and the window's button says why in its tooltip.
- If Live Link is connected it sends the model. If not, it connects; if nobody answers on the link name, it starts the standalone
  with `--livelink` and the current link name in the environment variable `YOLUPAINTER_LINK_NAME` (so a changed link name is the one the
  standalone listens on), tries again until the standalone answers, then connects and sends the model. The wait is shown in a progress bar
  that can be cancelled, and it gives up 60 seconds after the connection was started (counted again from the start of the
  standalone), also when a peer accepts the connection but never answers.
- Whether a standalone is there is decided by trying to connect, not by looking for its key file. A standalone that answers on the
  link name, even to refuse, is never started a second time. When it refuses because another Unity is connected, nothing is started and
  Live Link waits up to 10 seconds (4 seconds for the automatic reconnection) for that connection to end. A refusal because of another
  version or key is shown at once, without waiting. A standalone that is running but not listening (Live Link turned off in its
  settings), or that fails during the handshake, does not answer, so one more is started with `--livelink`.
- A new unnamed project in the standalone that has nothing painted becomes one texture set per material of the received model; a
  project with painting is kept as it is.
- Where the standalone is: on Windows, the location the installer wrote (`InstallLocation` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YoluPainter`, the file `yolupainter.exe` in it); otherwise the path in
  **Edit ▸ Preferences ▸ YoluPainter** (also in the window's *Details*; quotation marks and spaces around a pasted path are ignored). When neither is known, a file dialog asks once and the choice is
  kept in Preferences. On Linux the path in Preferences is used.
- After a script recompile (domain reload) or entering or leaving Play mode, a connection that was showing a model is made again and the model is
  sent again (remembered in `SessionState`; the standalone is not started for this, and if it was closed nothing is shown). Disconnecting by hand
  or quitting the editor forgets it. When the standalone is closed, the temporary display is removed and the link is closed quietly.
- The window keeps the button and the state in front. *Details* holds the link name (default `yolupainter-livelink`), the
  executable, Connect / Disconnect, Send model, Stop showing, the bridge version, the model's materials, the log and the test pattern.

## What it touches

- It reads the renderers under the chosen GameObject (active `MeshRenderer` and `SkinnedMeshRenderer`): mesh positions,
  normals, UV0 and triangles per submesh, in the chosen GameObject's local space. Meshes are only read (non-readable meshes
  through the editor API; import settings are not changed). Skinned meshes are baked in their current pose into a mesh owned
  by Live Link. No GameObject is instantiated and nothing is written to the scene, materials, meshes or textures.
- Texture sets are per material (all submeshes that use the same `Material` share one set; submeshes without a material form
  one `Unassigned` group). Each material is sent with a key (name, and the asset GUID and local file ID when it is an asset),
  its shader name, its 2D texture properties and the channels Live Link can show.
- The painted textures are shown with `MaterialPropertyBlock`s on the scene's renderers (per material index). The renderer's
  own block is kept and restored when Live Link stops showing (Disconnect, Stop showing, closing the model, a domain reload,
  entering Play mode or quitting the editor). Property blocks are not saved with the scene.
- Which property receives which channel follows the 3D view's binding (`PreviewMaterialBindings`): verified lilToon versions
  and variants, the built-in Standard shader, otherwise the main texture. A channel whose property needs a shader keyword is
  shown only when the material already has that keyword on: a property block cannot turn keywords on, and Live Link does not
  change materials. Value channels are packed like the 3D view (`PreviewChannelPack.shader`).
- When a skinned mesh's pose or BlendShapes change, or a renderer moves, the new positions of those meshes are sent (checked
  every 0.2 s; nothing is sent while nothing changes). When the renderers, the meshes or the materials of the submeshes
  change, the model is sent again; when only a material's shader, keywords or textures change, only a material update is
  sent (the texture sets stay).

## Material values (lilToon)

When a material of the model is a verified lilToon material (the shader comes from the installed lilToon package, and its version,
variant and properties are the verified ones, the same check as the binding above; a shader that only looks like lilToon is not used),
Live Link also sends what the standalone needs to draw it with the lilToon look in its 3D View:

- the shader name, every property value as the material holds it (colors, numbers, vectors, toggles, and the tiling and offset of
  each texture as `<name>_ST`) and the enabled keywords;
- for each lilToon texture slot that the standalone draws and that does not show a painted channel (shadow color textures, MatCaps,
  masks, …), the texture itself. It is drawn into a temporary `RenderTexture` and read from there (the texture is not made readable and
  its import settings are not changed), halved until no side exceeds 2048, and at most 64 MiB of pixels are sent at a time; a slot over
  that is reported as not sent. A texture already sent is not sent again until the model is sent again.

Values are sent after the model, and again (only for the materials that changed) when a material is edited, checked every 0.2 s through
the material's change count and the textures in the slots. A material that is no longer a verified lilToon material is sent as "no
values", and the standalone drops what it had. Nothing is written to the material.

The values are sent only when the standalone has the *material values* feature mark (`ylb_common_features`); an older standalone
gets nothing new. The library functions are `ylb_values_*` and `ylb_texture_send` (library version 4).

## Original textures

When the standalone has the *original textures* feature mark, Live Link also sends what is already in the slot the standalone paints (the Color
property of each material, such as `_MainTex`), so that a new texture set starts from it instead of an empty layer. The standalone puts the
picture at the bottom of the set as a layer named "Original", and the painting goes over it. The model in the scene does not change when
the link is made: the standalone shows a set in Unity only after the original is in it, so nothing turns black, white or checkered.

- Sent after the model for each material whose Color property holds a texture, a few textures per editor update (a standalone without the
  *material requests* mark; for one with the mark see *Requests from the standalone* below). The standalone adds it only
  to a texture set it has just created (or to the untouched first set of a new unsaved project), never to a set that has painting, was edited
  while waiting, or comes from a project the user opened. Nothing is added to the undo history.
- Read without changing anything (the texture, its import settings and the material are not written and the texture is not made readable).
  From the source file for PNG, TGA and JPG when the import settings keep its pixels: the real values, with the RGB of transparent pixels.
  Otherwise (PSD, or an import that changes the picture: normal map, sprite, alpha source, "Alpha Is Transparency", a PNG with a gamma
  chunk that Unity applies, …) from the imported texture: its CPU values,
  or, when it is not readable, drawn into a temporary `RenderTexture` and read back (a compressed texture gives the values the GPU decodes,
  not the file's). A picture that is not an asset is drawn and read back. The standalone shows a mark on a layer that was read through the GPU
  or from a compressed texture, and says if it scaled the picture to the size of the set.
- A texture used by several materials is read once; each material still gets its own copy of the pixels, and each copy counts against the
  1 GiB below (the standalone keeps a separate set per material). The originals are sent again whenever the model is sent again (a changed
  hierarchy, or the link made again after a script reload).
- At most 8192 pixels on a side (a larger texture is reported as too large, not shrunk) and 1 GiB of pixels in one send. A texture that cannot
  be read or sent is reported with its reason, and the set starts empty. The standalone gives up waiting after 30 seconds without progress.
- Sent only when the standalone has the feature mark (`ylb_common_features`); an older standalone is not sent anything and shows its sets
  at once as before. The library functions are `ylb_original_send` and `ylb_pending_bytes` (library version 5; `ylb_original_send` takes the
  picture's stamp since library version 6).

## Requests from the standalone

When both sides have the *material requests* mark, the standalone asks for what it needs and Live Link answers only that (`ylb_next_request`,
read every editor update; library version 6):

- **Original textures**: Live Link no longer pushes them. The standalone asks for the materials of the sets it is about to fill, so a model
  sent again (a changed hierarchy, a reconnection) reads and sends only the textures of sets that are really new, not every texture of the model.
  A request names the model generation (a request for another generation is not answered), the material and the slot. A material whose slot
  holds no texture any more is answered as unreadable, so the standalone does not wait for it.
- **Stamps**: a request can carry the stamp of the picture the standalone already holds. When the stamp equals the texture's stamp now, Live Link
  answers without pixels (`Cached`) and does not read the texture. A stamp mixes the asset (GUID and local file ID), what the import produced from it
  (`AssetDatabase.GetAssetDependencyHash`: the source file, the import settings and the importer version), the hash of the imported picture, the
  length and modification time of the source file, the size, the project's colour space and the build target, and the version of Live Link's way of
  reading. It is taken before the texture is read, so a texture that changes while it is read is never sent with the newer stamp. A texture that is
  not a project asset (a runtime texture, a `RenderTexture`), or whose stamp cannot be taken, has no stamp (0) and is always sent as pixels. When the
  stamps differ, the pixels are sent, so an older picture is never used for a changed texture.
- **Values**: a request for values sends the material's lilToon values again with every texture of its slots (the standalone says it holds none);
  a material without a `Material` is answered as "no values" so the standalone's wait ends. The standalone asks when no values arrived for a
  material that has a texture set, or when a texture it was told would follow did not arrive.

A standalone without the mark keeps the old way: originals are pushed after the model and values after the model and when they change.

## Transport

The native library in `Plugins/LiveLink` (`libyolu_bridge.so` for the Linux editor, `yolu_bridge.dll` for the Windows editor,
x86_64) connects to the standalone through a local socket (a Unix socket on Linux and macOS, a named pipe on Windows). Pixels
arrive through shared memory, one mapped file per texture set and channel, in 128-pixel tiles; only the tiles that changed are
copied. The library's functions never wait on the standalone, so the editor's main thread is not blocked.

- The library is not read from `Plugins/LiveLink` itself but from a copy under the project's `Library/YoluPainter/LiveLink/<hash of the
  contents>/` (the functions are called through function pointers, `Native/LiveLinkNativeLoader.cs`). Unity never lets go of a native library
  it has loaded, and Windows does not allow a file that is loaded to be replaced or deleted, so loading the package's own file would make a
  package update fail (and make an update need an editor restart). The package's file is not held by the editor; after an update the new
  library is copied into a folder of its own and used from then on, and the copies of other contents are deleted where they can be (one that
  is still loaded stays until the editor quits and is not used). The library that was loaded before may still hold links (the standalone takes
  one Unity at a time, so they would make the new library's connection be refused as busy); the copy's path is kept in `SessionState`, and when
  a copy of other contents is loaded, `ylb_disconnect_all` of the previous copy is called first, and so is that of the package's own file
  for an editor that loaded it before copies were used. Only a library that is already loaded is asked; none is loaded for this.
- `ylb_abi_version` is asked first; a library of another version is not used. The library's version is 6.
- A model whose message would pass the limit of one message (512 MiB) is refused before anything is written (`ylb_model_send` returns
  `YLB_E_TOO_LARGE`, and Live Link says so); the link stays up and nothing is sent. Poses that would pass the limit are skipped with a note.
- Each texture channel is a `RenderTexture` kept on the GPU (RGBA, with a mip chain; Color and Emission are sRGB when the
  project's colour space is linear, the others linear). Changed tiles are copied from shared memory into a small strip texture,
  the strip is uploaded and `Graphics.CopyTexture` places each tile; the mip chain of the channels that changed is rebuilt on the
  GPU once the changes are in. A change of the whole texture is spread over several editor updates: each update spends about
  8 ms of the main thread on strips and mip rebuilds (a strip of up to 32 tiles cannot be split, so one strip is always
  uploaded), and several channels take turns. Channels packed for the material (Value, 1 − Value, Metallic with Smoothness)
  are packed again once, when the whole change has arrived, and that pass is outside the 8 ms. Where `CopyTexture` is not
  available, the whole `Texture2D` is uploaded with `Apply`.
- A texture that the graphics device lost is created again and filled again from the standalone.
- `Native/LiveLinkNative.g.cs` is generated from the library's C functions (csbindgen, then rewritten to function pointers by the
  standalone repository's `tools/gen-livelink-native.py`); do not edit it by hand.
- Diagnostics ▸ *Connect to the test pattern* starts a test server inside the library (a checker per material) to check
  the link, the shared memory and the property blocks without the standalone.

## Versions

In the greeting each side tells the other its app version (the Unity package's version and the standalone's version, which are numbered
separately), the oldest version of the other side it works with, and its feature marks. A mismatch never stops the link; only protocol
versions that do not overlap are refused.

- The Live Link window's state row gets a warning mark when the link is up but a version or a feature is off: the other side is older
  than the version this side asks for (or does not tell its version, because it predates this check), or one side has features the other
  lacks. The mark's tooltip names both versions, which side to update (and to which version) and the features that cannot be used. The
  state itself stays a short name. The mark is shown only while the link is up; it goes when the standalone is closed.
- A feature mark is a bit in the greeting (material values, assets, project transfer, animation, original textures, material requests). The marks both sides set are the
  features usable on this link; a command that needs a mark is sent only when the other side has it (`ylb_common_features`).
- When the protocol versions do not overlap the standalone refuses, and the window's warning says which side to update and to which
  version ("The Unity package must be 0.6.0 or newer.").
- The bridge library answers these through `ylb_link_report`, `ylb_peer_app_version` and `ylb_common_features` (library version 3 and later). The
  answers are about the link that is up: once it has ended they are empty (no peer version, no common features).

## Moving to the standalone

The Unity package no longer grows: Live Link is its main job. When the YoluPainter painting window is opened, a small window recommends
the standalone once per editor session (a one-line list of what only the standalone has, and three buttons: open the download page,
Later, Don't show again). **Edit ▸ Preferences ▸ YoluPainter ▸ Suggest the standalone YoluPainter** turns it off (on by default;
*Don't show again* is the same switch). The painting window itself is not changed. The standalone is distributed for Windows only, so the
window and the switch are shown in the Windows editor only (on Linux and macOS neither is shown, and nothing is remembered).

## Who can connect

Only programs of the same user. The standalone creates a random key every time it starts listening and stores it in a folder
only the user can open (`$XDG_RUNTIME_DIR/yolupainter` or `/run/user/<uid>/yolupainter` on Linux, a `yolupainter-<uid>` folder in
`/tmp` on macOS and where Linux has no runtime folder, `%LOCALAPPDATA%\YoluPainter\LiveLink` on Windows); the socket, the pipe and
the shared memory files are readable only by that user too. When a standalone starts listening it removes the shared memory
files that a standalone that crashed left behind. The library reads the key, proves it knows it in its greeting without sending it, and does not send the model
until the standalone proves the same. A standalone or Unity from before the key was introduced is refused, and the reason is
shown in the status; update both sides together. Other programs of the same user can read the key file, so they can connect: the operating system
does not tell them apart.
