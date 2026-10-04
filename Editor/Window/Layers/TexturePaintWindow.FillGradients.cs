using System;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        internal void EnableFillGradient(bool enabled)
        {
            TryAction(() =>
            {
                if (stroke != null) return;
                var g = enabled ? NewGeneratorSettings(GeneratorType.ShapeGradient).WithBlend(GeneratorBlend.Replace).WithRamp(GradientRamp.Default) : null;
                document.EndCoalescing(); document.SetFillGradient(selectedLayer, channel, g); document.EndCoalescing();
                if (enabled) { FillGradientEditChannel = channel; FillGradientEditLayer = selectedLayer; }
                repaintPixels = true; Repaint();
            });
        }
        /// <summary>Direct fill gradient properties, called only by DrawFill. The source is independent for each channel.</summary>
        void DrawFillGradient(UiRows rows, PaintLayer layer)
        {
            if (channel == PaintChannel.Normal) return;
            bool enabled = layer.FillGradients.TryGetValue(channel, out var g);
            if (!enabled)
            {
                if (PaintGui.Button(Spot("fill.gradient.add", rows.Row()), L.Tr("Add World Space Gradient"))) EnableFillGradient(true);
                return;
            }
            PaintGui.GroupLabel(rows.Row(16), L.Tr("World Space Gradient"));
            if (PaintGui.Button(Spot("fill.gradient.remove", rows.Row()), L.Tr("Remove World Space Gradient"))) { EnableFillGradient(false); return; }
            var next = g;
            ChoiceDropdown(Spot("fill.gradient.shape", rows.Row()), L.TrIn("shape gradient", "Shape"), g.Volume.Shape, (GeneratorShape[])Enum.GetValues(typeof(GeneratorShape)), ShapeName,
                picked => ApplyFillGradient(layer.Id, channel, g.WithVolume(g.Volume.WithShape(picked))));
            bool editing = FillGradientEditLayer == layer.Id && FillGradientEditChannel == channel;
            if (PaintGui.Button(Spot("fill.gradient.edit", rows.Row()), L.Tr("Edit in 3D View"), editing))
            { FillGradientEditChannel = channel; FillGradientEditLayer = editing ? Guid.Empty : layer.Id; }
            var modes = UiRows.Split(rows.Row(), 2, 4);
            if (PaintGui.Button(modes[0], L.TrIn("shape gradient", "Move"), shapeGizmoMode == ShapeGizmoMode.Move)) ShapeGizmoMode = ShapeGizmoMode.Move;
            if (PaintGui.Button(modes[1], L.TrIn("shape gradient", "Rotate"), shapeGizmoMode == ShapeGizmoMode.Rotate)) ShapeGizmoMode = ShapeGizmoMode.Rotate;
            var v = g.Volume;
            var c = VectorRow(rows, "fill.gradient.center", L.Tr("Center"), v.CenterX, v.CenterY, v.CenterZ, .01f, "0.###", -ShapeVolume.MaxCoordinate, ShapeVolume.MaxCoordinate, L.Tr("Placement in the model root's space, in scene units."), 0);
            v = v.WithCenter(c[0], c[1], c[2]);
            var a = VectorRow(rows, "fill.gradient.rotation", L.TrIn("shape gradient", "Rotation"), v.RotationX, v.RotationY, v.RotationZ, 1, "0.#", -ShapeVolume.MaxAngle, ShapeVolume.MaxAngle, L.Tr("Euler angles in degrees, as Unity's Transform shows them (turned about Z, then X, then Y)."), 0);
            v = v.WithRotation(WrapAngle(a[0]), WrapAngle(a[1]), WrapAngle(a[2]));
            if (v.Shape == GeneratorShape.Box)
            {
                var size = VectorRow(rows, "fill.gradient.size", L.TrIn("shape gradient", "Size"), v.SizeX, v.SizeY, v.SizeZ, .01f, "0.###", ShapeVolume.MinSize, ShapeVolume.MaxSize, L.Tr("The box's full width along each of its own axes (scene units)."), 0);
                v = v.WithSize(size[0], size[1], size[2]);
            }
            else if (v.Shape == GeneratorShape.Sphere)
            {
                double radius = KeepNumber(Spot("fill.gradient.radius", rows.Row()), L.TrIn("shape gradient", "Radius"), v.SizeX / 2, .01f, "0.###", ShapeVolume.MinSize / 2, ShapeVolume.MaxSize / 2, L.Tr("The sphere's radius (scene units)."));
                v = v.WithSize(radius * 2, v.SizeY, v.SizeZ);
            }
            else
            {
                double width = KeepNumber(Spot("fill.gradient.width", rows.Row()), L.TrIn("shape gradient", "Width"), v.SizeY, .01f, "0.###", ShapeVolume.MinSize, ShapeVolume.MaxSize, L.Tr("How far the value takes to go from 0 behind the plane to 1 in front of it (scene units). The plane faces its own +Y."));
                v = v.WithSize(v.SizeX, width, v.SizeZ);
            }
            if (v.Shape != GeneratorShape.Plane) v = v.WithFalloff(PaintGui.KeepSlider(Spot("fill.gradient.falloff", rows.Row()), L.TrIn("shape gradient", "Falloff"), v.Falloff, 0, 1, "0", "%", null, true, 100));
            next = next.WithVolume(v).WithInvert(PaintGui.FitToggle(Spot("fill.gradient.invert", rows.Row()), L.TrIn("generator", "Invert"), next.Invert));
            var ch = channel; var doc = document;
            next = GradientRampRows(rows, layer.Id + ":" + ch, next, picked => ApplyFillGradient(layer.Id, ch, picked),
                () => document == doc && layer.FillGradients.TryGetValue(ch, out var settings) ? settings : null, IsScalarChannel(ch), optional: false);
            if (!next.Equals(g)) ApplyFillGradient(layer.Id, ch, next, coalesce: true);
            var status = document.GetFillGradientStatus(layer.Id, ch);
            if (!status.Active)
            {
                NoteRow(rows, layer.IsDecal ? L.Tr("Decal gradient unavailable; it is transparent. {0}", status.Reason) : L.Tr("Gradient unavailable; the fallback value is shown. {0}", status.Reason), NoteKind.Warning);
                if (PaintGui.Button(Spot("fill.gradient.bake", rows.Row()), L.Tr("Bake Mesh Maps…"))) TryAction(() => OpenMeshBakeWindow());
            }
        }
        internal float DrawFillGradientSectionOnly(Rect area)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            if (active == null || active.Kind != LayerKind.Fill) return 0;
            var rows = new UiRows(area, 6); DrawFillGradient(rows, active); return rows.Used;
        }
        void ApplyFillGradient(Guid layer, PaintChannel ch, GeneratorSettings settings, bool coalesce = false)
        { TryAction(() => { document.SetFillGradient(layer, ch, settings, coalesce); repaintPixels = true; Repaint(); }); }
    }
}
