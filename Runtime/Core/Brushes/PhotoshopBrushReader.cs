using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>Reads Photoshop brush files (.abr): version 1/2 computed and sampled brushes, and version 6+ sampled tips
    /// ("samp") with their presets ("desc"). Written from public format descriptions (the Photoshop 6.0 File Formats
    /// Specification for v1, the Photoshop File Formats Specification for descriptors, and community write-ups of the newer
    /// layout). Features this engine does not have are listed in each brush's warnings, never dropped silently.</summary>
    public static class PhotoshopBrushReader
    {
        public static IReadOnlyList<ImportedBrush> Read(byte[] data, string fallbackName = null)
        {
            var r = new BigEndianReader(data);
            int version = r.I16();
            if (version == 1 || version == 2) return ReadOld(r, version, fallbackName);
            if (version >= 6 && version <= 10) return ReadNew(r, version, fallbackName);
            throw new BrushImportException("Unsupported ABR version " + version + ".");
        }

        // ---------------- version 1 / 2 ----------------
        static IReadOnlyList<ImportedBrush> ReadOld(BigEndianReader r, int version, string fallbackName)
        {
            int count = r.I16(); if (count < 0 || count > 10000) throw new BrushImportException("Invalid brush count " + count + ".");
            var result = new List<ImportedBrush>();
            for (int i = 0; i < count; i++)
            {
                int type = r.I16(); int size = r.I32(); int start = r.Position;
                if (size < 0 || size > r.Remaining) throw new BrushImportException("Brush " + (i + 1) + " is truncated.");
                string label = (fallbackName ?? "Brush") + " " + (i + 1);
                if (type == 1)
                {
                    r.Skip(4); int spacing = r.I16(), diameter = r.I16(), roundness = r.I16(), angle = r.I16(), hardness = r.I16();
                    var s = new BrushSettings
                    {
                        Radius = Clamp(diameter, 1, 2000) / 2.0, Spacing = Clamp(spacing, 1, 400) / 100.0, Hardness = Clamp(hardness, 0, 100) / 100.0,
                        Roundness = Math.Max(.01, Clamp(roundness, 1, 100) / 100.0), Angle = Clamp(angle, -180, 180), PressureOpacity = false,
                    };
                    result.Add(new ImportedBrush(label, "Photoshop ABR v" + version + " computed", s));
                }
                else if (type == 2)
                {
                    r.Skip(4); int spacing = r.I16();
                    string name = label;
                    if (version == 2) { int chars = r.Count(r.U32(), 2, "name length"); name = Encoding.BigEndianUnicode.GetString(r.Bytes(chars * 2)).TrimEnd('\0'); if (name.Length == 0) name = label; }
                    r.Skip(1); // antialias
                    r.Skip(8); // 16-bit bounds (the 32-bit bounds follow)
                    int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
                    int depth = r.I16();
                    var tip = ReadBitmap(r, name, top, left, bottom, right, depth, out var warnings);
                    var s = new BrushSettings { Tip = tip, Radius = Math.Max(tip.Width, tip.Height) / 2.0, Spacing = Clamp(spacing, 1, 400) / 100.0, PressureOpacity = false };
                    result.Add(new ImportedBrush(name, "Photoshop ABR v" + version + " sampled", s, warnings));
                }
                else throw new BrushImportException("Unknown brush type " + type + " in brush " + (i + 1) + ".");
                r.Position = start + size;
            }
            return result;
        }

        // ---------------- version 6+ ----------------
        sealed class Sample { public string Id; public BrushTip Tip; public List<string> Warnings; }

        static IReadOnlyList<ImportedBrush> ReadNew(BigEndianReader r, int version, string fallbackName)
        {
            int subversion = r.I16();
            if (subversion != 1 && subversion != 2) throw new BrushImportException("Unsupported ABR subversion " + subversion + ".");
            var samples = new List<Sample>(); DescriptorObject presets = null; string descError = null;
            var otherSections = new List<string>();
            while (r.Remaining >= 12)
            {
                string signature = r.Ascii(4), key = r.Ascii(4);
                if (signature != "8BIM") throw new BrushImportException("Expected an 8BIM section at offset " + (r.Position - 8) + ".");
                int length = r.Count(r.U32(), 1, "section length"), start = r.Position;
                switch (key)
                {
                    case "samp": ReadSamples(new BigEndianReader(r.Bytes(length)), subversion, samples, fallbackName); break;
                    case "desc":
                    {
                        var d = new BigEndianReader(r.Bytes(length));
                        try { d.U32(); presets = ActionDescriptorReader.ReadDescriptor(d); }
                        catch (BrushImportException ex) { descError = ex.Message; } // 先端だけでも取り込めるようにする
                        break;
                    }
                    default: otherSections.Add(key); r.Skip(length); break;
                }
                r.Position = start + length;
                int pad = (4 - length % 4) % 4; if (pad > 0 && r.Remaining >= pad) r.Skip(pad);
            }
            var common = new List<string>();
            if (descError != null) common.Add("Brush settings could not be read (" + descError + "); only the tips were imported with default settings.");
            if (otherSections.Contains("patt")) common.Add("Brush textures (patterns) are not imported.");
            foreach (var key in otherSections.Where(k => k != "patt").Distinct()) common.Add("Section '" + key + "' is not supported and was skipped.");

            var result = new List<ImportedBrush>(); var used = new HashSet<string>();
            var list = presets?.Get<DescriptorList>("Brsh");
            if (list != null)
                foreach (var item in list.Items.OfType<DescriptorObject>())
                {
                    var brush = Preset(item, samples, version, common, used);
                    if (brush != null) result.Add(brush);
                }
            // Tips no preset refers to (or files without a usable "desc") still become brushes.
            foreach (var sample in samples.Where(s => !used.Contains(s.Id)))
            {
                var s = new BrushSettings { Tip = sample.Tip, Radius = Math.Max(sample.Tip.Width, sample.Tip.Height) / 2.0, Spacing = .25, PressureOpacity = false };
                result.Add(new ImportedBrush(sample.Tip.Name, "Photoshop ABR v" + version + " tip", s, common.Concat(sample.Warnings)));
            }
            if (result.Count == 0) throw new BrushImportException("The file contains no brushes this tool can read.");
            return result;
        }

        static void ReadSamples(BigEndianReader r, int subversion, List<Sample> samples, string fallbackName)
        {
            while (r.Remaining >= 4)
            {
                int length = r.Count(r.U32(), 1, "sample length"), start = r.Position;
                int idLength = r.U8(); string id = r.Ascii(idLength);
                r.Skip(subversion == 1 ? 10 : 264); // 内容は未文書化（固定長であることは実装どうしで一致している）
                int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32(), depth = r.I16();
                string name = (fallbackName ?? "Tip") + " " + (samples.Count + 1);
                var tip = ReadBitmap(r, name, top, left, bottom, right, depth, out var warnings);
                samples.Add(new Sample { Id = id, Tip = tip, Warnings = warnings });
                int next = start + length; next += (4 - next % 4) % 4;
                if (next > r.Length) break;
                r.Position = next;
            }
        }

        /// <summary>A sampled tip: compression byte, then raw rows or PackBits rows with a 16-bit length per row. Rows are top-down.</summary>
        static BrushTip ReadBitmap(BigEndianReader r, string name, int top, int left, int bottom, int right, int depth, out List<string> warnings)
        {
            warnings = new List<string>();
            int width = right - left, height = bottom - top;
            if (width < 1 || height < 1 || width > BrushTip.MaxSize || height > BrushTip.MaxSize) throw new BrushImportException("Tip size " + width + "x" + height + " is outside 1.." + BrushTip.MaxSize + ".");
            if (depth != 8 && depth != 16) throw new BrushImportException("Unsupported tip depth " + depth + ".");
            int compression = r.U8(), bytesPerPixel = depth / 8, rowBytes = width * bytesPerPixel;
            var rows = new byte[height][];
            if (compression == 0) for (int y = 0; y < height; y++) rows[y] = r.Bytes(rowBytes);
            else if (compression == 1)
            {
                if (depth == 16) throw new BrushImportException("16-bit compressed tips are not supported.");
                var lengths = new int[height]; for (int y = 0; y < height; y++) lengths[y] = r.U16();
                for (int y = 0; y < height; y++) rows[y] = PackBits.DecodeRow(r.Bytes(lengths[y]), rowBytes);
            }
            else throw new BrushImportException("Unsupported tip compression " + compression + ".");
            if (depth == 16) warnings.Add("The 16-bit tip is reduced to 8 bits.");
            var alpha = new byte[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                alpha[(height - 1 - y) * width + x] = rows[y][x * bytesPerPixel]; // 上位バイト。上から下の行を左下原点へ
            return new BrushTip(name, width, height, alpha);
        }

        // ---------------- preset mapping ----------------
        static ImportedBrush Preset(DescriptorObject preset, List<Sample> samples, int version, List<string> common, HashSet<string> used)
        {
            var warnings = new List<string>(common);
            string name = preset.Text("Nm  ") ?? "Brush";
            var shape = preset.Get<DescriptorObject>("Brsh");
            if (shape == null) return null;
            var s = new BrushSettings { PressureOpacity = false, PressureSize = false };
            double? diameter = shape.Number("Dmtr"), angle = shape.Number("Angl"), roundness = shape.Number("Rndn"), spacing = shape.Number("Spcn"), hardness = shape.Number("Hrdn");
            if (shape.ClassId == "sampledBrush")
            {
                string id = shape.Text("sampledData");
                var sample = samples.FirstOrDefault(x => x.Id == id);
                if (sample == null) { warnings.Add("The preset refers to a tip that is not in the file; it was skipped."); return null; }
                used.Add(id); warnings.AddRange(sample.Warnings);
                s.Tip = new BrushTip(name, sample.Tip.Width, sample.Tip.Height, sample.Tip.CopyAlpha());
            }
            else if (shape.ClassId == "computedBrush") s.Hardness = Clamp01(hardness ?? 100, 100);
            else { warnings.Add("Unknown tip kind '" + shape.ClassId + "'; a round tip is used."); }
            s.Radius = Math.Max(.5, Math.Min(2000, diameter ?? (s.Tip != null ? Math.Max(s.Tip.Width, s.Tip.Height) : 20)) / 2);
            s.Angle = Math.Max(-180, Math.Min(180, angle ?? 0));
            s.Roundness = Math.Max(.01, Clamp01(roundness ?? 100, 100));
            s.Spacing = Math.Max(.01, Math.Min(4, (spacing ?? 25) / 100));
            if (shape.Bool("flipX") == true || shape.Bool("flipY") == true) warnings.Add("Tip flipping is not supported.");

            if (preset.Bool("useTipDynamics") == true)
            {
                var size = preset.Get<DescriptorObject>("szVr");
                s.SizeJitter = Jitter(size); s.PressureSize = Control(size, "Size", warnings);
                if (preset.Number("minimumDiameter") is double min && min > 0) warnings.Add("Minimum diameter (" + min + "%) is not supported.");
                s.AngleJitter = Jitter(preset.Get<DescriptorObject>("angleDynamics")); Control(preset.Get<DescriptorObject>("angleDynamics"), "Angle", warnings, false);
                s.RoundnessJitter = Jitter(preset.Get<DescriptorObject>("roundnessDynamics")); Control(preset.Get<DescriptorObject>("roundnessDynamics"), "Roundness", warnings, false);
            }
            if (preset.Bool("useScatter") == true)
            {
                var scatter = preset.Get<DescriptorObject>("scatterDynamics");
                s.Scatter = Math.Min(10, (scatter?.Number("jitter") ?? 0) / 100);
                Control(scatter, "Scatter", warnings, false);
                s.Count = (int)Math.Max(1, Math.Min(16, preset.Number("Cnt ") ?? 1));
                if (preset.Bool("bothAxes") != true && s.Scatter > 0) warnings.Add("Scatter along one axis only is not supported; dabs scatter along both axes.");
                if (Jitter(preset.Get<DescriptorObject>("countDynamics")) > 0) warnings.Add("Count jitter is not supported.");
            }
            if (preset.Bool("usePaintDynamics") == true)
            {
                var opacity = preset.Get<DescriptorObject>("opVr"); var flow = preset.Get<DescriptorObject>("prVr");
                s.OpacityJitter = Jitter(opacity); s.PressureOpacity = Control(opacity, "Opacity", warnings);
                s.FlowJitter = Jitter(flow); s.PressureFlow = Control(flow, "Flow", warnings);
            }
            var options = preset.Get<DescriptorObject>("toolOptions");
            if (options != null)
            {
                if (options.Number("Opct") is double op) s.Opacity = Clamp01(op, 100);
                if (options.Number("flow") is double fl) s.Flow = Clamp01(fl, 100);
            }
            foreach (var (key, what) in new[] { ("useTexture", "Texture"), ("useDualBrush", "Dual brush"), ("useColorDynamics", "Color dynamics"), ("Nose", "Noise"), ("Wtdg", "Wet edges") })
                if (preset.Bool(key) == true) warnings.Add(what + " is not supported.");
            if (preset.Get<DescriptorObject>("dualBrush")?.Bool("useDualBrush") == true && !warnings.Contains("Dual brush is not supported.")) warnings.Add("Dual brush is not supported.");
            return new ImportedBrush(name, "Photoshop ABR v" + version, s, warnings);
        }

        static double Jitter(DescriptorObject dynamics) { return dynamics == null ? 0 : Clamp01(dynamics.Number("jitter") ?? 0, 100); }
        /// <summary>The control source of a dynamics block. Pen pressure maps to this engine's pressure switches where it has
        /// one (size, opacity, flow); every other control, and pressure where the engine has no switch, is reported. The
        /// numbering of controls beyond pen pressure differs between sources, so only 0 (off) and 2 (pen pressure) are trusted.</summary>
        static bool Control(DescriptorObject dynamics, string what, List<string> warnings, bool pressureSupported = true)
        {
            if (dynamics == null) return false;
            int control = (int)(dynamics.Number("bVTy") ?? 0);
            if (control == 0) return false;
            if (control == 2 && pressureSupported) return true;
            warnings.Add(what + (control == 2 ? " by pen pressure" : " control " + control + " (fade, tilt, wheel or direction)") + " is not supported; it is left off.");
            return false;
        }
        static double Clamp01(double value, double scale) { return Math.Max(0, Math.Min(1, value / scale)); }
        static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
    }
}
