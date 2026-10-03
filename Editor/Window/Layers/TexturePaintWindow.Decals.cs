using System;
using System.Linq;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// デカール（Substance Painter の平面の投影を箱で切り抜いたもの）: 塗りつぶしの層の投影の種類「デカール」（<see cref="FillProjectionMode.Decal"/>）。
    /// アセットのパネル（か Unity の Project ウィンドウ）の画像を 3D ビューのモデルへ落とすと、落とした所の面に向けて、画像の縦横比の大きさで
    /// 置く（選んだ層の上に新しい層、1 回の Undo。画像は今のチャンネルへ）。デカールの層を選んでいる間は、投影の置き場と同じギズモで動かせる
    /// （TexturePaintWindow.ShapeGizmo.cs、1 回の Undo、Esc・フォーカスの喪失で戻す。マスクの編集中と Q で隠したときは出ない）。プロパティの投影の欄で奥行きと裏向きの間引きを変え、
    /// 2D キャンバスには選んだデカールの届く範囲を薄く重ねる。表示だけで、文書には何も足さない。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>落とした画像の長い辺が、3D ビューの見えている高さ（落とした所の奥行きで）のこの割合になる。</summary>
        internal const float DecalViewFraction = .3f;
        /// <summary>デカールの奥行き（箱の Z）の、長い辺に対する割合。面から手前と奥へ半分ずつ。</summary>
        internal const float DecalDepthFraction = .5f;
        static readonly Color DecalOverlayTint = new Color(1f, .62f, .2f, 1f);
        const float DecalOverlayOpacity = .42f;
        /// <summary>2D に重ねる範囲の格子の長い辺（テクスチャセットがこれより小さければその大きさ）。</summary>
        const int DecalOverlayMaxSide = 512;

        // ───────── 置く ─────────

        /// <summary>3D ビューの GUI の点の下の、今のテクスチャセットの面。無ければ false と理由。</summary>
        bool DecalTarget(Vector2 gui, out SurfaceHit hit, out string why)
        {
            why = null; hit = default;
            if (preview == null || !preview.HasModel || surfaceRect.width <= 0) { why = L.Tr("Load a model (or the demo cube) and show the 3D view to place a decal."); return false; }
            if (!preview.TryPick(surfaceRect, gui, out hit)) { why = L.Tr("Drop the image on the model to place a decal there."); return false; }
            if (hit.MaterialSlot != materialSlot)
            {
                var owner = SetOfSlot(hit.MaterialSlot);
                why = owner != null ? L.Tr("That face belongs to the texture set {0}. Switch to it (double-click the face) to place a decal there.", owner.Name) : L.Tr("That face belongs to no texture set of this project.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 3D ビューの GUI の点の面に、プロジェクトの画像のデカールを置く: 選んだ層の上に、今のチャンネルにその画像（と画像を使えないときの値）を持つ
        /// 塗りつぶしの層を作り、投影をデカールにして落とした所の面に向ける（1 回の Undo）。置いた層を選び、置き場をギズモで動かせるようにする。
        /// 置けない（モデルの外・ほかのテクスチャセットの面）ときは何も変えずに理由を投げる。
        /// </summary>
        internal Guid PlaceDecal(Guid resourceId, Vector2 gui)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            var image = ImageResources.Get(resourceId);
            if (!DecalTarget(gui, out var hit, out string why)) throw new InvalidOperationException(why);
            var projection = FillProjection.DecalAt(DecalPlacement(hit, gui, image.Width, image.Height));
            Guid layerId = Guid.Empty; var target = channel;
            document.Batch(() =>
            {
                var layer = document.AddFillLayer(image.Name, new System.Collections.Generic.Dictionary<PaintChannel, Rgba32> { { target, PaintDocument.DefaultFillImageFallback(target) } }, above: AboveSelected());
                document.SetFillImage(layer.Id, target, resourceId);
                document.SetFillProjection(layer.Id, projection);
                layerId = layer.Id;
            });
            selectedLayer = layerId; editMask = false; repaintPixels = true;
            ProjectionHandlesHidden = false; // 選んだデカールのハンドルを出して、置いたらすぐ動かせるように
            string problem = document.GetDecalProblem(layerId);
            message = problem == null ? L.Tr("Placed {0} as a decal in {1}. Drag the handles in the 3D view to move, turn or size it.", image.Name, L.Tr(target.ToString()))
                : L.Tr("Placed {0} as a decal in {1}, but it is not shown yet: {2}", image.Name, L.Tr(target.ToString()), problem);
            Repaint();
            return layerId;
        }

        /// <summary>
        /// 当たった面に向けたデカールの置き場（モデルのルートの空間）: 中心は当たった点、−Z の面が面の外を向く（+Z は法線の逆）、+Y は 3D ビューの
        /// 上を面に写した向き（ビューから見て画像が立つ）。長い辺は見えている高さの <see cref="DecalViewFraction"/>、短い辺は画像の縦横比、奥行きは
        /// 長い辺の <see cref="DecalDepthFraction"/>。
        /// </summary>
        internal ShapeVolume DecalPlacement(SurfaceHit hit, Vector2 gui, int imageWidth, int imageHeight)
        {
            var inverse = Quaternion.Inverse(preview.ModelRootRotation);
            Vector3 center = inverse * (hit.Position - preview.ModelRootPosition);
            Vector3 normal = (inverse * hit.Normal).normalized;
            if (normal.sqrMagnitude < .5f) normal = Vector3.back;
            // ビューの上と、見えている高さ（落とした所の奥行きで）
            Vector3 up = Vector3.up; float height = preview.ModelRadius;
            if (preview.TryGuiRay(surfaceRect, gui, out var ray) && preview.TryGuiRay(surfaceRect, gui - new Vector2(0, 1), out var above)
                && preview.TryGuiRay(surfaceRect, new Vector2(gui.x, surfaceRect.yMin), out var top) && preview.TryGuiRay(surfaceRect, new Vector2(gui.x, surfaceRect.yMax), out var bottom))
            {
                up = inverse * (above.direction - ray.direction);
                float distance = Mathf.Max(1e-4f, hit.Distance);
                height = Vector3.Distance(top.direction.normalized, bottom.direction.normalized) * distance;
            }
            Vector3 forward = -normal; // +Z は面の中へ
            up -= Vector3.Dot(up, forward) * forward;
            if (up.sqrMagnitude < 1e-10f) { up = Vector3.Cross(forward, Mathf.Abs(forward.y) < .9f ? Vector3.up : Vector3.right); up -= Vector3.Dot(up, forward) * forward; }
            var rotation = Quaternion.LookRotation(forward, up.normalized).eulerAngles;
            double longest = Math.Max(ShapeVolume.MinSize, Math.Min(ShapeVolume.MaxSize, height * DecalViewFraction));
            double sx = imageWidth >= imageHeight ? longest : longest * imageWidth / Math.Max(1, imageHeight);
            double sy = imageHeight >= imageWidth ? longest : longest * imageHeight / Math.Max(1, imageWidth);
            return new ShapeVolume(GeneratorShape.Box, center.x, center.y, center.z, WrapAngle(rotation.x), WrapAngle(rotation.y), WrapAngle(rotation.z),
                Math.Max(ShapeVolume.MinSize, sx), Math.Max(ShapeVolume.MinSize, sy), Math.Max(ShapeVolume.MinSize, longest * DecalDepthFraction), 0);
        }

        /// <summary>デカールをモデルに合わせる（投影の欄の「モデルに合わせる」・ほかの投影からデカールにしたとき）: 外形の中心に、3D ビューから見て
        /// 正面を向け（ビューが無ければ −Z から）、外形の短い辺の半分の大きさ（形の画像の縦横比）、奥行きは外形を突き抜ける長さ。</summary>
        ShapeVolume DecalFitPlacement(PaintLayer layer)
        {
            var b = preview.Bounds; var inverse = Quaternion.Inverse(preview.ModelRootRotation);
            Vector3 center = inverse * (b.center - preview.ModelRootPosition);
            Vector3 forward = Vector3.forward, up = Vector3.up;
            var middle = surfaceRect.center;
            if (surfaceRect.width > 0 && preview.TryGuiRay(surfaceRect, middle, out var ray) && preview.TryGuiRay(surfaceRect, middle - new Vector2(0, 1), out var above))
            { forward = inverse * ray.direction; up = inverse * (above.direction - ray.direction); }
            up -= Vector3.Dot(up, forward) * forward;
            if (up.sqrMagnitude < 1e-10f) up = Vector3.Cross(forward, Mathf.Abs(forward.y) < .9f ? Vector3.up : Vector3.right);
            var rotation = Quaternion.LookRotation(forward, up.normalized).eulerAngles;
            double side = Math.Max(ShapeVolume.MinSize, Math.Min(b.size.x, Math.Min(b.size.y, b.size.z)) * .5), depth = Math.Max(ShapeVolume.MinSize, b.size.magnitude * 1.02);
            var shape = layer?.FillImages.OrderBy(e => e.Key).Select(e => ImageResources.TryGetImage(e.Value, out var image) ? image : null).FirstOrDefault();
            double sx = side, sy = side;
            if (shape != null && shape.Width > 0 && shape.Height > 0) { if (shape.Width >= shape.Height) sy = side * shape.Height / shape.Width; else sx = side * shape.Width / shape.Height; }
            return new ShapeVolume(GeneratorShape.Box, center.x, center.y, center.z, WrapAngle(rotation.x), WrapAngle(rotation.y), WrapAngle(rotation.z), sx, sy, Math.Min(ShapeVolume.MaxSize, depth), 0);
        }

        // ───────── 投影の欄 ─────────

        /// <summary>デカールの間引き（奥行きの縁・裏向きの角度と縁）と、置けない理由（マップのベイクのボタン付き）。置けないときは置き場の欄を
        /// 出さずに true（理由だけ）。</summary>
        bool DrawDecalCulling(UiRows rows, PaintLayer active)
        {
            var p = active.Projection; var next = p;
            try
            {
                double depth = PaintGui.KeepSlider(Spot("decal.depth", rows.Row()), L.TrIn("decal", "Depth edge"), 1 - p.DepthHardness, 0, 1, "0", "%",
                    L.Tr("How softly the decal fades towards the box's front and back faces (0 %: a hard cut)."), true, 100);
                double angle = PaintGui.KeepSlider(Spot("decal.angle", rows.Row()), L.TrIn("decal", "Back faces"), p.BackfaceAngle, 0, FillProjection.MaxBackfaceAngle, "0", "°",
                    L.Tr("Faces turned more than this from the side the image is seen from are hidden (180°: none)."));
                double soft = PaintGui.KeepSlider(Spot("decal.angle.edge", rows.Row()), L.TrIn("decal", "Face edge"), 1 - p.BackfaceHardness, 0, 1, "0", "%",
                    L.Tr("How softly faces fade out towards that angle (0 %: a hard cut)."), true, 100);
                next = p.WithCulling(1 - depth, angle, 1 - soft);
            }
            catch (ArgumentException ex) { message = ex.Message; next = p; }
            if (!next.Equals(p)) TryAction(() => document.SetFillProjection(active.Id, next, coalesce: true));
            string problem = null;
            try { problem = document.GetDecalProblem(active.Id); } catch (ArgumentException) { }
            if (problem == null)
            {
                NoteRow(rows, L.Tr("The decal shows inside its box on the model, cut out by the alpha of its first image (Color first); every channel's value shows in that shape."), NoteKind.Info);
                return false;
            }
            NoteRow(rows, L.Tr("The decal is not shown: {0}", problem), NoteKind.Warning);
            if (preview != null && preview.CanPaint && PaintGui.Button(Spot("decal.bake", rows.Row(24)), L.Tr("Bake Position and World Normal"), false, GUI.enabled && stroke == null,
                    L.Tr("Bakes the two mesh maps a decal reads (for the checked texture sets), keeping the other maps."), "deployed_code"))
                TryAction(() => BakeDecalMaps());
            DrawProjectionPlacement(rows, active);
            return true;
        }

        /// <summary>デカールが読む Position と World Normal だけを焼く（ほかのマップの選びは変えない。焼いた種類だけ置き換わる）。</summary>
        internal MeshBakeStatus? BakeDecalMaps()
        {
            var chosen = meshBakeSettings.Maps;
            meshBakeSettings.Maps = new[] { MeshMapKind.Position, MeshMapKind.WorldNormal };
            try { return BakeMeshMaps(); }
            finally { meshBakeSettings.Maps = chosen; repaintPixels = true; Repaint(); }
        }

        // ───────── 2D キャンバスに範囲を重ねる ─────────

        Texture2D decalOverlay; (Guid layer, long fill, long inputs, int w, int h) decalOverlayKey;

        /// <summary>選んだ層がデカールなら、届く範囲（箱・奥行き・面の向き。画像の形は入れない）を 2D キャンバスに薄く重ねる。</summary>
        void DrawDecalOverlay(Rect image)
        {
            if (Event.current.type != EventType.Repaint) return;
            var texture = EnsureDecalOverlay(); if (texture == null) return;
            var previous = GUI.color; GUI.color = new Color(1, 1, 1, DecalOverlayOpacity);
            GUI.DrawTexture(image, texture, ScaleMode.StretchToFill, true);
            GUI.color = previous;
        }
        /// <summary>重ねる範囲のテクスチャ（デカールの中身か入力が変わったときだけ作り直す）。選んだ層がデカールでない・置けないときは null。</summary>
        internal Texture2D EnsureDecalOverlay()
        {
            var layer = document?.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            if (layer == null || !layer.IsDecal) return null;
            float scale = Mathf.Min(1, DecalOverlayMaxSide / (float)Mathf.Max(document.Width, document.Height));
            int w = Mathf.Max(1, Mathf.RoundToInt(document.Width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(document.Height * scale));
            var key = (layer.Id, layer.FillRevision, document.GeneratorInputsRevision, w, h);
            if (decalOverlay != null && decalOverlayKey.Equals(key)) return decalOverlayKeyHasCoverage ? decalOverlay : null;
            var coverage = document.DecalCoverage(layer.Id, w, h);
            decalOverlayKey = key; decalOverlayKeyHasCoverage = coverage != null;
            if (coverage == null) return null;
            if (decalOverlay == null || decalOverlay.width != w || decalOverlay.height != h)
            {
                DisposeDecalOverlay();
                decalOverlay = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "YoluPainter decal overlay" };
            }
            var rgba = new byte[w * h * 4];
            byte r = (byte)(DecalOverlayTint.r * 255), g = (byte)(DecalOverlayTint.g * 255), b = (byte)(DecalOverlayTint.b * 255);
            for (int i = 0; i < coverage.Length; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = coverage[i]; }
            decalOverlay.LoadRawTextureData(rgba); decalOverlay.Apply(false, false);
            return decalOverlay;
        }
        bool decalOverlayKeyHasCoverage;
        void DisposeDecalOverlay() { if (decalOverlay != null) { DestroyImmediate(decalOverlay); decalOverlay = null; } decalOverlayKey = default; }
    }
}
