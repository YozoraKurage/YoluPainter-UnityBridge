using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>What a resource is. Only images today; brushes, materials and smart materials join here (each new kind is a new
    /// .ylp format, so an older YoluPainter refuses the file instead of dropping what it does not know).</summary>
    public enum ResourceKind { Image = 0 }

    /// <summary>Where a resource's copy came from.</summary>
    public enum ResourceOriginKind
    {
        /// <summary>No source: the embedded copy is all there is.</summary>
        None = 0,
        /// <summary>A Texture2D in the Unity project (Assets or Packages), by GUID.</summary>
        UnityAsset = 1,
        /// <summary>An image file outside the Unity project's assets, by absolute path.</summary>
        File = 2,
        /// <summary>A file in the user's own library folder (personal settings), by file name.</summary>
        Library = 3,
        /// <summary>One of YoluPainter's procedural images, by key and version.</summary>
        BuiltIn = 4,
    }

    /// <summary>How the stored values are meant: colour (sRGB-encoded), data (linear), or not known (an image file).</summary>
    public enum ResourceColorSpace { Unspecified = 0, Srgb = 1, Linear = 2 }

    /// <summary>
    /// The pixels of an image resource: straight RGBA8, bottom-left origin, 1–<see cref="MaxSide"/> pixels on each side, never
    /// changed after creation. <see cref="Hash"/> is the SHA-256 of the canonical pixel stream (see <see cref="ComputeHash"/>), so
    /// equal pixels have equal hashes whatever file they came from; the .ylp stores the content once as
    /// <c>resources/&lt;hash&gt;.png</c>.
    /// </summary>
    public sealed class ImageContent
    {
        /// <summary>The largest side, the same as a texture set (<see cref="PaintDocument.MaxNativeSide"/>).</summary>
        public const int MaxSide = 8192;
        public string Hash { get; }
        public int Width { get; }
        public int Height { get; }
        readonly byte[] pixels;
        byte[] png;
        /// <summary>The pixel bytes this content holds in memory (what budgets count).</summary>
        public long ByteSize => pixels.LongLength;

        ImageContent(byte[] rgba, int width, int height, string hash) { pixels = rgba; Width = width; Height = height; Hash = hash; }

        /// <summary>A content from a copy of <paramref name="rgba"/> (the caller keeps its array).</summary>
        public static ImageContent FromPixels(byte[] rgba, int width, int height)
        {
            Check(rgba, width, height);
            var copy = (byte[])rgba.Clone();
            return new ImageContent(copy, width, height, ComputeHash(copy, width, height));
        }

        /// <summary>A content that takes over <paramref name="rgba"/> (no copy; the caller must not change the array afterwards).</summary>
        public static ImageContent Adopt(byte[] rgba, int width, int height)
        {
            Check(rgba, width, height);
            return new ImageContent(rgba, width, height, ComputeHash(rgba, width, height));
        }

        /// <summary>Decodes a resource's PNG (<see cref="RgbaPng"/>). With <paramref name="expectedHash"/>, refuses pixels whose hash
        /// differs (a .ylp entry is named by its content). The PNG bytes are kept to write the same file back.</summary>
        public static ImageContent FromPng(byte[] png, string expectedHash = null)
        {
            var (rgba, width, height) = RgbaPng.Decode(png);
            var content = Adopt(rgba, width, height);
            if (expectedHash != null && content.Hash != expectedHash) throw new InvalidDataException("The image's pixels do not match its content hash (" + Short(expectedHash) + " expected, " + Short(content.Hash) + " found).");
            content.png = png;
            return content;
        }

        static void Check(byte[] rgba, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width < 1 || height < 1 || width > MaxSide || height > MaxSide) throw new ResourceRefusedException(ResourceRefusal.TooLarge, "An image is 1–" + MaxSide + " pixels on each side (this one is " + width + " × " + height + ").");
            if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("The image must be " + width + " × " + height + " RGBA8 (" + (long)width * height * 4 + " bytes).", nameof(rgba));
        }

        /// <summary>
        /// The content hash: SHA-256 (lower-case hex) of the ASCII bytes <c>YLPRGBA8</c>, width and height as 32-bit little-endian
        /// integers, then the straight RGBA8 pixels row by row from the bottom row up.
        /// </summary>
        public static string ComputeHash(byte[] rgba, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            using (var sha = SHA256.Create())
            {
                var head = new byte[16];
                System.Text.Encoding.ASCII.GetBytes("YLPRGBA8", 0, 8, head, 0);
                BitConverter.GetBytes(width).CopyTo(head, 8); BitConverter.GetBytes(height).CopyTo(head, 12);
                if (!BitConverter.IsLittleEndian) { Array.Reverse(head, 8, 4); Array.Reverse(head, 12, 4); }
                sha.TransformBlock(head, 0, head.Length, null, 0);
                sha.TransformFinalBlock(rgba, 0, rgba.Length);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        /// <summary>Whether text is a content hash (64 lower-case hex digits).</summary>
        public static bool IsHash(string text) => text != null && text.Length == 64 && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal static string Short(string hash) => hash != null && hash.Length > 12 ? hash.Substring(0, 12) : hash;

        /// <summary>A copy of the pixels (straight RGBA8, bottom-left origin).</summary>
        public byte[] CopyPixels() => (byte[])pixels.Clone();
        /// <summary>Copies the pixels into <paramref name="destination"/> (<see cref="ByteSize"/> bytes).</summary>
        public void CopyPixelsTo(byte[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.LongLength != pixels.LongLength) throw new ArgumentException("The destination must hold " + pixels.LongLength + " bytes.", nameof(destination));
            Buffer.BlockCopy(pixels, 0, destination, 0, pixels.Length);
        }
        /// <summary>One pixel (x from the left, y from the bottom).</summary>
        public Rgba32 GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(x < 0 || x >= Width ? nameof(x) : nameof(y));
            int o = (y * Width + x) * 4;
            return new Rgba32(pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]);
        }
        /// <summary>The pixels shrunk to at most <paramref name="maxSide"/> on the long side (premultiplied area average, for
        /// thumbnails). The content itself is returned when it already fits.</summary>
        public (byte[] rgba, int width, int height) Preview(int maxSide)
        {
            if (maxSide < 1) throw new ArgumentOutOfRangeException(nameof(maxSide));
            if (Math.Max(Width, Height) <= maxSide) return (CopyPixels(), Width, Height);
            double scale = maxSide / (double)Math.Max(Width, Height);
            int w = Math.Max(1, (int)Math.Round(Width * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
            return (ImageResampling.Resample(pixels, Width, Height, w, h, CanvasResampling.Area), w, h);
        }
        /// <summary>The pixels at another size (<see cref="ImageResampling"/>; <paramref name="resampling"/> null is
        /// <see cref="ImageResampling.Automatic"/>). The same size is a copy.</summary>
        public byte[] Resampled(int width, int height, CanvasResampling? resampling = null)
            => ImageResampling.Resample(pixels, Width, Height, width, height, resampling ?? ImageResampling.Automatic(Width, Height, width, height));
        /// <summary>The PNG of this content (made once, then kept).</summary>
        public byte[] EncodePng() => png ?? (png = RgbaPng.Encode(pixels, Width, Height));

        /// <summary>Read-only access for code in this assembly (stages that sample the image).</summary>
        internal byte[] Pixels => pixels;
    }

    /// <summary>
    /// Where a resource came from, and what lets YoluPainter tell whether the source changed since the copy was taken. Immutable.
    /// <list type="bullet">
    /// <item>UnityAsset: <see cref="AssetGuid"/> (32 lower-case hex digits, the reference), <see cref="Path"/> (where it was at the time,
    /// for showing only), <see cref="SourceStamp"/> (Unity's dependency hash of the asset at the time: a quick "unchanged" test; when
    /// it differs the pixels are read again and compared by content hash), <see cref="ReadThroughGpu"/>.</item>
    /// <item>File: <see cref="Path"/> (absolute), <see cref="SourceStamp"/> (SHA-256 of the file's bytes), <see cref="SourceLength"/>.</item>
    /// <item>Library: <see cref="Path"/> (the file name inside the library folder), stamp and length as for File.</item>
    /// <item>BuiltIn: <see cref="BuiltInKey"/> and <see cref="BuiltInVersion"/>.</item>
    /// </list>
    /// </summary>
    public sealed class ResourceOrigin
    {
        public const int MaxPathLength = 1024, MaxStampLength = 128;
        public static readonly ResourceOrigin None = new ResourceOrigin(ResourceOriginKind.None, null, null, null, -1, null, 0, false);
        public ResourceOriginKind Kind { get; }
        public string AssetGuid { get; }
        public string Path { get; }
        public string SourceStamp { get; }
        public long SourceLength { get; }
        public string BuiltInKey { get; }
        public int BuiltInVersion { get; }
        /// <summary>UnityAsset: the texture was not readable on the CPU and was drawn into a render texture and read back (compressed
        /// textures give the values they show, not the original file's).</summary>
        public bool ReadThroughGpu { get; }

        ResourceOrigin(ResourceOriginKind kind, string guid, string path, string stamp, long length, string key, int version, bool gpu)
        { Kind = kind; AssetGuid = guid; Path = path; SourceStamp = stamp; SourceLength = length; BuiltInKey = key; BuiltInVersion = version; ReadThroughGpu = gpu; }

        public static ResourceOrigin UnityAsset(string guid, string path, string stamp, bool readThroughGpu)
        {
            if (!IsAssetGuid(guid)) throw new ArgumentException("A Unity asset GUID is 32 lower-case hex digits.", nameof(guid));
            CheckText(path, nameof(path), MaxPathLength, required: true);
            CheckText(stamp, nameof(stamp), MaxStampLength, required: false);
            return new ResourceOrigin(ResourceOriginKind.UnityAsset, guid, path, stamp ?? "", -1, null, 0, readThroughGpu);
        }
        public static ResourceOrigin File(string path, string sha256, long length)
        {
            CheckText(path, nameof(path), MaxPathLength, required: true);
            if (!ImageContent.IsHash(sha256)) throw new ArgumentException("A file stamp is a SHA-256 (64 lower-case hex digits).", nameof(sha256));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            return new ResourceOrigin(ResourceOriginKind.File, null, path, sha256, length, null, 0, false);
        }
        public static ResourceOrigin Library(string fileName, string sha256, long length)
        {
            CheckText(fileName, nameof(fileName), 255, required: true);
            if (fileName.IndexOfAny(new[] { '/', '\\' }) >= 0 || fileName == "." || fileName == "..") throw new ArgumentException("A library entry is a file name, not a path.", nameof(fileName));
            if (!ImageContent.IsHash(sha256)) throw new ArgumentException("A file stamp is a SHA-256 (64 lower-case hex digits).", nameof(sha256));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            return new ResourceOrigin(ResourceOriginKind.Library, null, fileName, sha256, length, null, 0, false);
        }
        public static ResourceOrigin BuiltIn(string key, int version)
        {
            if (!BuiltInImages.IsKey(key)) throw new ArgumentException("A built-in key is 1–64 characters of a–z, 0–9 and '-'.", nameof(key));
            if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
            return new ResourceOrigin(ResourceOriginKind.BuiltIn, null, null, null, -1, key, version, false);
        }

        /// <summary>The same source with another stamp (after the copy was updated from it).</summary>
        public ResourceOrigin WithStamp(string stamp, long length = -1)
        {
            switch (Kind)
            {
                case ResourceOriginKind.UnityAsset: return UnityAsset(AssetGuid, Path, stamp, ReadThroughGpu);
                case ResourceOriginKind.File: return File(Path, stamp, length);
                case ResourceOriginKind.Library: return Library(Path, stamp, length);
                default: return this;
            }
        }

        public static bool IsAssetGuid(string text) => text != null && text.Length == 32 && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        static void CheckText(string text, string name, int max, bool required)
        {
            if (string.IsNullOrEmpty(text)) { if (required) throw new ArgumentException("Empty " + name + ".", name); return; }
            if (text.Length > max) throw new ArgumentException(name + " is longer than " + max + " characters.", name);
            if (text.Any(c => c < 0x20 || c == 0x7f)) throw new ArgumentException(name + " cannot hold control characters.", name);
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case ResourceOriginKind.UnityAsset: return "Unity asset " + Path + " (" + AssetGuid + ")";
                case ResourceOriginKind.File: return "file " + Path;
                case ResourceOriginKind.Library: return "library " + Path;
                case ResourceOriginKind.BuiltIn: return "built-in " + BuiltInKey + " v" + BuiltInVersion;
                default: return "embedded";
            }
        }
    }

    /// <summary>
    /// An image resource of a project: a stable ID (what layers and stages refer to), a name, the embedded copy of its pixels
    /// (<see cref="Content"/>, shared with other resources of the same content), how its values are meant and where it came from.
    /// Updating it from its source replaces the content under the same ID (<see cref="Revision"/> counts the replacements).
    /// </summary>
    public sealed class ImageResource
    {
        public const int MaxNameLength = 256;
        public Guid Id { get; }
        public ResourceKind Kind => ResourceKind.Image;
        public string Name { get; internal set; }
        public ImageContent Content { get; internal set; }
        public ResourceOrigin Origin { get; internal set; }
        public ResourceColorSpace ColorSpace { get; internal set; }
        /// <summary>Counts content replacements (0 when added).</summary>
        public long Revision { get; internal set; }
        public int Width => Content.Width;
        public int Height => Content.Height;
        public string ContentHash => Content.Hash;

        internal ImageResource(Guid id, string name, ImageContent content, ResourceOrigin origin, ResourceColorSpace colorSpace)
        {
            if (id == Guid.Empty) throw new ArgumentException("A resource needs an ID.", nameof(id));
            CheckName(name);
            if (!Enum.IsDefined(typeof(ResourceColorSpace), colorSpace)) throw new ArgumentOutOfRangeException(nameof(colorSpace));
            Id = id; Name = name; Content = content ?? throw new ArgumentNullException(nameof(content)); Origin = origin ?? ResourceOrigin.None; ColorSpace = colorSpace;
        }

        /// <summary>A resource name: 1–256 characters, not only white space, no control characters. ArgumentException otherwise.</summary>
        public static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A resource needs a name.", nameof(name));
            if (name.Length > MaxNameLength) throw new ArgumentException("A resource name is at most " + MaxNameLength + " characters.", nameof(name));
            if (name.Any(c => c < 0x20 || c == 0x7f)) throw new ArgumentException("A resource name cannot hold control characters.", nameof(name));
        }

        public override string ToString() => Name + " (" + Width + " × " + Height + ", " + ImageContent.Short(ContentHash) + ")";
    }
}
