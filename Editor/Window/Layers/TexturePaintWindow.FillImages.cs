using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 塗りつぶしレイヤーの画像と投影（Substance Painter の Fill の画像の欄と Projection）: チャンネルごとの画像の欄（プロジェクトのリソースを
    /// 選ぶ・アセットのパネルや Unity の Project ウィンドウからドラッグ・外す。色として読むかデータとして読むか＝リソースの色空間）と、層ごとの
    /// 投影の欄（UV・トライプラナー・平面・球・円柱、タイル・オフセット・回転・繰り返し、トライプラナーの混ぜる幅、モデルの上の置き場）。
    /// 置き場は 3D ビューで形のグラデーションと同じギズモ（<see cref="ShapeGizmo"/>）で動かせる（ボックス = 投影の範囲）。どの変更も文書の
    /// Undo に 1 回ずつ（数値の欄とギズモのドラッグは 1 回にまとめる）。リソースの色空間はプロジェクトのもの（名前と同じく Undo に入らない）。
    /// <para>文書へのつなぎ: 全部のテクスチャセットの文書に、このプロジェクトのリソース（<see cref="ImageResources"/>）を差す
    /// （<see cref="ConnectGeneratorInputs"/>）。リソースを消す前の問い合わせ（<see cref="ProjectResources.AddUsageProbe"/>）に、画像を使っている
    /// 層の名前を答える。</para>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        // ───────── プロジェクトのリソースとのつなぎ ─────────

        /// <summary>リソースを使っている層（全部のテクスチャセット）。消す前の問い合わせの答え。使っていなければ null。</summary>
        string FillImageUsage(Guid id)
        {
            var users = new List<string>();
            foreach (var set in textureSets)
            {
                var d = set == currentSet && document != null ? document : set.Document;
                if (d == null) continue;
                foreach (var layer in d.LayersUsingResource(id)) users.Add(textureSets.Count > 1 ? set.Name + ": " + layer : layer);
            }
            return users.Count == 0 ? null : string.Join(", ", users.Take(6)) + (users.Count > 6 ? " …" : "");
        }

        /// <summary>開いた・戻したプロジェクトで、リソースに無い画像を読む層の知らせ（無ければ null）。ID は残し、塗りつぶしの値を見せる。</summary>
        internal string MissingFillImageNote()
        {
            var lines = new List<string>();
            foreach (var set in textureSets)
            {
                var d = set == currentSet && document != null ? document : set.Document;
                if (d == null) continue;
                d.ImageResources = resources;
                foreach (var line in d.MissingFillImages()) lines.Add(textureSets.Count > 1 ? set.Name + ": " + line : line);
            }
            if (lines.Count == 0) return null;
            return L.Tr("{0} fill channel(s) read an image this project does not have; they show their fill value (the reference is kept): {1}", lines.Count, string.Join("; ", lines.Take(5)) + (lines.Count > 5 ? " …" : ""));
        }

        // ───────── チャンネルの画像の欄 ─────────

        static string ColorSpaceName(ResourceColorSpace space)
            => space == ResourceColorSpace.Srgb ? L.Tr("Colour (sRGB)") : space == ResourceColorSpace.Linear ? L.Tr("Data (linear)") : L.Tr("Not known (as stored)");
        static string ConversionText(FillImageConversion conversion, bool luminance, PaintChannel channel)
        {
            string how = conversion == FillImageConversion.LinearToSrgb ? L.Tr("A data image in a colour channel: its values are encoded to sRGB.")
                : channel == PaintChannel.Normal ? L.Tr("Normal: the values are read as stored (a tangent-space normal map, OpenGL Y+).")
                : FillImageColor.IsColorChannel(channel) ? L.Tr("The values are used as stored.")
                : L.Tr("A data channel: the values are used as stored, whatever the image's colour space.");
            return luminance ? how + " " + L.Tr("This channel takes the image's luminance.") : how;
        }

        /// <summary>今のチャンネルの画像の欄: 画像（サムネイルと名前。押すとプロジェクトの画像の一覧、ドラッグで落とせる）、外す、読み方（リソースの
        /// 色空間）、使えないときの理由。</summary>
        void DrawFillImage(UiRows rows, PaintLayer active)
        {
            bool has = active.FillImages.TryGetValue(channel, out var id);
            ImageResource image = has && ImageResources.TryGetImage(id, out var found) ? found : null;
            var row = rows.Row(24);
            PaintGui.Text(new Rect(row.x, row.y, PropertyLabelWidth, row.height), L.TrIn("fill", "Image"), PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            var box = Spot("fill.image", new Rect(row.x + PropertyLabelWidth, row.y, row.width - PropertyLabelWidth - (has ? 28 : 0), row.height));
            DrawImageBox(box, active, image, has ? id : (Guid?)null);
            if (has && PaintGui.IconButton(Spot("fill.image.clear", new Rect(row.xMax - 24, row.y, 24, row.height)), "close", L.Tr("Stop using the image (the channel shows its value again)"), false, GUI.enabled, 15))
                TryAction(() => document.SetFillImage(active.Id, channel, null));
            if (!has) return;
            if (image != null)
            {
                var cs = image.ColorSpace;
                ChoiceDropdown(Spot("fill.image.colorspace", rows.Row()), L.Tr("Read as"), cs, (ResourceColorSpace[])Enum.GetValues(typeof(ResourceColorSpace)), ColorSpaceName,
                    next => TryAction(() => { RequireNoStrokeForResources(); ImageResources.SetColorSpace(image.Id, next); }),
                    L.Tr("What the image's values are (the resource's colour space, for every layer that uses it; not an undo step). Data channels always use the values as stored; in a colour channel a data (linear) image is encoded to sRGB."));
            }
            FillImageStatus status = null;
            try { status = document.GetFillImageStatus(active.Id, channel); } catch (ArgumentException) { }
            if (status == null) return;
            if (status.Active) NoteRow(rows, ConversionText(status.Conversion, status.Luminance, channel), NoteKind.Info);
            else NoteRow(rows, L.Tr("The image is not projected; the channel shows its value: {0}", status.Reason), NoteKind.Warning);
        }

        /// <summary>画像の箱: サムネイルと名前（無ければ「なし」）。押すとこのプロジェクトの画像の一覧。アセットのパネルの格子・Unity の Project
        /// ウィンドウからのドロップを受ける（このプロジェクトに無ければ取り込んでから）。</summary>
        void DrawImageBox(Rect box, PaintLayer active, ImageResource image, Guid? id)
        {
            var e = Event.current; bool enabled = GUI.enabled && stroke == null;
            bool hover = enabled && box.Contains(e.mousePosition);
            bool dropping = enabled && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && box.Contains(e.mousePosition) && DroppedImageSource(out _, out _);
            PaintGui.Rounded(box, dropping ? PaintTheme.AccentDim : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 3);
            PaintGui.Outline(box, dropping || hover ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
            var thumb = new Rect(box.x + 3, box.y + 3, box.height - 6, box.height - 6);
            if (e.type == EventType.Repaint)
            {
                PaintGui.Checker(thumb, 3);
                if (image != null) { var t = CachedThumbnail(AssetKey(AssetSource.Project, image.Id.ToString("D")) + ":" + image.ContentHash, () => image.Content.Preview((int)AssetThumb)); if (t != null) GUI.DrawTexture(thumb, t, ScaleMode.ScaleToFit, true); }
                else PaintGui.Icon(thumb, id.HasValue ? "link_off" : "texture", PaintTheme.TextDim, 13);
                PaintGui.Outline(thumb, PaintTheme.Border, 1, 0);
            }
            string name = image != null ? image.Name : id.HasValue ? L.Tr("Missing image") : L.Tr("None");
            var label = new Rect(thumb.xMax + 6, box.y, box.xMax - thumb.xMax - 26, box.height);
            PaintGui.Text(label, PaintGui.Fit(name, label.width, PaintTheme.Label, false), PaintTheme.Label, !enabled ? PaintTheme.TextDisabled : image != null ? PaintTheme.Text : PaintTheme.TextDim);
            PaintGui.Icon(new Rect(box.xMax - 22, box.y, 20, box.height), "arrow_drop_down", PaintTheme.TextDim, 16);
            PaintGui.Tooltip(box, (image != null ? image.Name + " (" + image.Width + " × " + image.Height + ")\n" + SourceLabel(image) + "\n" : "") + L.Tr("Click to choose one of this project's images; drop one from the Assets panel or the Project window."));
            if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); OpenFillImageMenu(box, active.Id, channel); }
            if (dropping)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); var layerId = active.Id; var ch = channel; TryAction(() => SetFillImageFromDrop(layerId, ch)); }
                e.Use();
            }
        }

        /// <summary>ドラッグ中のものが画像として受け取れるか: アセットのパネルの格子の 1 つ（鍵）か、Unity の Project ウィンドウのテクスチャ。</summary>
        bool DroppedImageSource(out string key, out Texture2D texture)
        {
            key = DragAndDrop.GetGenericData(ResourceDragKey) as string; texture = null;
            if (key != null) return IsImageAsset(key);
            texture = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault(t => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(t)));
            return texture != null;
        }
        void SetFillImageFromDrop(Guid layerId, PaintChannel target)
        {
            if (!DroppedImageSource(out string key, out var texture)) return;
            if (key != null) SetFillImageFromAsset(layerId, target, key);
            else { var image = ImportUnityTexture(texture); SetFillImage(layerId, target, image.Id); }
        }

        /// <summary>アセットのパネルの 1 つ（このプロジェクトのリソース・Unity・自分の置き場・内蔵）をチャンネルの画像にする。このプロジェクトに
        /// 無ければ先に取り込む（取り込みは Undo に入らない。画像を差すのは 1 回の Undo）。</summary>
        internal void SetFillImageFromAsset(Guid layerId, PaintChannel target, string assetKey)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("A stroke is in progress."));
            if (!TryParseAssetKey(assetKey, out var source, out string id)) throw new ArgumentException("Not an asset: " + assetKey, nameof(assetKey));
            var image = ImportAsset(source, id);
            SetFillImage(layerId, target, image.Id);
            selectedAsset = AssetKey(AssetSource.Project, image.Id.ToString("D"));
        }

        /// <summary>層のチャンネルに、このプロジェクトの画像を差す（1 回の Undo）。知らせに読み方を添える。</summary>
        internal void SetFillImage(Guid layerId, PaintChannel target, Guid resourceId)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("A stroke is in progress."));
            var image = ImageResources.Get(resourceId);
            document.SetFillImage(layerId, target, resourceId);
            selectedLayer = layerId; repaintPixels = true;
            message = L.Tr("{0} now reads {1} in {2}.", document.GetLayer(layerId).Name, image.Name, L.Tr(target.ToString())); // 読み方は欄に出す
            Repaint();
        }

        void OpenFillImageMenu(Rect at, Guid layerId, PaintChannel target)
        {
            var menu = new PaintMenu(); var images = ImageResources.Images;
            var current = document.Layers.FirstOrDefault(l => l.Id == layerId)?.FillImages.TryGetValue(target, out var cur) == true ? cur : Guid.Empty;
            if (images.Count == 0) menu.AddDisabledItem(new GUIContent(L.Tr("No images")));
            foreach (var r in images)
            {
                var image = r;
                menu.AddItem(new GUIContent(image.Name + "  (" + image.Width + " × " + image.Height + ")"), image.Id == current, () => { TryAction(() => SetFillImage(layerId, target, image.Id)); Repaint(); });
            }
            if (current != Guid.Empty) { menu.AddSeparator(""); menu.AddItem(new GUIContent(L.Tr("None (use the value)")), false, () => { TryAction(() => document.SetFillImage(layerId, target, null)); Repaint(); }); }
            menu.DropDown(at);
        }

        // ───────── 投影の欄 ─────────

        static string ProjectionName(FillProjectionMode mode)
        {
            switch (mode)
            {
                case FillProjectionMode.Uv: return L.TrIn("projection", "UV");
                case FillProjectionMode.Triplanar: return L.TrIn("projection", "Tri-planar");
                case FillProjectionMode.Planar: return L.TrIn("projection", "Planar");
                case FillProjectionMode.Spherical: return L.TrIn("projection", "Spherical");
                case FillProjectionMode.Decal: return L.TrIn("projection", "Decal");
                default: return L.TrIn("projection", "Cylindrical");
            }
        }
        static string WrapName(FillWrap wrap) => wrap == FillWrap.Repeat ? L.TrIn("projection", "Repeat") : wrap == FillWrap.Clamp ? L.TrIn("projection", "Clamp to edge") : L.TrIn("projection", "Transparent");

        /// <summary>塗りつぶしの層の投影の欄。数値の欄・スライダーは 1 回の Undo にまとめる（マウスを離して区切る）。</summary>
        void DrawFillProjection(UiRows rows, PaintLayer active)
        {
            var p = active.Projection; var next = p;
            try
            {
                ChoiceDropdown(Spot("projection.mode", rows.Row()), L.Tr("Projection"), p.Mode, (FillProjectionMode[])Enum.GetValues(typeof(FillProjectionMode)), ProjectionName,
                    mode => TryAction(() => SetProjectionMode(active.Id, mode)),
                    L.Tr("UV: the image laid on the UV square. Tri-planar: three projections along the box's axes, mixed where the surface turns (no UV seams). Planar: through the box's front face. Spherical and cylindrical: around the box's centre. Decal: through the box's front face, only inside the box, cut out by the image's alpha."));
                ChoiceDropdown(Spot("projection.wrap", rows.Row()), L.TrIn("projection", "Outside"), p.Wrap, (FillWrap[])Enum.GetValues(typeof(FillWrap)), WrapName,
                    wrap => TryAction(() => document.SetFillProjection(active.Id, active.Projection.WithWrap(wrap))), L.Tr("Repeat the image, continue its edge pixels, or leave it transparent outside the image."));
                var c = PaintGui.LabeledColumns(rows.Row(), L.TrIn("projection", "Tiling"), ShapeVectorLabelWidth, 2);
                double tu = KeepNumber(Spot("projection.tile.u", c[0]), "U", p.TileU, .01f, "0.###", FillProjection.MinTiles, FillProjection.MaxTiles, L.Tr("How many times the image repeats across the projected square (U)"));
                double tv = KeepNumber(Spot("projection.tile.v", c[1]), "V", p.TileV, .01f, "0.###", FillProjection.MinTiles, FillProjection.MaxTiles, L.Tr("How many times the image repeats across the projected square (V)"));
                next = next.WithTiles(tu, tv);
                c = PaintGui.LabeledColumns(rows.Row(), L.TrIn("projection", "Offset"), ShapeVectorLabelWidth, 2);
                double ou = KeepNumber(Spot("projection.offset.u", c[0]), "U", p.OffsetU, .005f, "0.###", -FillProjection.MaxOffset, FillProjection.MaxOffset, L.Tr("Shifts the image (in image widths)"));
                double ov = KeepNumber(Spot("projection.offset.v", c[1]), "V", p.OffsetV, .005f, "0.###", -FillProjection.MaxOffset, FillProjection.MaxOffset, L.Tr("Shifts the image (in image heights)"));
                next = next.WithOffset(ou, ov);
                next = next.WithRotation(PaintGui.KeepSlider(Spot("projection.rotation", rows.SliderRow()), L.TrIn("projection", "Rotation"), p.Rotation, -180, 180, "0.#", "°", L.Tr("Turns the image counter-clockwise about the projected square's centre")));
                if (p.Mode == FillProjectionMode.Triplanar)
                    next = next.WithBlendWidth(PaintGui.KeepSlider(Spot("projection.blend", rows.SliderRow()), L.TrIn("projection", "Blend"), p.BlendWidth, 0, 1, "0", "%",
                        L.Tr("Where the surface turns between the box's axes: 0 %: a hard switch to the axis it faces most; 100 %: mixed in proportion to the normal."), true, 100));
            }
            catch (ArgumentException ex) { message = ex.Message; next = p; }
            if (!next.Equals(p)) TryAction(() => document.SetFillProjection(active.Id, next, coalesce: true));
            if (p.IsDecal && DrawDecalCulling(rows, active)) return; // デカールの間引きと、置けない理由（TexturePaintWindow.Decals.cs）
            if (p.Mode != FillProjectionMode.Uv) DrawProjectionPlacement(rows, active);
            // 使えない理由（マップが無いなど）: 画像のチャンネルのうち最初のもの
            foreach (var entry in active.FillImages.OrderBy(e => e.Key))
            {
                FillImageStatus status;
                try { status = document.GetFillImageStatus(active.Id, entry.Key); } catch (ArgumentException) { continue; }
                if (!status.Active) { NoteRow(rows, L.Tr("{0}: the image is not projected (the channel shows its value): {1}", L.Tr(entry.Key.ToString()), status.Reason), NoteKind.Warning); break; }
            }
        }

        /// <summary>投影の種類を変える。UV から型の上の投影に替えたとき、置き場が初めのままならモデルの外形に合わせ、3D ビューで動かせるようにする。</summary>
        internal void SetProjectionMode(Guid layerId, FillProjectionMode mode)
        {
            var layer = document.GetLayer(layerId); var p = layer.Projection;
            if (p.Mode == mode) return;
            var next = p.WithMode(mode);
            if (mode == FillProjectionMode.Decal && p.Wrap == FillWrap.Repeat && p.TileU == 1 && p.TileV == 1) next = next.WithWrap(FillWrap.None); // デカールは画像を 1 回
            if (mode != FillProjectionMode.Uv && p.Placement.Equals(FillProjection.DefaultPlacement) && preview != null && preview.HasModel) next = next.WithPlacement(ModelPlacement(mode));
            document.EndCoalescing();
            document.SetFillProjection(layerId, next);
            document.EndCoalescing();
            if (mode != FillProjectionMode.Uv) ProjectionHandlesHidden = false; // 型の上の投影を選んだら、置き場をすぐ動かせるように
        }

        /// <summary>モデルの外形に合わせた置き場（モデルのルートの空間）: 中心は外形の中心、大きさは外形（トライプラナーは最も長い辺の立方体で、
        /// 画像の縦横比を崩さない）。</summary>
        ShapeVolume ModelPlacement(FillProjectionMode mode)
        {
            if (preview == null || !preview.HasModel) return FillProjection.DefaultPlacement;
            if (mode == FillProjectionMode.Decal) return DecalFitPlacement(document.Layers.FirstOrDefault(l => l.Id == selectedLayer)); // ビューから見た正面（TexturePaintWindow.Decals.cs）
            var b = preview.Bounds; var inverse = Quaternion.Inverse(preview.ModelRootRotation);
            var center = inverse * (b.center - preview.ModelRootPosition); var size = inverse * b.size;
            double Size(float s) => Math.Max(ShapeVolume.MinSize, Math.Min(ShapeVolume.MaxSize, Math.Abs(s) * 1.02));
            double sx = Size(size.x), sy = Size(size.y), sz = Size(size.z);
            if (mode == FillProjectionMode.Triplanar || mode == FillProjectionMode.Spherical) sx = sy = sz = Math.Max(sx, Math.Max(sy, sz));
            return new ShapeVolume(GeneratorShape.Box, center.x, center.y, center.z, 0, 0, 0, sx, sy, sz, 0);
        }

        /// <summary>型の上の投影の置き場: 3D ビューのハンドルを出す・隠す（Q）・ギズモのモード・モデルに合わせる、中心・回転・大きさ（モデルのルートの
        /// 空間、シーンの単位）。ハンドルは層を選んでいる間に出る（マスクの編集中は出ない）ので、ボタンは Substance の Show/Hide manipulator と同じ切り替え。</summary>
        void DrawProjectionPlacement(UiRows rows, PaintLayer active)
        {
            var p = active.Projection; var v = p.Placement;
            bool editing = ProjectionEditLayer == active.Id;
            var row = rows.Row(24); float modes = 3 * 26 + 6;
            if (PaintGui.Button(Spot("projection.edit", new Rect(row.x, row.y, row.width - modes, row.height)), L.Tr("Handles in 3D View"), editing, GUI.enabled && stroke == null,
                    L.Tr("Show or hide the projection's box and its handles in the 3D view (Q)"), "view_in_ar"))
                ProjectionHandlesHidden = editing;
            if (PaintGui.IconButton(Spot("projection.move", new Rect(row.xMax - modes + 4, row.y, 26, row.height)), "transform", L.Tr("Handles: move (arrows along the model's axes, the square in the view's plane)"), shapeGizmoMode == ShapeGizmoMode.Move, GUI.enabled && editing, 16))
                ShapeGizmoMode = ShapeGizmoMode.Move;
            if (PaintGui.IconButton(Spot("projection.rotate", new Rect(row.xMax - modes + 32, row.y, 26, row.height)), "3d_rotation", L.Tr("Handles: rotate (rings about the model's axes; Ctrl uses Unity's Scene rotation increment)"), shapeGizmoMode == ShapeGizmoMode.Rotate, GUI.enabled && editing, 16))
                ShapeGizmoMode = ShapeGizmoMode.Rotate;
            if (PaintGui.IconButton(Spot("projection.fit", new Rect(row.xMax - 26, row.y, 26, row.height)), "target", L.Tr("Fit the box to the model (its bounds; a cube for tri-planar and spherical)"), false, GUI.enabled && stroke == null && preview != null && preview.HasModel, 16))
                TryAction(() => { document.EndCoalescing(); document.SetFillProjection(active.Id, active.Projection.WithPlacement(ModelPlacement(p.Mode))); document.EndCoalescing(); });
            if (EditingMask && !projectionHandlesHidden) NoteRow(rows, L.Tr("Handles hidden while the mask is edited"), NoteKind.Info);
            else if (editing && surfaceRect.width <= 0) NoteRow(rows, L.Tr("The 3D view is hidden."), NoteKind.Info);
            else if (editing && (preview == null || !preview.HasModel)) NoteRow(rows, L.Tr("No model in the 3D view."), NoteKind.Info);
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Placement"), L.Tr("In the model root's space: its position and rotation, in scene units (the root's scale is not applied). The same values as the handles in the 3D view."));
            var c = VectorRow(rows, "projection.center", L.Tr("Center"), v.CenterX, v.CenterY, v.CenterZ, .01f, "0.###", -ShapeVolume.MaxCoordinate, ShapeVolume.MaxCoordinate,
                L.Tr("The box's centre, from the model root (scene units)"), 0);
            var next = v.WithCenter(c[0], c[1], c[2]);
            var r = VectorRow(rows, "projection.rotation3d", L.TrIn("shape gradient", "Rotation"), v.RotationX, v.RotationY, v.RotationZ, 1, "0.#", -ShapeVolume.MaxAngle, ShapeVolume.MaxAngle,
                L.Tr("Euler angles in degrees, as Unity's Transform shows them (turned about Z, then X, then Y)."), 0);
            next = next.WithRotation(WrapAngle(r[0]), WrapAngle(r[1]), WrapAngle(r[2]));
            if (p.Mode != FillProjectionMode.Spherical)
            {
                var s = VectorRow(rows, "projection.size", L.TrIn("shape gradient", "Size"), v.SizeX, v.SizeY, v.SizeZ, .01f, "0.###", ShapeVolume.MinSize, ShapeVolume.MaxSize,
                    p.Mode == FillProjectionMode.Planar ? L.Tr("Planar: the image spans the box's X and Y (seen from its −Z side); Z is not used.")
                    : p.Mode == FillProjectionMode.Decal ? L.Tr("Decal: the image spans the box's X and Y (seen from its −Z side); Z is how deep it reaches into the surface.")
                    : p.Mode == FillProjectionMode.Cylindrical ? L.Tr("Cylindrical: the image goes once around the box's Y axis and spans its Y size; X and Z are not used.")
                    : L.Tr("Tri-planar: each face's image spans the box's two other sizes."), 0);
                next = next.WithSize(s[0], s[1], s[2]);
            }
            if (!next.Equals(v))
            {
                string why = next.Refusal();
                if (why != null) message = why; else TryAction(() => document.SetFillProjection(active.Id, active.Projection.WithPlacement(next), coalesce: true));
            }
        }

        // ───────── 書き出し・保存の知らせ ─────────

        /// <summary>書き出す前に、投影されていない画像（マップが無い・画像が無い）があれば確かめる。続けてよければ true。</summary>
        bool ConfirmInactiveFillImages(IEnumerable<(string set, PaintDocument document)> documents)
        {
            var lines = new List<string>(); bool named = textureSets.Count > 1;
            foreach (var (set, d) in documents) { d.ImageResources = resources; foreach (var line in d.InactiveFillImages()) lines.Add((named ? set + ": " : "") + line); }
            if (lines.Count == 0) return true;
            string list = string.Join("\n", lines.Take(12)) + (lines.Count > 12 ? "\n… " + (lines.Count - 12) : "");
            if (Dialogs.Confirm(L.Tr("Fill images not projected"), L.Tr("These fill channels cannot project their image now, so the exported images would show their fill value there instead (a decal: nothing):\n{0}\n\nFix them first (bake the mesh maps, choose an image), or export anyway?", list), L.Tr("Export Anyway"), L.Tr("Cancel")))
                return true;
            message = L.Tr("Nothing was exported: some fill images cannot be projected now.");
            return false;
        }
        /// <summary>保存の知らせに添える文（.ylp には画像の参照と投影がそのまま残る。合成の画像だけが塗りつぶしの値）。無ければ空。</summary>
        string InactiveFillImageSaveNote()
        {
            int count = textureSets.Sum(s => (s == currentSet ? document : s.Document)?.InactiveFillImages().Count ?? 0);
            return count == 0 ? "" : " " + L.Tr("{0} fill image channel(s) are not projected now; they are kept in the file, and the preview images inside it show the fill value.", count);
        }

        // ───────── 試験用 ─────────

        /// <summary>選んだ層の投影の欄だけを area に描く（欄と同じ部品と幅。オフスクリーンで見た目を確かめる試験用）。使った高さ。</summary>
        internal float DrawProjectionSectionOnly(Rect area)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            PaintGui.Fill(area, PaintTheme.PanelBg);
            if (active == null || active.Kind != LayerKind.Fill) return 0;
            var rows = new UiRows(area, 6);
            DrawFillProjection(rows, active);
            return rows.Used;
        }
        /// <summary>選んだ層の塗りつぶしの欄（値と画像）だけを area に描く（試験用）。使った高さ。</summary>
        internal float DrawFillSectionOnly(Rect area)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            PaintGui.Fill(area, PaintTheme.PanelBg);
            if (active == null || active.Kind != LayerKind.Fill) return 0;
            var rows = new UiRows(area, 6);
            DrawFill(rows, active);
            return rows.Used;
        }
    }
}
