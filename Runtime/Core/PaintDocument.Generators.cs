using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// Generator stages read the texture set's baked mesh maps, which live outside the document. The editor connects them through
    /// <see cref="GeneratorInputs"/>; the document resolves every map kind once per input revision into an immutable snapshot that
    /// evaluations (and their worker threads) read, and stamps generator output with the snapshot's revision.
    /// <para>Change tracking: whenever the resolved maps differ from the previous snapshot (a map baked, removed, loaded, gone
    /// stale or current again, another provider), every layer with a generator in its content or mask stack is reported as changed
    /// through <see cref="TryGetChangedTiles"/>, so the compositors redraw it and no tile mixes old and new maps. The document
    /// looks at the inputs when it is asked for changed tiles or a composite (<see cref="TryGetChangedTiles"/>,
    /// <see cref="CpuCompositor"/>), when a stack with a generator is edited, undone or redone, before baking, and when
    /// <see cref="RefreshGeneratorInputs"/> is called; not per tile.</para>
    /// <para>A generator whose maps are not usable (no inputs connected, missing, stale, unverified, another size, another bake than
    /// its pin) passes its input through; <see cref="GetGeneratorStatus(GeneratorSettings)"/> says why. Nothing reads a missing map as
    /// black.</para>
    /// </summary>
    public sealed partial class PaintDocument
    {
        static readonly int MapKinds = MaxMapKind() + 1;
        static int MaxMapKind() { int max = 0; foreach (MeshMapKind k in Enum.GetValues(typeof(MeshMapKind))) max = Math.Max(max, (int)k); return max; }

        IGeneratorInputs generatorInputs;
        bool generatorResolved, generatorEverResolved;
        long generatorInputsSeen, generatorRevision = 1;
        BakedMeshMap[] generatorMaps = new BakedMeshMap[MapKinds];
        string[] generatorReasons = new string[MapKinds];
        /// <summary>Where the model root is in the maps' space (<see cref="IGeneratorModelFrame"/>; identity when the inputs do not say),
        /// or null when the inputs failed to say (shape gradients then pass their input through).</summary>
        GeneratorModelFrame generatorFrame = GeneratorModelFrame.Identity; string generatorFrameReason;

        /// <summary>Where generators read the texture set's mesh maps (null: nothing connected, every generator passes its input
        /// through). Setting another provider reads the maps again and reports the generator layers as changed if they differ.
        /// Not part of history or persistence.</summary>
        public IGeneratorInputs GeneratorInputs
        {
            get { return generatorInputs; }
            set
            {
                if (ReferenceEquals(value, generatorInputs)) return;
                generatorInputs = value; generatorResolved = false;
                PollGeneratorInputs();
            }
        }

        /// <summary>Reads the inputs again if their revision changed. Returns true when the maps the generators read changed (the
        /// generator layers were then reported as changed).</summary>
        public bool RefreshGeneratorInputs() { return PollGeneratorInputs(); }

        /// <summary>The revision of what evaluations read from outside the document: the resolved maps (generators, fill projections on
        /// the model) and the fill layers' resolved images. Changes whenever any of them changes; reading it brings them up to date first.
        /// Display caches keyed by <see cref="Revision"/> can add it to their key.</summary>
        public long GeneratorInputsRevision { get { PollGeneratorInputs(); return inputsRevision; } }

        /// <summary>True when a layer has a generator in its content or mask stack (on or off).</summary>
        public bool HasGenerators
        {
            get { foreach (var layer in layers) if (LayerHasGenerator(layer)) return true; return false; }
        }
        static bool LayerHasGenerator(PaintLayer layer)
        {
            foreach (var e in layer.FilterList) if (e.Settings.IsGenerator) return true;
            if (layer.Mask != null) foreach (var e in layer.Mask.FilterList) if (e.Settings.IsGenerator) return true;
            return false;
        }
        static bool ContainsGenerator(FilterEffect[] stack) { foreach (var e in stack) if (e.Settings.IsGenerator) return true; return false; }

        /// <summary>Re-resolves the maps when the document has generators and the inputs' revision changed (or nothing was resolved
        /// since the provider was set). True when the resolved maps changed.</summary>
        internal bool PollGeneratorInputs()
        {
            bool images = PollImageResources(); // 塗りつぶしの画像（プロジェクトのリソース）も同じ所で見る
            if (!HasGenerators && !HasMapProjections) return images;
            long seen = InputsRevision();
            if (generatorResolved && seen == generatorInputsSeen) return images;
            return ResolveGeneratorInputs(seen) || images;
        }
        long InputsRevision()
        {
            if (generatorInputs == null) return long.MinValue;
            try { return generatorInputs.Revision; }
            catch (Exception) { return long.MinValue + 1; } // 壊れた口: 下の TryGetMap も理由付きで使えないと答える
        }

        bool ResolveGeneratorInputs(long seen)
        {
            var maps = new BakedMeshMap[MapKinds]; var reasons = new string[MapKinds];
            foreach (MeshMapKind kind in Enum.GetValues(typeof(MeshMapKind)))
            {
                int k = (int)kind;
                if (generatorInputs == null) { reasons[k] = "No mesh maps are connected to this document (open it in the YoluPainter window, where its texture set's baked maps are)."; continue; }
                try
                {
                    if (!generatorInputs.TryGetMap(kind, out var map, out var why) || map == null) { reasons[k] = why ?? kind + " has not been baked."; continue; }
                    if (map.Kind != kind) { reasons[k] = "The texture set returned a " + map.Kind + " map for " + kind + "."; continue; }
                    if (map.Width != Width || map.Height != Height) { reasons[k] = kind + " was baked at " + map.Width + "×" + map.Height + "; this texture set is " + Width + "×" + Height + ". Bake it again."; continue; }
                    maps[k] = map;
                }
                catch (Exception ex) { reasons[k] = "Reading the " + kind + " map failed: " + ex.Message; }
            }
            GeneratorModelFrame frame = GeneratorModelFrame.Identity; string frameReason = null;
            if (generatorInputs is IGeneratorModelFrame framed)
                try { frame = framed.ModelFrame; if (frame == null) frameReason = "Where the model root is is not known (load the model)."; }
                catch (Exception ex) { frame = null; frameReason = "Reading where the model root is failed: " + ex.Message; }
            bool changed = !generatorEverResolved || !Equals(frame, generatorFrame);
            for (int k = 0; k < MapKinds && !changed; k++) changed = !ReferenceEquals(maps[k], generatorMaps[k]);
            generatorMaps = maps; generatorReasons = reasons; generatorFrame = frame; generatorFrameReason = frameReason;
            generatorInputsSeen = seen; generatorResolved = true; generatorEverResolved = true;
            if (!changed) return false;
            generatorRevision++; inputsRevision++;
            MarkGeneratorLayersChanged();
            return true;
        }

        /// <summary>Reports every layer with a generator (or a fill projected on the model) as changed and forgets its cached filtered tiles.</summary>
        void MarkGeneratorLayersChanged()
        {
            bool any = false;
            foreach (var layer in layers)
            {
                if (!LayerHasGenerator(layer) && !layer.ReadsMeshMapsForFill) continue;
                any = true;
                foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) FilterEvaluator.Forget(layer.Id, (int)c);
                FilterEvaluator.Forget(layer.Id, FilterEngine.MaskKey);
                MarkLayerChanged(layer, null);
            }
            if (any) MarkClippedLayersChanged();
        }

        /// <summary>The resolved maps by kind (an immutable snapshot), the model root's frame and their revision, resolving them first
        /// if nothing was resolved since the provider was set. Evaluations read this; they do not poll the inputs per tile.</summary>
        internal IReadOnlyList<BakedMeshMap> GeneratorMapSnapshot(out long revision, out GeneratorModelFrame frame)
        {
            if (!generatorResolved) ResolveGeneratorInputs(InputsRevision());
            revision = generatorRevision; frame = generatorFrame;
            return generatorMaps;
        }

        /// <summary>What the generator would read now: each map it uses (or why it cannot), and whether it has an effect.</summary>
        public GeneratorStatus GetGeneratorStatus(GeneratorSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            PollGeneratorInputs();
            if (!generatorResolved) ResolveGeneratorInputs(InputsRevision());
            var uses = new List<GeneratorMapUse>();
            bool all = true;
            foreach (var kind in settings.UsedMaps)
            {
                var map = generatorMaps[(int)kind]; string reason = map == null ? generatorReasons[(int)kind] : null;
                settings.Pins.TryGetValue(kind, out var pin);
                var available = map;
                if (map != null && pin != null && map.Provenance.ConditionKey != pin)
                {
                    reason = kind + " was baked again under other conditions; this generator is pinned to an earlier bake (" + pin.Substring(0, 12) + "…). Pin the current bake or follow the latest one.";
                    map = null;
                }
                if (map == null) all = false;
                uses.Add(new GeneratorMapUse(kind, map, reason, pin, available));
            }
            string extra = null;
            if (all && BoundGenerator.Bind(settings, generatorMaps, generatorFrame, Width, Height, out var why) == null) extra = generatorFrameReason != null && settings.Type == GeneratorType.ShapeGradient ? generatorFrameReason : why;
            return new GeneratorStatus(uses.AsReadOnly(), extra);
        }
        /// <summary>The status of a generator stage of a layer (content or mask stack).</summary>
        public GeneratorStatus GetGeneratorStatus(Guid layerId, Guid filterId)
        {
            var effect = FindFilter(layerId, filterId, out _);
            if (effect == null) throw new KeyNotFoundException("Filter not found on layer: " + filterId);
            if (!effect.Settings.IsGenerator) throw new ArgumentException(effect.Settings.Name + " is not a generator.", nameof(filterId));
            return GetGeneratorStatus(effect.Settings.Generator);
        }

        /// <summary>One line per generator that is switched on but has no effect now (its maps are not usable), naming the layer, the
        /// stack and the reason. Empty when every generator that is on has its maps. Exports and saves list these instead of writing
        /// a composite without the generators' effect silently.</summary>
        public IReadOnlyList<string> InactiveGenerators()
        {
            var notes = new List<string>();
            if (!HasGenerators) return notes;
            foreach (var layer in layers)
            {
                foreach (var e in layer.FilterList) Note(layer, e, false);
                if (layer.Mask != null) foreach (var e in layer.Mask.FilterList) Note(layer, e, true);
            }
            return notes;
            void Note(PaintLayer layer, FilterEffect e, bool mask)
            {
                if (!e.Settings.IsGenerator || !e.IsActive) return;
                var status = GetGeneratorStatus(e.Settings.Generator);
                if (!status.Active) notes.Add("'" + layer.Name + "'" + (mask ? " (mask)" : "") + ": " + e.Settings.Name + " has no effect: " + status.Reason);
            }
        }

        /// <summary>Refuses to bake a stack that has a generator switched on without usable maps (baking would drop its effect).</summary>
        void RefuseInactiveGenerators(PaintLayer layer, bool content, bool mask)
        {
            PollGeneratorInputs();
            var stacks = new List<(IEnumerable<FilterEffect> effects, string where)>();
            if (content) stacks.Add((layer.FilterList, ""));
            if (mask && layer.Mask != null) stacks.Add((layer.Mask.FilterList, " (mask)"));
            foreach (var (effects, where) in stacks)
                foreach (var e in effects)
                {
                    if (!e.Settings.IsGenerator || !e.IsActive) continue;
                    var status = GetGeneratorStatus(e.Settings.Generator);
                    if (!status.Active)
                        throw new InvalidOperationException("'" + layer.Name + "'" + where + ": " + e.Settings.Name + " cannot be baked because it has no effect now (" + status.Reason
                            + ") Baking would leave its effect out. Bake the mesh maps again (or turn the generator off) first. Nothing was changed.");
                }
        }
    }
}
