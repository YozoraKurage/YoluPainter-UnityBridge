using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Dot.TexturePainter.Core.Psd
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

        internal void Validate()
        {
            if (MaxSourceBytes < 26 || MaxOutputBytes < 26 || MaxDimension < 1 || MaxDimension > 30000 ||
                MaxCanvasPixels < 1 || MaxLayers < 1 || MaxLayers > 32767 || MaxDecodedBytes < 4 ||
                MaxMetadataBytes < 0 || MaxNameCodeUnits < 1 || MaxDiagnostics < 1)
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
    }

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
        public byte[] PixelsRgba;
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
