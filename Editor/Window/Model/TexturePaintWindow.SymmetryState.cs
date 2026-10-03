using System;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        internal sealed partial class BrushState
        {
            // schema 3 の追加項目。2D 中心は解像度に依存しない比率で保存し、描画時に画素へ直す。
            public bool symmetry3D, radialSymmetry3D, symmetryIgnoreVisibility;
            public SymmetryAxis symmetryAxis = SymmetryAxis.X, radialSymmetryAxis = SymmetryAxis.Y;
            public float symmetryOffset;
            public int radialSymmetryCount = 2;
            public bool symmetryAxesShown = true;
            public CanvasSymmetryMode canvasSymmetry;
            public float canvasSymmetryX = .5f, canvasSymmetryY = .5f;
            public int canvasSymmetryCount = 2;
        }
        [SerializeField, FormerlySerializedAs("symmetry")] bool legacySymmetry;
        [SerializeField, FormerlySerializedAs("symmetryAxis")] SymmetryAxis legacySymmetryAxis;
        [SerializeField, FormerlySerializedAs("symmetryOffset")] float legacySymmetryOffset;
        [SerializeField, FormerlySerializedAs("symmetryPlaneShown")] bool legacySymmetryPlaneShown = true;
        [SerializeField] bool symmetryStateMigrated;
        void MigrateSymmetryState()
        {
            if (symmetryStateMigrated) return;
            if (legacySymmetry || legacySymmetryAxis != SymmetryAxis.X || legacySymmetryOffset != 0 || !legacySymmetryPlaneShown)
            {
                brush.symmetry3D = legacySymmetry; brush.symmetryAxis = legacySymmetryAxis;
                brush.symmetryOffset = legacySymmetryOffset; brush.symmetryAxesShown = legacySymmetryPlaneShown;
            }
            symmetryStateMigrated = true;
        }
        static void ValidateSymmetryState(BrushState b)
        {
            if (!Enum.IsDefined(typeof(SymmetryAxis), b.symmetryAxis) || !Enum.IsDefined(typeof(SymmetryAxis), b.radialSymmetryAxis) ||
                !Enum.IsDefined(typeof(CanvasSymmetryMode), b.canvasSymmetry) || b.radialSymmetryCount < 2 || b.radialSymmetryCount > 16 ||
                b.canvasSymmetryCount < 2 || b.canvasSymmetryCount > 16 || !Finite(b.symmetryOffset) || Math.Abs(b.symmetryOffset) > MaxSymmetryOffset ||
                !Finite(b.canvasSymmetryX) || !Finite(b.canvasSymmetryY) || b.canvasSymmetryX < 0 || b.canvasSymmetryX > 1 || b.canvasSymmetryY < 0 || b.canvasSymmetryY > 1)
                throw new InvalidDataException("Unsupported symmetry brush settings.");
        }
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static void CopySymmetry(BrushState a, BrushState b)
        {
            b.symmetry3D = a.symmetry3D; b.symmetryAxis = a.symmetryAxis; b.symmetryOffset = a.symmetryOffset;
            b.radialSymmetry3D = a.radialSymmetry3D; b.radialSymmetryAxis = a.radialSymmetryAxis; b.radialSymmetryCount = a.radialSymmetryCount;
            b.symmetryIgnoreVisibility = a.symmetryIgnoreVisibility; b.symmetryAxesShown = a.symmetryAxesShown;
            b.canvasSymmetry = a.canvasSymmetry; b.canvasSymmetryX = a.canvasSymmetryX; b.canvasSymmetryY = a.canvasSymmetryY; b.canvasSymmetryCount = a.canvasSymmetryCount;
        }
        CanvasSymmetrySettings CanvasSymmetryNow() => new CanvasSymmetrySettings { Mode = brush.canvasSymmetry,
            CenterX = brush.canvasSymmetryX * (double)document.Width, CenterY = brush.canvasSymmetryY * (double)document.Height, Count = brush.canvasSymmetryCount };
        bool HasSurfaceSymmetry => symmetry || brush.radialSymmetry3D;
        internal bool RadialSymmetry3D { get => brush.radialSymmetry3D; set { brush.radialSymmetry3D = value; Repaint(); } }
        internal SymmetryAxis RadialSymmetryAxis { get => brush.radialSymmetryAxis; set { brush.radialSymmetryAxis = value; Repaint(); } }
        internal int RadialSymmetryCount { get => brush.radialSymmetryCount; set { brush.radialSymmetryCount = Mathf.Clamp(value, 2, 16); Repaint(); } }
        internal bool SymmetryIgnoreVisibility { get => brush.symmetryIgnoreVisibility; set { brush.symmetryIgnoreVisibility = value; Repaint(); } }
        RadialSymmetry CurrentRadialSymmetry => RadialSymmetry.FromModel(preview.ModelRootPosition, preview.ModelRootRotation, brush.radialSymmetryAxis, brush.radialSymmetryCount);
    }
}
