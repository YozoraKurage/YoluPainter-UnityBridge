using System;
using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// The preview of a smart material or smart mask (the Assets panel's thumbnail and the .ylsmart's thumbnail.png): it is placed on a
    /// small swatch — a sphere with a shallow cross-shaped groove, seen from the front — whose mesh maps are made by
    /// formulas (normal, position, curvature from the normals' divergence, ambient occlusion deeper in the groove, constant thickness),
    /// so generators show what they do (edge wear on the bevels, dirt in the groove, a direction on the faces that look up). The colour
    /// (or, without one, the first channel it uses) is lit from the upper left with a simple diffuse and specular term from roughness
    /// and metallic. A smart mask shows its visibility, light on dark. Deterministic (integer-free doubles in a fixed order on one
    /// thread); not a physically based render.
    /// </summary>
    public static class SmartPreview
    {
        /// <summary>Straight RGBA8, bottom-left origin, size × size. Outside the swatch is transparent.</summary>
        public static byte[] Render(SmartMaterial material, int size = 64)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (size < 16 || size > 512) throw new ArgumentOutOfRangeException(nameof(size), "A preview is 16–512 pixels.");
            var swatch = Swatch.Make(size);
            var d = new PaintDocument(size, size, material.TileSize, 0) { GeneratorInputs = swatch };
            if (material.Images.Count > 0)
            {
                // 塗りつぶしの層の画像は、スマートマテリアルが持つ画像（層が使う ID のまま）から
                var images = new ProjectResources { BudgetBytes = long.MaxValue };
                foreach (var image in material.Images) images.Restore(image.Id, image.Name, image.Content, ResourceOrigin.None, image.ColorSpace);
                d.ImageResources = images;
            }
            byte[] albedo, rough = null, metal = null; bool mask = material.Kind == SmartKind.Mask;
            if (mask)
            {
                d.AddFillLayer("dark", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(46, 48, 56, 255) } });
                var light = d.AddFillLayer("light", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(226, 226, 232, 255) } });
                d.ApplySmartMask(material, light.Id, CanvasResampling.Area);
                albedo = d.Composite(PaintChannel.Color);
            }
            else
            {
                d.PlaceSmartMaterial(material, new SmartPlacement { Resampling = CanvasResampling.Area });
                var main = material.Channels.Contains(PaintChannel.Color) ? PaintChannel.Color : material.Channels.Count > 0 ? material.Channels[0] : PaintChannel.Color;
                albedo = d.Composite(main);
                if (material.Channels.Contains(PaintChannel.Roughness)) rough = d.Composite(PaintChannel.Roughness);
                if (material.Channels.Contains(PaintChannel.Metallic)) metal = d.Composite(PaintChannel.Metallic);
            }
            return Shade(swatch, albedo, rough, metal, size);
        }

        static byte[] Shade(Swatch s, byte[] albedo, byte[] rough, byte[] metal, int n)
        {
            var result = new byte[n * n * 4];
            double lx = -.45, ly = .55, lz = .70, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
            double hx = lx, hy = ly, hz = lz + 1, hl = Math.Sqrt(hx * hx + hy * hy + hz * hz); hx /= hl; hy /= hl; hz /= hl;
            for (int i = 0; i < n * n; i++)
            {
                if (!s.Inside[i]) continue;
                int o = i * 4;
                double a = albedo[o + 3] / 255.0;
                // 透明なところは中間の灰色の上に置く
                double r = (albedo[o] * a + 128 * (1 - a)) / 255, g = (albedo[o + 1] * a + 128 * (1 - a)) / 255, b = (albedo[o + 2] * a + 128 * (1 - a)) / 255;
                double roughness = rough == null ? .6 : (rough[o] * rough[o + 3] / 255.0 + 153 * (1 - rough[o + 3] / 255.0)) / 255;
                double metallic = metal == null ? 0 : metal[o] * metal[o + 3] / (255.0 * 255);
                double nx = s.Nx[i], ny = s.Ny[i], nz = s.Nz[i];
                double diffuse = .28 + .72 * Math.Max(0, nx * lx + ny * ly + nz * lz);
                double exponent = 4 + (1 - roughness) * (1 - roughness) * 140, strength = .08 + (1 - roughness) * .9;
                double spec = strength * Math.Pow(Math.Max(0, nx * hx + ny * hy + nz * hz), exponent);
                double ambient = metallic * (.5 + .3 * ny); // 金属は周りを映すので、上からの空の明るさを足す
                // 拡散は金属ほど弱く、鏡面の色は金属なら下地の色・そうでなければ白っぽい灰色
                double Channel(double c) => c * (1 - metallic) * diffuse + (metallic * c + (1 - metallic) * .35) * spec + c * ambient;
                result[o] = MathUtil.ToByte(Channel(r)); result[o + 1] = MathUtil.ToByte(Channel(g)); result[o + 2] = MathUtil.ToByte(Channel(b)); result[o + 3] = 255;
            }
            return result;
        }

        /// <summary>The swatch's mesh maps (an <see cref="IGeneratorInputs"/> with every kind a generator reads).</summary>
        sealed class Swatch : IGeneratorInputs
        {
            internal bool[] Inside; internal double[] Nx, Ny, Nz;
            readonly Dictionary<MeshMapKind, BakedMeshMap> maps = new Dictionary<MeshMapKind, BakedMeshMap>();
            public long Revision => 1;
            public bool TryGetMap(MeshMapKind kind, out BakedMeshMap map, out string reason)
            {
                reason = maps.TryGetValue(kind, out map) ? null : "The preview swatch has no " + kind + " map.";
                return map != null;
            }

            const double Margin = .06, Bevel = .09, Groove = .035;

            internal static Swatch Make(int n)
            {
                var s = new Swatch { Inside = new bool[n * n], Nx = new double[n * n], Ny = new double[n * n], Nz = new double[n * n] };
                int m = n + 2; var height = new double[m * m]; var depth = new double[m * m]; var inside = new bool[m * m];
                for (int y = 0; y < m; y++)
                    for (int x = 0; x < m; x++)
                    {
                        double u = (x - 1 + .5) / n, v = (y - 1 + .5) / n;
                        double radius = .5 - Margin, r2 = (u - .5) * (u - .5) + (v - .5) * (v - .5);
                        double d = radius - Math.Sqrt(r2);
                        int k = y * m + x;
                        if (d <= 0) { inside[k] = false; height[k] = 0; continue; }
                        inside[k] = true;
                        double h = Math.Sqrt(Math.Max(0, radius * radius - r2));
                        double g = Math.Min(Math.Abs(u - .5), Math.Abs(v - .5));
                        if (d > Bevel && g < Groove) { double gd = Math.Sqrt(Groove * Groove - g * g); h -= gd; depth[k] = gd / Groove; }
                        height[k] = h;
                    }
                var nx = new double[m * m]; var ny = new double[m * m]; var nz = new double[m * m];
                double step = 1.0 / n;
                for (int y = 1; y <= n; y++)
                    for (int x = 1; x <= n; x++)
                    {
                        int k = y * m + x;
                        double du = (height[k + 1] - height[k - 1]) / (2 * step), dv = (height[k + m] - height[k - m]) / (2 * step);
                        double l = Math.Sqrt(du * du + dv * dv + 1);
                        nx[k] = -du / l; ny[k] = -dv / l; nz[k] = 1 / l;
                    }
                var normal = new ushort[n * n * 3]; var position = new ushort[n * n * 3]; var curvature = new ushort[n * n];
                var ao = new ushort[n * n]; var thickness = new ushort[n * n]; var coverage = new byte[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int k = (y + 1) * m + x + 1, i = y * n + x;
                        if (!inside[k]) continue;
                        s.Inside[i] = true; s.Nx[i] = nx[k]; s.Ny[i] = ny[k]; s.Nz[i] = nz[k];
                        coverage[i] = (byte)MeshTexelCoverage.Covered;
                        normal[i * 3] = U16(nx[k] * .5 + .5); normal[i * 3 + 1] = U16(ny[k] * .5 + .5); normal[i * 3 + 2] = U16(nz[k] * .5 + .5);
                        position[i * 3] = U16((x + .5) / n); position[i * 3 + 1] = U16((y + .5) / n); position[i * 3 + 2] = U16((height[k] + Groove) / (.5 - Margin + Groove));
                        // 法線の xy の発散（凸で正）を曲率の 0〜1 に。端の 1 画素は内側の値を使う
                        double div = (Get(nx, inside, k + 1, k) - Get(nx, inside, k - 1, k)) / step + (Get(ny, inside, k + m, k) - Get(ny, inside, k - m, k)) / step;
                        curvature[i] = U16(.5 + Math.Max(-.5, Math.Min(.5, div * .035)));
                        ao[i] = U16(1 - .75 * depth[k]);
                        thickness[i] = U16(.5);
                    }
                double[] min = { 0, 0, -Groove }, max = { 1, 1, .5 - Margin };
                s.Put(MeshMapKind.WorldNormal, n, normal, coverage, min, max);
                s.Put(MeshMapKind.BentNormal, n, (ushort[])normal.Clone(), coverage, min, max);
                s.Put(MeshMapKind.Position, n, position, coverage, min, max);
                s.Put(MeshMapKind.Curvature, n, curvature, coverage, min, max);
                s.Put(MeshMapKind.AmbientOcclusion, n, ao, coverage, min, max);
                s.Put(MeshMapKind.Thickness, n, thickness, coverage, min, max);
                return s;
            }
            static double Get(double[] values, bool[] inside, int k, int fallback) => inside[k] ? values[k] : values[fallback];
            static ushort U16(double v) => (ushort)Math.Round(Math.Max(0, Math.Min(1, v)) * 65535);
            void Put(MeshMapKind kind, int n, ushort[] data, byte[] coverage, double[] min, double[] max)
            {
                var provenance = new MeshMapProvenance(kind, MeshBaker.EngineVersion, "preview-swatch", "preview-swatch", 0, n, n, 0, 0, 1, "preview-swatch", MeshBaker.Space, MeshBaker.Pose, MeshBaker.Source, min, max);
                maps[kind] = new BakedMeshMap(provenance, data, (byte[])coverage.Clone());
            }
        }
    }
}
