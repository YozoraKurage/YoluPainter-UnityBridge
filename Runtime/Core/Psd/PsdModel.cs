using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core.Psd
{
    public enum PsdCompatibilityMode { EditableRaster, PreserveOnly, Rejected }

    /// <summary>All allocations and decoded pixels are bounded. These are prototype limits, not PSD format maxima.</summary>
    public sealed class PsdLimits
    {
        public int MaxSourceBytes = 128 * 1024 * 1024;
        public int MaxOutputBytes = 128 * 1024 * 1024;
        public int MaxDimension = 8192;
        public int MaxCanvasPixels = 4096 * 4096;
        public int MaxLayers = 256;
        public long MaxDecodedBytes = 256L * 1024 * 1024;
        public int MaxMetadataBytes = 4 * 1024 * 1024;
        public int MaxNameCodeUnits = 4096;
        public int MaxDiagnostics = 128;
        /// <summary>Folder nesting depth. Photoshop itself allows about ten levels; deeper files are refused as malformed.</summary>
        public int MaxGroupDepth = 32;

        internal void Validate()
        {
            if (MaxSourceBytes < 26 || MaxOutputBytes < 26 || MaxDimension < 1 || MaxDimension > 30000 ||
                MaxCanvasPixels < 1 || MaxLayers < 1 || MaxLayers > 32767 || MaxDecodedBytes < 4 ||
                MaxMetadataBytes < 0 || MaxNameCodeUnits < 1 || MaxDiagnostics < 1 || MaxGroupDepth < 0 || MaxGroupDepth > 1000)
                throw new ArgumentOutOfRangeException("limits", "PSD limits must be positive and within PSD version 1 bounds.");
        }
    }

    /// <summary>Standalone snapshot. Layers are top-to-bottom, pixels are top-down straight RGBA8.
    /// No ICC conversion is implied: colors are untagged sample values, blended in encoded sample space.
    /// Caller must not mutate a snapshot while Write is executing.</summary>
    public sealed class PsdDocument
    {
        public int Width;
        public int Height;
        public List<PsdRasterLayer> Layers = new List<PsdRasterLayer>();
        /// <summary>Merged image the writer stores (Width x Height, top-down straight RGBA8; the writer mattes it on white).
        /// Null: the writer composites the layers with the same reference arithmetic as <see cref="CpuCompositor"/>.
        /// The reader leaves it null; the stored merged image is only compared, never exposed as layer data.</summary>
        public byte[] CompositeRgba;
    }

    /// <summary>A raster layer, a group (folder) when <see cref="Children"/> is not null, or an adjustment layer when
    /// <see cref="Adjustment"/> is not null. Groups and adjustment layers have no pixels and no rectangle. A group's Opacity,
    /// Visible, BlendMode (PassThrough or an isolated mode), Clipping and Mask apply to its contents; an adjustment layer's
    /// apply to its adjustment of what is below it.</summary>
    public sealed class PsdRasterLayer
    {
        public int Id;
        public string Name = "Layer";
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public byte Opacity = 255;
        public bool Visible = true;
        /// <summary>One of the 26 modes with a PSD key (see PsdCodec.BlendKey), or PassThrough on a group (written as "pass" in its section divider).</summary>
        public LayerBlendMode BlendMode = LayerBlendMode.Normal;
        /// <summary>PSD clipping "non-base": clipped to the nearest unclipped layer below, blended with it as a group.</summary>
        public bool Clipping;
        /// <summary>Raster user mask, or null.</summary>
        public PsdLayerMask Mask;
        public byte[] PixelsRgba;
        /// <summary>Contents of a group, top to bottom; null for a raster layer.</summary>
        public List<PsdRasterLayer> Children;
        /// <summary>Layer ID of the group's bounding section divider record ("&lt;/Layer group&gt;"); 0 = the record has none.</summary>
        public int DividerId;
        /// <summary>Invert (nvrt), Levels (levl) or Hue/Saturation (hue2) with the native formulas; null for other layers.</summary>
        public AdjustmentSettings Adjustment;
        /// <summary>Solid colour fill layer (SoCo) colour, opaque; null for other layers. A fill layer has no pixels of its own.</summary>
        public Rgba32? FillColor;
        /// <summary>Layers-panel locks: the protection flags of the layer's (or folder's) lspf block (bit 0 transparency, bit 1 image
        /// pixels, bit 2 position, bit 31 all) and layer flag bit 0 (transparency). Editing state only; nothing renders differently.</summary>
        public LayerLocks Locks;
        public bool IsFill { get { return FillColor.HasValue; } }
        public bool IsGroup { get { return Children != null; } }
        public bool IsAdjustment { get { return Adjustment != null; } }
    }

    /// <summary>Raster layer mask in Photoshop's sense: 255 shows, 0 hides. The rectangle is in canvas coordinates and may
    /// be smaller than the layer or the canvas; outside it every sample is <see cref="DefaultColor"/>.</summary>
    public sealed class PsdLayerMask
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        /// <summary>0 or 255.</summary>
        public byte DefaultColor = 255;
        /// <summary>False when the mask is disabled (kept, but does not affect rendering).</summary>
        public bool Enabled = true;
        /// <summary>User mask density (255 = full). The mask hides (255 - value) x Density / 255.</summary>
        public byte Density = 255;
        /// <summary>Width x Height samples, top-down.</summary>
        public byte[] Pixels;

        /// <summary>Mask sample at a canvas position (the default colour outside the rectangle).</summary>
        public byte ValueAt(int x, int y)
        {
            long mx = (long)x - Left, my = (long)y - Top;
            return mx < 0 || my < 0 || mx >= Width || my >= Height ? DefaultColor : Pixels[my * Width + mx];
        }
    }

    public sealed class PsdDiagnostic
    {
        public string Code { get; private set; }
        public string Message { get; private set; }
        public int Offset { get; private set; }
        public int Length { get; private set; }
        internal PsdDiagnostic(string code, string message, int offset, int length)
        { Code = code; Message = message; Offset = offset; Length = length; }
        public override string ToString() { return Code + ": " + Message; }
    }

    public sealed class PsdReadResult
    {
        private readonly byte[] original;
        public PsdCompatibilityMode Mode { get; private set; }
        /// <summary>Null unless every parsed feature belongs to the supported editable subset.</summary>
        public PsdDocument Document { get; private set; }
        public ReadOnlyCollection<PsdDiagnostic> Diagnostics { get; private set; }
        public bool HasOriginalBytes { get { return original != null; } }
        internal PsdReadResult(PsdCompatibilityMode mode, PsdDocument doc, byte[] bytes, List<PsdDiagnostic> diagnostics)
        { Mode = mode; Document = doc; original = bytes; Diagnostics = diagnostics.AsReadOnly(); }
        /// <summary>Only lossless operation for PreserveOnly: copy the entire unchanged source to a backup.
        /// The codec never writes paths and does not authorize overwriting an externally changed source.</summary>
        public byte[] CopyOriginalBytes()
        {
            if (original == null) throw new InvalidOperationException("Source exceeded the retention limit or could not be read.");
            return (byte[])original.Clone();
        }
    }
}
