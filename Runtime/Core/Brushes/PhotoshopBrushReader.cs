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
            var samples = new List<Sample>(); DescriptorObject presets = null; string descError = null, patternError = null;
            var patterns = new List<PhotoshopPattern>(); var patternFailures = new List<PhotoshopPatternReader.Failure>();
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
                    case "patt":
                    {
                        try { patterns.AddRange(PhotoshopPatternReader.ReadFramed(new BigEndianReader(r.Bytes(length)), patternFailures)); }
                        catch (BrushImportException ex) { patternError = ex.Message; } // 模様が読めなくても筆先と設定は取り込む
                        break;
                    }
                    default: otherSections.Add(key); r.Skip(length); break;
                }
                r.Position = start + length;
                int pad = (4 - length % 4) % 4; if (pad > 0 && r.Remaining >= pad) r.Skip(pad);
            }
            var common = new List<string>();
            if (descError != null) common.Add("Brush settings could not be read (" + descError + "); only the tips were imported with default settings.");
            if (patternError != null) common.Add("Brush textures (patterns) could not be read (" + patternError + "); brushes that use one are imported without it.");
            foreach (var key in otherSections.Distinct()) common.Add("Section '" + key + "' is not supported and was skipped.");
            var textures = new Textures { Patterns = patterns, Failures = patternFailures };

            var result = new List<ImportedBrush>(); var used = new HashSet<string>();
            var list = presets?.Get<DescriptorList>("Brsh");
            if (list != null)
                foreach (var item in list.Items.OfType<DescriptorObject>())
                {
                    var brush = Preset(item, samples, textures, version, common, used);
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
        sealed class Textures { public List<PhotoshopPattern> Patterns; public List<PhotoshopPatternReader.Failure> Failures; }
        enum Target { None, Size, Opacity, Flow, Angle }

        static ImportedBrush Preset(DescriptorObject preset, List<Sample> samples, Textures textures, int version, List<string> common, HashSet<string> used)
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
                s.SizeJitter = Jitter(size); s.PressureSize = false; Control(size, "Size", warnings, s, Target.Size);
                if (preset.Number("minimumDiameter") is double min && min > 0) warnings.Add("Minimum diameter (" + min + "%) is not supported.");
                s.AngleJitter = Jitter(preset.Get<DescriptorObject>("angleDynamics")); Control(preset.Get<DescriptorObject>("angleDynamics"), "Angle", warnings, s, Target.Angle);
                s.RoundnessJitter = Jitter(preset.Get<DescriptorObject>("roundnessDynamics")); Control(preset.Get<DescriptorObject>("roundnessDynamics"), "Roundness", warnings, s, Target.None);
            }
            if (preset.Bool("useScatter") == true)
            {
                var scatter = preset.Get<DescriptorObject>("scatterDynamics");
                s.Scatter = Math.Min(10, (scatter?.Number("jitter") ?? 0) / 100);
                Control(scatter, "Scatter", warnings, s, Target.None);
                s.Count = (int)Math.Max(1, Math.Min(16, preset.Number("Cnt ") ?? 1));
                if (preset.Bool("bothAxes") != true && s.Scatter > 0) warnings.Add("Scatter along one axis only is not supported; dabs scatter along both axes.");
                if (Jitter(preset.Get<DescriptorObject>("countDynamics")) > 0) warnings.Add("Count jitter is not supported.");
            }
            if (preset.Bool("usePaintDynamics") == true)
            {
                var opacity = preset.Get<DescriptorObject>("opVr"); var flow = preset.Get<DescriptorObject>("prVr");
                s.OpacityJitter = Jitter(opacity); s.PressureOpacity = false; Control(opacity, "Opacity", warnings, s, Target.Opacity);
                s.FlowJitter = Jitter(flow); s.PressureFlow = false; Control(flow, "Flow", warnings, s, Target.Flow);
            }
            var options = preset.Get<DescriptorObject>("toolOptions");
            if (options != null)
            {
                if (options.Number("Opct") is double op) s.Opacity = Clamp01(op, 100);
                if (options.Number("flow") is double fl) s.Flow = Clamp01(fl, 100);
            }
            if (preset.Bool("useColorDynamics") == true) ColorDynamicsOf(preset, s, warnings);
            var dualBlock = preset.Get<DescriptorObject>("dualBrush");
            if (preset.Bool("useDualBrush") == true || dualBlock?.Bool("useDualBrush") == true) DualOf(dualBlock, samples, s, warnings, used);
            if (preset.Bool("useTexture") == true) TextureOf(preset, textures, s, warnings);
            foreach (var (key, what) in new[] { ("Nose", "Noise"), ("Wtdg", "Wet edges") })
                if (preset.Bool(key) == true) warnings.Add(what + " is not supported.");
            return new ImportedBrush(name, "Photoshop ABR v" + version, s, warnings);
        }

        /// <summary>Color Dynamics: foreground/background jitter (clVr), hue (H), saturation (Strt), brightness (Brgh) and purity.
        /// The background colour is not stored in a brush; the painter's secondary colour is used.</summary>
        static void ColorDynamicsOf(DescriptorObject preset, BrushSettings s, List<string> warnings)
        {
            var fb = preset.Get<DescriptorObject>("clVr");
            s.ForegroundBackgroundJitter = Jitter(fb);
            int control = (int)(fb?.Number("bVTy") ?? 0);
            if (control != 0) warnings.Add("Foreground/background " + ControlName(control) + " control is not supported; the colour is mixed at random only.");
            s.HueJitter = Clamp01(preset.Number("H   ") ?? 0, 100); s.SaturationJitter = Clamp01(preset.Number("Strt") ?? 0, 100); s.BrightnessJitter = Clamp01(preset.Number("Brgh") ?? 0, 100);
            s.Purity = Math.Max(-1, Math.Min(1, (preset.Number("purity") ?? 0) / 100));
            if (preset.Bool("colorDynamicsPerTip") == false) s.ColorPerTip = false;
        }

        /// <summary>Dual Brush: the second tip (Brsh: computed or sampled, with its diameter, hardness, angle, roundness and
        /// spacing), its blending mode (BlnM), scatter (scatterDynamics jitter), count (Cnt).</summary>
        static void DualOf(DescriptorObject block, List<Sample> samples, BrushSettings s, List<string> warnings, HashSet<string> used)
        {
            var shape = block?.Get<DescriptorObject>("Brsh");
            if (shape == null) { warnings.Add("Dual brush: the second tip is missing; the dual brush is not used."); return; }
            var d = new DualBrush();
            if (shape.ClassId == "sampledBrush")
            {
                string id = shape.Text("sampledData"); var sample = samples.FirstOrDefault(x => x.Id == id);
                if (sample == null) { warnings.Add("Dual brush: its tip is not in the file; the dual brush is not used."); return; }
                used.Add(id); d.Tip = new BrushTip(sample.Tip.Name + " (dual)", sample.Tip.Width, sample.Tip.Height, sample.Tip.CopyAlpha());
            }
            else if (shape.ClassId == "computedBrush") d.Hardness = Clamp01(shape.Number("Hrdn") ?? 100, 100);
            else warnings.Add("Dual brush: unknown tip kind '" + shape.ClassId + "'; a round tip is used.");
            d.Radius = Math.Max(.5, Math.Min(2000, shape.Number("Dmtr") ?? (d.Tip != null ? Math.Max(d.Tip.Width, d.Tip.Height) : 20)) / 2);
            d.Angle = Math.Max(-180, Math.Min(180, shape.Number("Angl") ?? 0));
            d.Roundness = Math.Max(.01, Clamp01(shape.Number("Rndn") ?? 100, 100));
            d.Spacing = Math.Max(.01, Math.Min(4, (shape.Number("Spcn") ?? block.Number("Spcn") ?? 25) / 100));
            string mode = (block["BlnM"] as DescriptorEnum)?.Value;
            switch (mode)
            {
                case null: case "Mltp": d.Mode = DualBrushMode.Multiply; break;
                case "Drkn": d.Mode = DualBrushMode.Darken; break;
                case "Ovrl": d.Mode = DualBrushMode.Overlay; break;
                case "CDdg": d.Mode = DualBrushMode.ColorDodge; break;
                case "CBrn": d.Mode = DualBrushMode.ColorBurn; break;
                case "linearBurn": d.Mode = DualBrushMode.LinearBurn; break;
                case "hardMix": d.Mode = DualBrushMode.HardMix; break;
                case "blendSubtraction": case "Sbtr": d.Mode = DualBrushMode.Subtract; break;
                default: d.Mode = DualBrushMode.Multiply; warnings.Add("Dual brush mode '" + mode + "' is not supported; Multiply is used."); break;
            }
            var scatter = block.Get<DescriptorObject>("scatterDynamics");
            d.Scatter = Math.Min(10, (scatter?.Number("jitter") ?? 0) / 100);
            Control(scatter, "Dual brush scatter", warnings, s, Target.None);
            d.Count = (int)Math.Max(1, Math.Min(16, block.Number("Cnt ") ?? 1));
            if (block.Bool("bothAxes") == false && d.Scatter > 0) warnings.Add("Dual brush scatter along one axis only is not supported; dabs scatter along both axes.");
            if (Jitter(block.Get<DescriptorObject>("countDynamics")) > 0) warnings.Add("Dual brush count jitter is not supported.");
            if (block.Bool("Flip") == true || shape.Bool("flipX") == true || shape.Bool("flipY") == true) warnings.Add("Dual brush flipping is not supported.");
            s.Dual = d;
        }

        /// <summary>Texture: the pattern (Txtr, matched by its id only, never by name), depth, scale and invert. The texture is
        /// applied to the stroke's opacity ceiling as this engine's paper texture (Photoshop's Multiply with "Texture Each Tip"
        /// off); other modes and per-tip texturing are reported.</summary>
        static void TextureOf(DescriptorObject preset, Textures textures, BrushSettings s, List<string> warnings)
        {
            var reference = preset.Get<DescriptorObject>("Txtr");
            string id = reference?.Text("Idnt"), patternName = reference?.Text("Nm  ") ?? "";
            var pattern = id == null ? null : textures.Patterns.FirstOrDefault(p => p.Id == id);
            if (pattern == null)
            {
                var failure = textures.Failures.FirstOrDefault(f => f.Name == patternName && patternName.Length > 0);
                warnings.Add(failure != null ? "Texture: the pattern '" + patternName + "' could not be used (" + failure.Reason + "); the brush has no texture."
                    : "Texture: the pattern '" + patternName + "' is not in the file (Photoshop takes it from its pattern library); the brush has no texture.");
                return;
            }
            var texture = pattern.Texture;
            if (preset.Bool("InvT") == true)
            {
                var a = texture.CopyAlpha(); for (int i = 0; i < a.Length; i++) a[i] = (byte)(255 - a[i]);
                texture = new BrushTip(texture.Name, texture.Width, texture.Height, a);
            }
            s.Texture = texture;
            s.TextureDepth = Clamp01(preset.Number("textureDepth") ?? 100, 100);
            double scale = (preset.Number("textureScale") ?? 100) / 100;
            s.TextureScale = Math.Max(.05, Math.Min(64, scale));
            if (s.TextureScale != scale) warnings.Add("Texture scale " + scale * 100 + "% is outside 5..6400%; " + s.TextureScale * 100 + "% is used.");
            string mode = (preset["textureBlendMode"] as DescriptorEnum)?.Value;
            if (mode != null && mode != "Mltp") warnings.Add("Texture mode '" + mode + "' is not supported; Multiply is used.");
            if (preset.Bool("TxtC") == true) warnings.Add("Texture each tip is not supported; the texture is applied once per stroke.");
            var depthDynamics = preset.Get<DescriptorObject>("textureDepthDynamics");
            if (Jitter(depthDynamics) > 0 || (depthDynamics?.Number("bVTy") ?? 0) != 0) warnings.Add("Texture depth jitter and control are not supported.");
            foreach (var key in new[] { "textureBrightness", "textureContrast" })
                if ((preset.Number(key) ?? 0) != 0) warnings.Add("Texture " + key.Substring(7).ToLowerInvariant() + " is not supported.");
            warnings.AddRange(pattern.Warnings.Select(w => "Texture: " + w));
        }

        static double Jitter(DescriptorObject dynamics) { return dynamics == null ? 0 : Clamp01(dynamics.Number("jitter") ?? 0, 100); }
        /// <summary>The control source (bVTy) of a dynamics block: 0 off, 1 fade (over fStp steps), 2 pen pressure, 3 pen tilt,
        /// 4 stylus wheel, 5 rotation, 6 initial direction, 7 direction (the order in the KDE "Krita/Photoshop Mapping Table" and
        /// Brushfactory's ABR notes, which agree). Size, opacity and flow take fade, pressure and tilt; the angle takes tilt
        /// (the pen's lean direction) and direction (FollowDirection). Everything else is reported and left off.</summary>
        static void Control(DescriptorObject dynamics, string what, List<string> warnings, BrushSettings s, Target target)
        {
            if (dynamics == null) return;
            int control = (int)(dynamics.Number("bVTy") ?? 0);
            if (control == 0) return;
            bool scalar = target == Target.Size || target == Target.Opacity || target == Target.Flow;
            if (control == 2 && scalar)
            { if (target == Target.Size) s.PressureSize = true; else if (target == Target.Opacity) s.PressureOpacity = true; else s.PressureFlow = true; return; }
            if (control == 1 && scalar)
            {
                double steps = dynamics.Number("fStp") ?? dynamics.Number("fstp") ?? 0;
                if (steps >= 1 && steps <= BrushSettings.MaxFade)
                { int n = (int)Math.Round(steps); if (target == Target.Size) s.FadeSize = n; else if (target == Target.Opacity) s.FadeOpacity = n; else s.FadeFlow = n; return; }
                warnings.Add(what + " fade over " + steps + " steps is outside 1.." + BrushSettings.MaxFade + "; it is left off."); return;
            }
            if (control == 3 && (scalar || target == Target.Angle))
            { if (target == Target.Size) s.TiltSize = true; else if (target == Target.Opacity) s.TiltOpacity = true; else if (target == Target.Flow) s.TiltFlow = true; else s.TiltAngle = true; return; }
            if (control == 7 && target == Target.Angle) { s.FollowDirection = true; return; }
            warnings.Add(what + " " + ControlName(control) + " control is not supported; it is left off.");
        }
        static string ControlName(int control)
        {
            switch (control)
            {
                case 1: return "fade"; case 2: return "pen pressure"; case 3: return "pen tilt"; case 4: return "stylus wheel";
                case 5: return "rotation"; case 6: return "initial direction"; case 7: return "direction"; default: return "unknown (" + control + ")";
            }
        }
        static double Clamp01(double value, double scale) { return Math.Max(0, Math.Min(1, value / scale)); }
        static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
    }
}
