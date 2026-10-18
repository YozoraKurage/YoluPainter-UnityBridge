# Third-party material

This package bundles no third-party code, binaries, icons, brushes or other assets, and depends on no other package. It uses
Unity's own Editor API inside the user's licensed Unity installation. Live Link exchanges JSON files with the standalone
YoluPainter and bundles no native library; on macOS and Linux it only calls `chmod` of the operating system's own C library to make the
Live Link folder private.

The package does not depend on, bundle or modify lilToon or any other shader package. The name, version and property values of a
material's shader are read from the user's project only to describe the material to the standalone YoluPainter; applying exported
textures sets texture properties of the user's materials and nothing else.

Product and format names (Unity, FBX, lilToon) describe interoperability, not affiliation or endorsement.
