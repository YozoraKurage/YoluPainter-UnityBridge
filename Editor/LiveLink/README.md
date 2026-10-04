# Live Link

Live Link shows the textures painted in the standalone YoluPainter on a model in the Unity scene, with its real materials
(lilToon, Standard, …). Menu: **YozoLab ▸ YoluPainter ▸ Live Link**. The standalone listens on a link name (default
`yolupainter-livelink`); connect, choose a GameObject in the scene and send it.

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

## Transport

The native library in `Plugins/LiveLink` (`libyolu_bridge.so` for the Linux editor, `yolu_bridge.dll` for the Windows editor,
x86_64) connects to the standalone through a local socket (a Unix socket on Linux and macOS, a named pipe on Windows). Pixels
arrive through shared memory, one mapped file per texture set and channel, in 128-pixel tiles; only the tiles that changed are
copied. The library's functions never wait on the standalone, so the editor's main thread is not blocked.

- `ylb_abi_version` is asked first; a library of another version is not used (Unity keeps a loaded native library until it
  quits, so restart Unity after updating the package).
- Each texture channel is a `RenderTexture` kept on the GPU (RGBA, with a mip chain; Color and Emission are sRGB when the
  project's colour space is linear, the others linear). Changed tiles are copied from shared memory into a small strip texture,
  the strip is uploaded and `Graphics.CopyTexture` places each tile; the mip chain of the channels that changed is rebuilt on the
  GPU once the changes are in. A change of the whole texture is spread over several editor updates: each update spends about
  8 ms of the main thread on strips and mip rebuilds (a strip of up to 32 tiles cannot be split, so one strip is always
  uploaded), and several channels take turns. Channels packed for the material (Value, 1 − Value, Metallic with Smoothness)
  are packed again once, when the whole change has arrived, and that pass is outside the 8 ms. Where `CopyTexture` is not
  available, the whole `Texture2D` is uploaded with `Apply`.
- A texture that the graphics device lost is created again and filled again from the standalone.
- `Native/LiveLinkNative.g.cs` is generated from the library's C functions (csbindgen); do not edit it by hand.
- Diagnostics ▸ *Connect to the test pattern* starts a test server inside the library (a checker per material) to check
  the link, the shared memory and the property blocks without the standalone.

## Who can connect

Only programs of the same user. The standalone creates a random key every time it starts listening and stores it in a folder
only the user can open (`$XDG_RUNTIME_DIR/yolupainter` or `/run/user/<uid>/yolupainter` on Linux, a `yolupainter-<uid>` folder in
`/tmp` on macOS and where Linux has no runtime folder, `%LOCALAPPDATA%\YoluPainter\LiveLink` on Windows); the socket, the pipe and
the shared memory files are readable only by that user too. When a standalone starts listening it removes the shared memory
files that a standalone that crashed left behind. The library reads the key, proves it knows it in its greeting without sending it, and does not send the model
until the standalone proves the same. A standalone or Unity from before the key was introduced is refused, and the reason is
shown in the status; update both sides together. Other programs of the same user can read the key file, so they can connect: the operating system
does not tell them apart.
