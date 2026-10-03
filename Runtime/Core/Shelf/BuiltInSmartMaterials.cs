using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// YoluPainter's own smart materials and smart masks ("Built-in" in the Assets panel): made by code from fill values and generators
    /// only, so no image is shipped and they look the same at any size (nothing in them is measured in pixels). The same key and version
    /// always make the same fragment (fixed IDs; the tests pin the native bytes' hash). A version is raised when one changes, so a project
    /// that took the older copy can tell (its origin records the version). Generators read the texture set's baked mesh maps; without
    /// them a built-in shows its base layer only, and the window says which maps to bake.
    /// </summary>
    public static class BuiltInSmartMaterials
    {
        public sealed class Entry
        {
            public string Key { get; }
            public int Version { get; }
            public SmartKind Kind { get; }
            /// <summary>The English name (the UI translates it).</summary>
            public string Name { get; }
            /// <summary>What it is for, in English (the UI translates it).</summary>
            public string Description { get; }
            internal readonly Action<Builder> Make;
            internal Entry(string key, int version, SmartKind kind, string name, string description, Action<Builder> make)
            { Key = key; Version = version; Kind = kind; Name = name; Description = description; Make = make; }
        }

        /// <summary>The fragment's size: anything works (no pixels, no pixel radii); placing resamples it to the texture set's size.</summary>
        const int Size = 1024, Tile = 128;
        static Rgba32 Grey(byte v) => new Rgba32(v, v, v, 255);

        public static readonly IReadOnlyList<Entry> All = new[]
        {
            new Entry("rusty-metal", 1, SmartKind.Material, "Rusty metal", "Bare steel with rust in the cavities and in patches towards the bottom, and shiny worn edges (reads AO, curvature and position)", b =>
            {
                b.Fill("Steel", new Rgba32(124, 126, 130, 255), rough: 105, metal: 255);
                var rust = b.Fill("Rust", new Rgba32(122, 58, 28, 255), rough: 225, metal: 0);
                b.MaskGenerator(rust, GeneratorSettings.Default(GeneratorType.Dirt).WithLevels(.12, .55, .6).WithNoise(.55, .08, 7, GeneratorNoiseSpace.Model));
                // 平らな面にも、下ほど多い錆の斑（くぼみの錆と大きいほうを取る）
                b.MaskGenerator(rust, GeneratorSettings.Default(GeneratorType.PositionGradient).WithLevels(0, .8, .5).WithInvert(true).WithNoise(.85, .07, 13, GeneratorNoiseSpace.Model).WithBlend(GeneratorBlend.Max));
                var edges = b.Fill("Worn edges", new Rgba32(196, 196, 200, 255), rough: 60, metal: 255);
                b.MaskGenerator(edges, GeneratorSettings.Default(GeneratorType.EdgeWear));
            }),
            new Entry("dirty-paint", 1, SmartKind.Material, "Dirty paint", "Painted surface with grime in the cavities and towards the bottom, and dust on the faces that look up (reads AO, curvature, world normal and position)", b =>
            {
                b.Fill("Paint", new Rgba32(52, 96, 150, 255), rough: 150, metal: 0);
                var grime = b.Fill("Grime", new Rgba32(62, 52, 40, 255), rough: 215, metal: 0);
                b.MaskGenerator(grime, GeneratorSettings.Default(GeneratorType.Dirt).WithLevels(.2, .65, .5).WithNoise(.5, .06, 11, GeneratorNoiseSpace.Model));
                b.MaskGenerator(grime, GeneratorSettings.Default(GeneratorType.PositionGradient).WithLevels(0, .45, .5).WithInvert(true).WithNoise(.7, .05, 29, GeneratorNoiseSpace.Model).WithBlend(GeneratorBlend.Max));
                var dust = b.Fill("Dust", new Rgba32(150, 140, 120, 255), rough: 235, metal: 0, opacity: .6);
                b.MaskGenerator(dust, GeneratorSettings.Default(GeneratorType.Direction).WithLevels(.72, .95, .5).WithNoise(.6, .05, 23, GeneratorNoiseSpace.Model));
            }),
            new Entry("worn-edges", 1, SmartKind.Material, "Chipped paint edges", "Red paint chipped off the edges down to bare metal (reads curvature and position)", b =>
            {
                b.Fill("Paint", new Rgba32(170, 40, 35, 255), rough: 120, metal: 0);
                var metal = b.Fill("Chipped edges", new Rgba32(176, 176, 180, 255), rough: 70, metal: 255);
                b.MaskGenerator(metal, GeneratorSettings.Default(GeneratorType.EdgeWear).WithLevels(.03, .22, .5).WithNoise(.7, .05, 5, GeneratorNoiseSpace.Model));
            }),
            new Entry("edges", 1, SmartKind.Mask, "Edges", "Shows the layer on convex edges, worn away in patches (curvature and position)", b => b.Mask(GeneratorSettings.Default(GeneratorType.EdgeWear))),
            new Entry("cavities", 1, SmartKind.Mask, "Cavities", "Shows the layer in cavities and occluded corners (AO, curvature and position)", b => b.Mask(GeneratorSettings.Default(GeneratorType.Dirt))),
            new Entry("facing-up", 1, SmartKind.Mask, "Facing up", "Shows the layer on faces that look up, like dust or snow (world normal and position)", b => b.Mask(GeneratorSettings.Default(GeneratorType.Direction))),
            new Entry("bottom-to-top", 1, SmartKind.Mask, "Bottom to top", "Fades the layer in from the bottom of the model to the top (position)", b => b.Mask(GeneratorSettings.Default(GeneratorType.PositionGradient))),
        };

        public static bool TryGet(string key, out Entry entry) { entry = All.FirstOrDefault(e => e.Key == key); return entry != null; }

        /// <summary>The smart material of a built-in entry (made each time; the content is the same every time).</summary>
        public static SmartMaterial Make(string key)
        {
            if (!TryGet(key, out var entry)) throw new ResourceRefusedException(ResourceRefusal.Unknown, "No built-in smart material \"" + key + "\".");
            var builder = new Builder(entry);
            entry.Make(builder);
            return builder.Finish();
        }

        /// <summary>Builds a fragment with IDs derived from the key (so the bytes do not change between runs).</summary>
        internal sealed class Builder
        {
            readonly Entry entry; readonly PaintDocument fragment; int ids;
            internal Builder(Entry entry)
            {
                this.entry = entry;
                fragment = new PaintDocument(Size, Size, Tile, 0, Id());
            }
            Guid Id()
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("yolupainter/built-in-smart/" + entry.Key + "/" + entry.Version + "/" + ids++));
                    var g = new byte[16]; Array.Copy(bytes, g, 16);
                    g[7] = (byte)(g[7] & 0x0f | 0x40); g[8] = (byte)(g[8] & 0x3f | 0x80); // 版 4 の形
                    return new Guid(g);
                }
            }
            internal PaintLayer Fill(string name, Rgba32 color, byte rough, byte metal, double opacity = 1)
            {
                var values = new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, color }, { PaintChannel.Roughness, Grey(rough) }, { PaintChannel.Metallic, Grey(metal) } };
                var layer = fragment.AddFillLayer(name, values, Id());
                if (opacity != 1) fragment.SetLayerOpacity(layer.Id, opacity);
                return layer;
            }
            internal void MaskGenerator(PaintLayer layer, GeneratorSettings generator)
            {
                if (layer.Mask == null) fragment.AddLayerMask(layer.Id);
                fragment.AddFilter(layer.Id, FilterTarget.Mask, FilterSettings.FromGenerator(generator), null, -1, Id());
            }
            internal void Mask(GeneratorSettings generator)
            {
                var holder = fragment.AddFillLayer(entry.Name, null, Id());
                MaskGenerator(holder, generator);
            }
            internal SmartMaterial Finish()
            {
                fragment.ClearHistory();
                return new SmartMaterial(entry.Kind, entry.Name, fragment, null, null);
            }
        }
    }
}
