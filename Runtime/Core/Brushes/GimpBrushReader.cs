using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>Reads GIMP brushes: .gbr (sampled tip), .gih (image hose: several tips) and .vbr (parametric tip).
    /// Implemented from the published format descriptions (GIMP devel-docs gbr.txt / gih.txt / vbr.txt); no GIMP code is
    /// used. GIMP-specific behaviour this engine does not have is reported as a warning.</summary>
    public static class GimpBrushReader
    {
        const uint Magic = 0x47494D50; // 'GIMP'

        public static ImportedBrush ReadGbr(byte[] data, string fallbackName = null)
        {
            var r = new BigEndianReader(data);
            var tip = ReadOneGbr(r, fallbackName, out double spacing, out var warnings);
            if (r.Remaining > 0) warnings.Add("Data after the brush was ignored (" + r.Remaining + " bytes; GIMP's old .gpb colour pattern is not supported).");
            return new ImportedBrush(tip.Name, "GIMP GBR", Settings(new[] { tip }, spacing), warnings);
        }

        /// <summary>One image hose becomes one brush with several tips. Cell selection by pressure, angle, velocity or tilt is
        /// approximated by random or sequential selection and reported.</summary>
        public static ImportedBrush ReadGih(byte[] data, string fallbackName = null)
        {
            int position = 0;
            string name = Line(data, ref position), parameters = Line(data, ref position);
            var fields = parameters.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 256)
                throw new BrushImportException("The image hose header must start with a cell count of 1..256.");
            var keys = new Dictionary<string, string>();
            foreach (var field in fields.Skip(1)) { int colon = field.IndexOf(':'); if (colon > 0) keys[field.Substring(0, colon)] = field.Substring(colon + 1); }
            var warnings = new List<string>();
            var selection = TipSelection.Random;
            string mode = keys.TryGetValue("sel0", out var s0) ? s0 : (keys.ContainsKey("dim") ? "random" : "incremental");
            if (mode == "incremental") selection = TipSelection.Sequential;
            else if (mode != "random") warnings.Add("Cell selection '" + mode + "' (GIMP chooses cells by " + mode + ") is approximated by random selection.");
            if (keys.TryGetValue("dim", out var dim) && dim != "1") warnings.Add("A " + dim + "-dimensional hose is flattened into one list of cells.");
            var r = new BigEndianReader(data, position);
            var tips = new List<BrushTip>(); double spacing = 25;
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && r.Remaining == 0)
                {
                    // ちょうどファイルの終わりでセルが尽きた（実在する配布物にある）。読めたセルで使い、そう伝える。
                    // セルの途中で切れているものは ReadOneGbr が拒否する。
                    warnings.Add("The hose declares " + count + " cells but the file holds only " + i + "; the " + i + " present are used.");
                    break;
                }
                var tip = ReadOneGbr(r, name, out double cellSpacing, out var cellWarnings);
                if (i == 0) spacing = cellSpacing;
                foreach (var w in cellWarnings) if (!warnings.Contains(w)) warnings.Add(w);
                tips.Add(tip);
            }
            var settings = Settings(tips.ToArray(), spacing);
            settings.TipSelection = selection;
            return new ImportedBrush(string.IsNullOrEmpty(name) ? fallbackName : name, "GIMP GIH", settings, warnings);
        }

        /// <summary>Parametric brush. Circles map to the round tip; squares, diamonds and spikes are rendered into a tip image.
        /// The hardness falloff is this engine's, not GIMP's (GIMP's curve is not documented).</summary>
        public static ImportedBrush ReadVbr(string text, string fallbackName = null)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string Line(int i) => i < lines.Length ? lines[i].Trim() : throw new BrushImportException("The parametric brush ends early (line " + (i + 1) + ").");
            double Number(int i, double min, double max)
            {
                if (!double.TryParse(Line(i), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsNaN(v) || v < min || v > max)
                    throw new BrushImportException("Line " + (i + 1) + " must be a number in " + min + ".." + max + ".");
                return v;
            }
            if (Line(0) != "GIMP-VBR") throw new BrushImportException("Not a GIMP parametric brush (missing GIMP-VBR).");
            string version = Line(1), name = Line(2);
            if (name.Length == 0) name = fallbackName ?? "Untitled";
            string shape = "circle"; int spikes = 2; double spacing, radius, hardness, aspect, angle;
            if (version == "1.0") { spacing = Number(3, 0, 5000); radius = Number(4, .1, 4000); hardness = Number(5, 0, 1); aspect = Number(6, 1, 20); angle = Number(7, 0, 180); }
            else if (version == "1.5")
            {
                shape = Line(3);
                if (shape != "circle" && shape != "square" && shape != "diamond") throw new BrushImportException("Unknown shape '" + shape + "'.");
                spacing = Number(4, 0, 5000); radius = Number(5, .1, 4000); spikes = (int)Number(6, 2, 20); hardness = Number(7, 0, 1); aspect = Number(8, 1, 20); angle = Number(9, 0, 180);
            }
            else throw new BrushImportException("Unsupported parametric brush version '" + version + "'.");
            var settings = new BrushSettings { Radius = radius, Hardness = hardness, Roundness = Math.Max(.01, 1 / aspect), Angle = angle, Spacing = Math.Max(.01, Math.Min(4, spacing / 100)) };
            var warnings = new List<string>();
            if (shape != "circle" || spikes > 2)
            {
                settings.Tip = RenderShape(name, shape, spikes, hardness);
                warnings.Add("The " + shape + (spikes > 2 ? " with " + spikes + " spikes" : "") + " shape is rendered into a tip image; edges may differ slightly from GIMP.");
            }
            return new ImportedBrush(name, "GIMP VBR " + version, settings, warnings);
        }

        static BrushSettings Settings(BrushTip[] tips, double spacingPercent)
        {
            int width = tips[0].Width, height = tips[0].Height;
            // GIMP spacing is a percentage of the brush width; this engine's is a fraction of the diameter (the larger side).
            double spacing = spacingPercent / 100 * width / Math.Max(width, height);
            var settings = new BrushSettings { Radius = Math.Max(.5, Math.Max(width, height) / 2.0), Spacing = Math.Max(.01, Math.Min(4, spacing)) };
            if (tips.Length == 1) settings.Tip = tips[0]; else settings.Tips = tips;
            return settings;
        }

        static BrushTip ReadOneGbr(BigEndianReader r, string fallbackName, out double spacing, out List<string> warnings)
        {
            warnings = new List<string>();
            int start = r.Position;
            uint headerSize = r.U32(), version = r.U32(), width = r.U32(), height = r.U32(), bytes = r.U32();
            if (width < 1 || height < 1 || width > BrushTip.MaxSize || height > BrushTip.MaxSize) throw new BrushImportException("Brush size " + width + "x" + height + " is outside 1.." + BrushTip.MaxSize + ".");
            string name;
            if (version == 1)
            {
                if (headerSize < 20) throw new BrushImportException("Invalid version 1 header size.");
                spacing = 25;
                name = Name(r.Bytes(r.Count(headerSize - 20, 1, "name length")), fallbackName);
            }
            else if (version == 2 || version == 3)
            {
                if (headerSize < 28) throw new BrushImportException("Invalid header size " + headerSize + ".");
                if (r.U32() != Magic) throw new BrushImportException("Missing the GIMP brush signature.");
                spacing = r.U32();
                if (headerSize - 28 > 256) throw new BrushImportException("The brush name is longer than 256 bytes.");
                name = Name(r.Bytes((int)(headerSize - 28)), fallbackName);
            }
            else throw new BrushImportException("Unsupported GIMP brush version " + version + ".");
            if (r.Position != start + headerSize) throw new BrushImportException("Header size does not match its fields.");
            if (bytes != 1 && bytes != 4)
                throw new BrushImportException(bytes == 18 ? "16-bit float (CinePaint) brushes are not supported." : "Unsupported pixel size " + bytes + ".");
            var pixels = r.Bytes(r.Count(width * height, (int)bytes, "pixel data") * (int)bytes);
            int w = (int)width, h = (int)height; var alpha = new byte[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                alpha[(h - 1 - y) * w + x] = bytes == 1 ? pixels[y * w + x] : pixels[(y * w + x) * 4 + 3]; // file rows are top-down
            if (bytes == 4) warnings.Add("A colour brush is imported as its alpha mask; its own colours are not kept (this engine paints with the chosen colour).");
            return new BrushTip(name, w, h, alpha);
        }

        static string Name(byte[] bytes, string fallback)
        {
            int length = Array.IndexOf(bytes, (byte)0); if (length < 0) length = bytes.Length;
            string name = new UTF8Encoding(false, false).GetString(bytes, 0, length).Trim();
            return name.Length > 0 ? name : (fallback ?? "Untitled");
        }
        static string Line(byte[] data, ref int position)
        {
            int end = Array.IndexOf(data, (byte)'\n', position);
            if (end < 0 || end - position > 4096) throw new BrushImportException("The image hose header is missing or too long.");
            string line = new UTF8Encoding(false, false).GetString(data, position, end - position).TrimEnd('\r');
            position = end + 1; return line;
        }

        static BrushTip RenderShape(string name, string shape, int spikes, double hardness)
        {
            const int size = 256; var alpha = new byte[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                double u = (x + .5) / size * 2 - 1, v = (y + .5) / size * 2 - 1, d;
                if (spikes > 2)
                {
                    // Star: fold into half a spike's sector (0 = along the spike) and pull the boundary in as the angle
                    // opens, so the valleys sit at 45% of the radius.
                    double half = Math.PI / spikes, a = Math.Atan2(v, u), r = Math.Sqrt(u * u + v * v);
                    a = Math.Abs((((a + half) % (2 * half)) + 2 * half) % (2 * half) - half); // 0 on a spike axis (the first along +x)
                    d = r * (1 + (1 / .45 - 1) * a / half);
                }
                else d = shape == "square" ? Math.Max(Math.Abs(u), Math.Abs(v)) : shape == "diamond" ? Math.Abs(u) + Math.Abs(v) : Math.Sqrt(u * u + v * v);
                double c = d > 1 ? 0 : d <= hardness ? 1 : Smooth((1 - d) / (1 - hardness));
                alpha[y * size + x] = MathUtil.ToByte(c);
            }
            return new BrushTip(name, size, size, alpha);
        }
        static double Smooth(double t) { return t * t * (3 - 2 * t); }
    }
}
