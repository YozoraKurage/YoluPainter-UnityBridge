using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class IdAssignmentTests
    {
        internal static MeshBakeInput Pieces(params string[] materialKeys)
        {
            var triangles = new List<SurfaceTriangle>();
            for (int p = 0; p < materialKeys.Length; p++)
            {
                float x = p * 2;
                var uv = new Vector2(.02f + p * .25f, .1f);
                triangles.Add(new SurfaceTriangle(new Vector3(x,0,0), new Vector3(x,1,0), new Vector3(x+1,0,0), uv, uv+new Vector2(0,.3f), uv+new Vector2(.2f,0), p, p));
            }
            return TexturePaintWindow.BuildMeshBakeInput(new SurfaceGeometry(triangles), materialKeys: materialKeys);
        }
        internal static MeshBakeSettings Settings(MeshIdSource source = MeshIdSource.MaterialAsset) => new MeshBakeSettings
        { Width = 32, Height = 32, Maps = new[] { MeshMapKind.Id, MeshMapKind.Position }, TargetSlot = -1, Padding = 0, IdSource = source };
        static int ColorAt(BakedMeshMap map, int part)
        { Assert.That(IdMapColors.TryGetAtUv(map, .07 + part * .25, .18, out int rgb), Is.True); return rgb; }

        [Test] public void MaterialAssetsShareColoursAcrossSlotsAndTextureSetsAndNamesAreIrrelevant()
        {
            var input = Pieces("asset-a:1", "asset-a:1", "asset-b:1"); var s = Settings();
            var map = MeshBaker.Bake(input, s).Maps.Single(m=>m.Kind==MeshMapKind.Id);
            Assert.That(ColorAt(map,0), Is.EqualTo(ColorAt(map,1))); Assert.That(ColorAt(map,2), Is.Not.EqualTo(ColorAt(map,0)));
            for (int slot = 0; slot < 3; slot++)
            { s.TargetSlot = slot; Assert.That(ColorAt(MeshBaker.Bake(input,s).Maps.Single(m=>m.Kind==MeshMapKind.Id),slot), Is.EqualTo(ColorAt(map,slot))); }
            var missing = MeshBaker.Bake(Pieces(null,null), Settings()).Maps.Single(m=>m.Kind==MeshMapKind.Id);
            Assert.That(ColorAt(missing,0), Is.Not.EqualTo(ColorAt(missing,1)), "missing identities retain separate slots");
        }
        [Test] public void MaterialIdentityAndManualEditsMakeOnlyTheIdMapStale()
        {
            var input = Pieces("asset-a:1", "asset-b:1"); var other = Pieces("asset-a:1", "asset-a:1"); var s = Settings();
            var baked = MeshBaker.Bake(input,s).Maps.ToDictionary(m=>m.Kind);
            Assert.That(other.Hash, Is.EqualTo(input.Hash), "other map inputs are unchanged");
            var expected = new MeshMapExpectation { MeshHash = input.Hash, TopologyHash = input.TopologyHash, Width = 32, Height = 32, TargetSlot = -1,
                Settings = s.WithIdContext(other,null,IdColorAssignments.Empty) };
            Assert.That(baked[MeshMapKind.Id].Provenance.Check(expected).State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(baked[MeshMapKind.Position].Provenance.Check(expected).State, Is.EqualTo(MeshMapState.Current));
            var parts = new IdPartIndex(input); var colors = IdColorAssignments.Empty.WithColor(parts.Binding,0,0x123456);
            expected.Settings = s.WithIdContext(input,null,colors);
            Assert.That(baked[MeshMapKind.Id].Provenance.Check(expected).State, Is.EqualTo(MeshMapState.Stale));
            Assert.That(baked[MeshMapKind.Position].Provenance.Check(expected).State, Is.EqualTo(MeshMapState.Current));
        }
        [Test] public void ManualPartColoursOverrideEverySourceAndTheReferenceAndAreDeterministic()
        {
            var input = Pieces("a:1", "b:1"); var parts = new IdPartIndex(input);
            var colors = IdColorAssignments.Empty.WithColor(parts.Binding,0,0x203040).WithColor(parts.Binding,1,0x203040);
            foreach (MeshIdSource source in Enum.GetValues(typeof(MeshIdSource)))
            {
                var s = Settings(source); s.ManualIdColors = colors;
                var map = MeshBaker.Bake(input,s,reference: input).Maps.Single(m=>m.Kind==MeshMapKind.Id);
                Assert.That(ColorAt(map,0), Is.EqualTo(0x203040), source.ToString()); Assert.That(ColorAt(map,1), Is.EqualTo(0x203040));
                Assert.That(MeshMapBinary.Write(MeshBaker.Bake(input,s,reference: input).Maps.Single(m=>m.Kind==MeshMapKind.Id)), Is.EqualTo(MeshMapBinary.Write(map)));
            }
        }
        [Test] public void ManualColoursRoundTripUndoRedoAndResizeWithoutChangingPixels()
        {
            var d = new PaintDocument(32,32,16); var layer = d.AddLayer("Paint"); d.ClearHistory();
            var parts = new IdPartIndex(Pieces("a:1", "b:1")); var colors = IdColorAssignments.Empty.WithColor(parts.Binding,0,0x123456);
            var before = DocumentBinary.Write(d); long allocated = d.AllocatedBytes;
            d.SetIdColors(colors); Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(d.AllocatedBytes, Is.EqualTo(allocated));
            var bytes = DocumentBinary.Write(d); Assert.That(BitConverter.ToInt32(bytes,8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var loaded = DocumentBinary.Read(bytes); Assert.That(loaded.IdColors, Is.EqualTo(colors)); Assert.That(loaded.UndoCount, Is.Zero);
            Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(bytes));
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(bytes));
            Assert.That(d.Resampled(64,64,CanvasResampling.Nearest).Document.IdColors, Is.EqualTo(colors));
        }
        [Test] public void OldNativeLayoutReadsWithNoAssignmentsAndEmptyCurrentLayoutOnlyChangesTheVersion()
        {
            var d = new PaintDocument(32,32,16); d.AddLayer("Paint");
            var current = DocumentBinary.Write(d); var old = (byte[])current.Clone(); BitConverter.GetBytes(16).CopyTo(old,8);
            var loaded = DocumentBinary.Read(old); Assert.That(loaded.IdColors.Colors, Is.Empty); Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(current));
        }
        [Test] public void InvalidTypesCountsAndActiveStrokesRefuseWithoutChanges()
        {
            var d = new PaintDocument(32,32,16); var layer = d.AddLayer("Paint"); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.That(() => new IdColorAssignments("bad",new Dictionary<int,int>{{0,1}}), Throws.ArgumentException);
            foreach (var pair in new[] { (-1,1), (0,-1), (0,0x1000000) })
                Assert.That(() => new IdColorAssignments(new string('a',64),new Dictionary<int,int>{{pair.Item1,pair.Item2}}), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => new IdColorAssignments(new string('a',64),Enumerable.Range(0,IdColorAssignments.MaxParts+1).ToDictionary(x=>x,x=>0)), Throws.ArgumentException);
            Assert.That(() => d.SetIdColors(null), Throws.ArgumentNullException);
            using (var stroke = d.BeginStroke(layer.Id,PaintChannel.Color,new BrushSettings()))
                Assert.That(() => d.SetIdColors(IdColorAssignments.Empty), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
        }
        [Test] public void AnotherPartitionAndInsufficientBakeBudgetAreRefused()
        {
            var input = Pieces("a:1", "b:1"); var parts = new IdPartIndex(input); var s = Settings();
            s.ManualIdColors = IdColorAssignments.Empty.WithColor(parts.Binding,0,0x123456);
            Assert.That(() => MeshBaker.Bake(Pieces("a:1"),s), Throws.TypeOf<MeshBakeRefusedException>());
            Assert.That(() => MeshBaker.Bake(input,s,new MeshBakeBudget { MaxBytes = 1 }), Throws.TypeOf<MeshBakeRefusedException>());
            Assert.That(s.ManualIdColors.Colors[0], Is.EqualTo(0x123456));
        }
        [Test] public void MalformedNativeAssignmentsAreNeverDroppedSilently()
        {
            var d = new PaintDocument(32,32,16); var before = DocumentBinary.Write(d); int at = before.Length;
            d.SetIdColors(IdColorAssignments.Empty.WithColor(new string('a',64),0,0x123456).WithColor(new string('a',64),1,0x654321));
            var bytes = DocumentBinary.Write(d);
            foreach (var edit in new Action<byte[]>[] {
                b=>BitConverter.GetBytes(0).CopyTo(b,at), b=>BitConverter.GetBytes(0).CopyTo(b,at+4), b=>BitConverter.GetBytes(IdColorAssignments.MaxParts+1).CopyTo(b,at+4),
                b=>b[at+12]=(byte)'z', b=>BitConverter.GetBytes(-1).CopyTo(b,at+76), b=>BitConverter.GetBytes(0x1000000).CopyTo(b,at+80), b=>BitConverter.GetBytes(0).CopyTo(b,at+84) })
            { var bad = (byte[])bytes.Clone(); edit(bad); Assert.That(()=>DocumentBinary.Read(bad),Throws.TypeOf<InvalidDataException>()); }
            Assert.That(()=>DocumentBinary.Read(bytes.Take(bytes.Length-1).ToArray()),Throws.TypeOf<InvalidDataException>());
            var older = (byte[])bytes.Clone(); BitConverter.GetBytes(16).CopyTo(older,8);
            Assert.That(()=>DocumentBinary.Read(older),Throws.TypeOf<InvalidDataException>());
        }
    }
}
