using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// テクスチャセット（スロット）ごとに、マテリアル表示で何を見せるかを選ぶ（Substance の Shader settings に当たる）: 元のマテリアル
    /// （前の版と同じ）・プロジェクトのマテリアルのアセット・シェーダー（既定の値の新しいマテリアルをプレビューの中だけに作る）。塗った
    /// チャンネルの流し込み先は、確かめた対応（lilToon・ビルトインの Standard）か、名前からの推し量り（書き出しのテンプレートと同じ名前）で
    /// 自動に決め、欄でチャンネルごとに手で変えられる。どれもプレビューの複製だけで、元のマテリアル・選んだマテリアル・シェーダーのアセットは
    /// 変えない。選びは窓の状態（.ylp には入らない。正本の版は変えない）で、セットの ID で覚える。消えたアセット・シェーダーは元のマテリアルに
    /// 戻して知らせる。マテリアルの欄（<see cref="PreviewMaterialEdits"/>）は選んだマテリアルの複製に効く。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] List<PreviewMaterialChoice> previewMaterialChoices = new List<PreviewMaterialChoice>();
        /// <summary>シェーダーから作った、プレビューの中だけのマテリアル（HideAndDontSave。スクリプトの読み直しをまたいで残し、欄の変更も残る。
        /// 窓を閉じたら捨てる）。</summary>
        [SerializeField] List<MadeMaterial> madeMaterials = new List<MadeMaterial>();
        [Serializable] sealed class MadeMaterial { public string set = "", shader = ""; public Material material; }
        /// <summary>選んだマテリアルのアセットを読んだもの（GUID から毎回読まないため。消えたら Unity の null になる）。</summary>
        readonly Dictionary<string, Material> resolvedChoices = new Dictionary<string, Material>();

        static string SetKey(TextureSet set) => set.Id.ToString("N");
        TextureSet SetOrCurrent(TextureSet set) { SyncCurrentSet(); return set ?? currentSet; }

        /// <summary>セットの選び（選んでいなければ null = 元のマテリアル・自動の流し込み）。</summary>
        internal PreviewMaterialChoice MaterialChoice(TextureSet set = null)
        {
            set = SetOrCurrent(set);
            if (set == null || previewMaterialChoices == null) return null;
            string key = SetKey(set);
            return previewMaterialChoices.FirstOrDefault(c => c != null && c.set == key);
        }
        PreviewMaterialChoice EnsureChoice(TextureSet set)
        {
            if (previewMaterialChoices == null) previewMaterialChoices = new List<PreviewMaterialChoice>();
            var choice = MaterialChoice(set);
            if (choice == null) previewMaterialChoices.Add(choice = new PreviewMaterialChoice { set = SetKey(set) });
            return choice;
        }

        /// <summary>選びを変えられないとき（ストロークの最中）の理由。変えられれば null。</summary>
        string MaterialChoiceRefusal() => stroke != null ? L.Tr("Finish the stroke first.") : null;

        /// <summary>セットを元のマテリアルで見せる（手で決めた流し込み先は残す）。</summary>
        internal bool UseOriginalMaterial(TextureSet set = null)
        {
            set = SetOrCurrent(set);
            string refused = MaterialChoiceRefusal(); if (refused != null) { message = refused; return false; }
            var choice = MaterialChoice(set);
            if (choice != null) { choice.source = PreviewMaterialSource.Original; choice.materialGuid = ""; choice.materialFileId = 0; choice.shaderName = ""; ForgetEmptyChoices(); }
            DropMadeMaterial(set);
            MaterialChoiceChanged(L.Tr("3D view: {0} shows its own material.", set.Name));
            return true;
        }

        /// <summary>セットをプロジェクトのマテリアルのアセットで見せる。アセットでないマテリアル（メモリの中だけのもの・シーンのもの）は断る。</summary>
        internal bool UsePreviewMaterial(Material material, TextureSet set = null)
        {
            set = SetOrCurrent(set);
            string refused = MaterialChoiceRefusal() ?? PreviewMaterialRefusal(material);
            if (refused != null) { message = refused; return false; }
            if (preview != null && preview.HasModel && material == preview.SourceMaterial(set.FirstSlot)) return UseOriginalMaterial(set);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long fileId);
            var choice = EnsureChoice(set);
            choice.source = PreviewMaterialSource.Material; choice.materialGuid = guid; choice.materialFileId = fileId; choice.shaderName = "";
            resolvedChoices[SetKey(set)] = material;
            DropMadeMaterial(set);
            MaterialChoiceChanged(L.Tr("3D view: {0} is shown with the material {1} (a preview copy; the material is not changed).", set.Name, material.name));
            return true;
        }

        /// <summary>プロジェクトのマテリアルに使えない理由（使えれば null）。</summary>
        internal static string PreviewMaterialRefusal(Object candidate)
        {
            if (candidate == null) return L.Tr("Choose a material.");
            if (!(candidate is Material material)) return L.Tr("{0} is a {1}, not a material.", candidate.name, candidate.GetType().Name);
            if (!EditorUtility.IsPersistent(material) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long _) || string.IsNullOrEmpty(guid))
                return L.Tr("{0} is not a material asset in the project (only an asset can be found again later).", material.name);
            return null;
        }

        /// <summary>セットを、シェーダーから作った既定の値のマテリアル（プレビューの中だけ）で見せる。使えないシェーダーは断る。</summary>
        internal bool UsePreviewShader(Shader shader, TextureSet set = null)
        {
            set = SetOrCurrent(set);
            string refused = MaterialChoiceRefusal() ?? PreviewShaderRefusal(shader);
            if (refused != null) { message = refused; return false; }
            var choice = EnsureChoice(set);
            choice.source = PreviewMaterialSource.Shader; choice.shaderName = shader.name; choice.materialGuid = ""; choice.materialFileId = 0;
            MadeMaterialFor(set, shader);
            MaterialChoiceChanged(L.Tr("3D view: {0} is shown with a new material of the shader {1} (made for the preview only).", set.Name, shader.name));
            return true;
        }

        /// <summary>シェーダーに使えない理由（使えれば null）: 無い・壊れている・この GPU で使えない・隠しのもの。</summary>
        internal static string PreviewShaderRefusal(Shader shader)
        {
            if (shader == null) return L.Tr("Choose a shader.");
            if (shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) return L.Tr("{0} is a hidden shader, not one for materials.", shader.name);
            return PreviewMaterialBindings.ShaderProblem(shader);
        }

        /// <summary>
        /// チャンネルの流し込み先を手で決める。property が null なら自動に戻し、空なら見せない。使えないもの（無いプロパティ・型・詰め方）は断る。
        /// </summary>
        internal bool SetChannelRoute(PaintChannel channel, string property, PreviewPacking packing = PreviewPacking.Color, TextureSet set = null)
        {
            set = SetOrCurrent(set);
            string refused = MaterialChoiceRefusal(); if (refused != null) { message = refused; return false; }
            if (property != null)
            {
                var material = preview != null && preview.HasModel ? ViewMaterialOf(set) : null;
                var route = new PreviewChannelRoute(channel, property, packing);
                string problem = material == null ? L.Tr("This material slot has no material to route into.") : PreviewMaterialBindings.RouteProblem(material.shader, route);
                if (problem != null) { message = L.Tr("{0}: {1}", L.Tr(channel.ToString()), problem); return false; }
            }
            var choice = property == null ? MaterialChoice(set) : EnsureChoice(set);
            if (choice == null) return true;
            choice.routes.RemoveAll(r => r == null || r.channel == channel);
            if (property != null) choice.routes.Add(new PreviewChannelRoute(channel, property, packing));
            ForgetEmptyChoices();
            MaterialChoiceChanged(property == null ? L.Tr("{0}: back to the automatic route.", L.Tr(channel.ToString()))
                : property.Length == 0 ? L.Tr("{0}: not shown in the material view.", L.Tr(channel.ToString()))
                : L.Tr("{0} goes into {1} ({2}).", L.Tr(channel.ToString()), property, PreviewMaterialBindings.PackingLabel(packing)));
            return true;
        }

        /// <summary>手で決めた流し込み先を全部、自動に戻す。</summary>
        internal void ClearChannelRoutes(TextureSet set = null)
        {
            set = SetOrCurrent(set);
            var choice = MaterialChoice(set);
            if (choice == null || choice.routes.Count == 0) return;
            if (MaterialChoiceRefusal() is string refused) { message = refused; return; }
            choice.routes.Clear(); ForgetEmptyChoices();
            MaterialChoiceChanged(L.Tr("All channels of {0} use the automatic routes again.", set.Name));
        }

        void ForgetEmptyChoices() => previewMaterialChoices.RemoveAll(c => c == null || c.source == PreviewMaterialSource.Original && (c.routes == null || c.routes.Count == 0));

        void MaterialChoiceChanged(string note)
        {
            SyncMaterialChoices();
            message = note; repaintPixels = true; lightingRevision = -1; Repaint(); RepaintPanelWindowsSoon();
        }

        /// <summary>セットのマテリアル表示で見せるマテリアル（選んだもの。選んでいなければ元のマテリアル。モデルが無ければ null）。</summary>
        internal Material ViewMaterialOf(TextureSet set)
        {
            if (preview == null || !preview.HasModel || set == null) return null;
            return ResolveChoice(set, MaterialChoice(set), out _) ?? preview.SourceMaterial(set.FirstSlot);
        }

        /// <summary>選びのマテリアル（元のマテリアルを使うなら null）。消えていれば null と理由。</summary>
        Material ResolveChoice(TextureSet set, PreviewMaterialChoice choice, out string gone)
        {
            gone = null;
            if (choice == null) return null;
            string key = SetKey(set);
            switch (choice.source)
            {
                case PreviewMaterialSource.Material:
                {
                    if (resolvedChoices.TryGetValue(key, out var cached) && cached != null) return cached;
                    Material found = null;
                    string path = string.IsNullOrEmpty(choice.materialGuid) ? null : AssetDatabase.GUIDToAssetPath(choice.materialGuid);
                    if (!string.IsNullOrEmpty(path))
                        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (o is Material m && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string _, out long id) && id == choice.materialFileId) { found = m; break; }
                    if (found == null) { gone = L.Tr("The preview material of {0} is gone (deleted or moved out of the project), so it shows its own material again.", set.Name); return null; }
                    resolvedChoices[key] = found;
                    return found;
                }
                case PreviewMaterialSource.Shader:
                {
                    var shader = Shader.Find(choice.shaderName ?? "");
                    if (shader == null) { gone = L.Tr("The shader {0} chosen for {1} is gone, so it shows its own material again.", choice.shaderName, set.Name); return null; }
                    return MadeMaterialFor(set, shader);
                }
                default: return null;
            }
        }

        /// <summary>シェーダーから作ったマテリアル（同じセットと同じシェーダーなら使い回す。別のシェーダーにしたら前のものを捨てる）。</summary>
        Material MadeMaterialFor(TextureSet set, Shader shader)
        {
            if (madeMaterials == null) madeMaterials = new List<MadeMaterial>();
            string key = SetKey(set);
            var made = madeMaterials.FirstOrDefault(m => m != null && m.set == key);
            if (made != null && made.material != null && made.shader == shader.name && made.material.shader == shader) return made.material;
            DropMadeMaterial(set);
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = shader.name.Substring(shader.name.LastIndexOf('/') + 1) + " (preview only)" };
            madeMaterials.Add(new MadeMaterial { set = key, shader = shader.name, material = material });
            return material;
        }
        void DropMadeMaterial(TextureSet set)
        {
            if (madeMaterials == null) return;
            string key = SetKey(set);
            foreach (var m in madeMaterials.Where(m => m != null && m.set == key).ToList())
            {
                if (m.material != null) { materialEdits?.RevertAll(m.material); DestroyImmediate(m.material); }
                madeMaterials.Remove(m);
            }
            resolvedChoices.Remove(key);
        }
        /// <summary>シェーダーから作った、プレビューだけのマテリアルか。</summary>
        internal bool IsMadeMaterial(Material m) => m != null && madeMaterials != null && madeMaterials.Any(x => x != null && x.material == m);

        /// <summary>窓を閉じたとき（OnDestroy）: シェーダーから作ったマテリアルを捨てる。</summary>
        void DisposeMadeMaterials()
        {
            if (madeMaterials == null) return;
            foreach (var m in madeMaterials) if (m != null && m.material != null) { materialEdits?.RevertAll(m.material); DestroyImmediate(m.material); }
            madeMaterials.Clear(); resolvedChoices.Clear();
        }

        /// <summary>
        /// 選びをプレビューに入れる（毎回呼んでよい。アセットは覚えたものを使い、消えたときだけ探し直す）。消えたアセット・シェーダーの選びは
        /// 元のマテリアルに戻して知らせる。使わなくなったセット（消したセット）の作ったマテリアルを捨てる。
        /// </summary>
        void SyncMaterialChoices()
        {
            if (preview == null || !preview.HasModel) return;
            SyncCurrentSet();
            var notes = new List<string>();
            foreach (var set in textureSets)
            {
                if (!set.InModel) continue;
                var choice = MaterialChoice(set);
                var material = ResolveChoice(set, choice, out string gone);
                if (gone != null)
                {
                    notes.Add(gone);
                    choice.source = PreviewMaterialSource.Original; choice.materialGuid = ""; choice.materialFileId = 0; choice.shaderName = "";
                    DropMadeMaterial(set);
                }
                foreach (int slot in set.Slots) preview.SetMaterialChoice(slot, material, choice?.routes); // マテリアルを使う全部のスロット
            }
            if (madeMaterials != null)
                foreach (var m in madeMaterials.Where(m => m != null && !textureSets.Any(s => SetKey(s) == m.set)).ToList())
                { if (m.material != null) { materialEdits?.RevertAll(m.material); DestroyImmediate(m.material); } madeMaterials.Remove(m); }
            if (notes.Count > 0) { ForgetEmptyChoices(); message = string.Join(" ", notes); repaintPixels = true; Repaint(); }
        }

        /// <summary>選びの名前（欄と見出しに出す）。</summary>
        internal string MaterialChoiceLabel(TextureSet set = null)
        {
            set = SetOrCurrent(set);
            var choice = MaterialChoice(set);
            switch (choice?.source ?? PreviewMaterialSource.Original)
            {
                case PreviewMaterialSource.Material: { var m = ResolveChoice(set, choice, out _); return L.Tr("Material: {0}", m != null ? m.name : "-"); }
                case PreviewMaterialSource.Shader: return L.Tr("Shader: {0}", choice.shaderName);
                default:
                {
                    var own = preview != null && preview.HasModel ? preview.SourceMaterial(set.FirstSlot) : null;
                    return own != null ? L.Tr("Own material ({0})", own.name) : L.Tr("Own material (none)");
                }
            }
        }

        // ───────── 欄: 見せるマテリアルの選び ─────────

        const int PreviewMaterialPickerId = 0x59500020;
        bool previewMaterialPickerPending;

        /// <summary>
        /// マテリアルの欄の 1 行目: 「マテリアル」と見せているマテリアルの箱（押すとメニュー: 元のマテリアル・プロジェクトのマテリアル…・
        /// シェーダー）と、プロジェクトの窓で示すボタン。マテリアルをドラッグして落としても選べる。マテリアルを選ぶ窓は、この行を描いた窓から
        /// 開く（その窓に結果が届く）。shown は今見せているマテリアル（無ければ null）。
        /// </summary>
        void DrawPreviewMaterialRow(UiRows rows, Material shown)
        {
            var e = Event.current;
            HandlePreviewMaterialPicker(e);
            if (previewMaterialPickerPending && e.type == EventType.Repaint)
            {
                previewMaterialPickerPending = false;
                var current = MaterialChoice()?.source == PreviewMaterialSource.Material ? ViewMaterialOf(currentSet) : null;
                EditorGUIUtility.ShowObjectPicker<Material>(current, false, "", PreviewMaterialPickerId);
            }
            var row = rows.Row();
            var box = new Rect(row.x, row.y, row.width - (shown != null ? 28 : 0), row.height);
            var source = MaterialChoice()?.source ?? PreviewMaterialSource.Original;
            string value = source == PreviewMaterialSource.Original ? (shown != null ? shown.name : L.Tr("None")) : MaterialChoiceLabel();
            PaintGui.FitDropdown(MaterialSpot("material.choice", box), L.Tr("Material"), value, at => ShowPreviewMaterialMenu(at),
                MaterialChoiceLabel() + "\n" + L.Tr("What the material view shows for this texture set: its own material, a material of the project, or a new material of a shader (all as preview copies; nothing is changed). Drop a material here to use it."),
                GUI.enabled, MaterialLabelWidth, true);
            if (shown != null && PaintGui.IconButton(MaterialSpot("material.ping", new Rect(row.xMax - 24, row.y, 24, row.height)), "target", L.Tr("Show the material in the Project window"), false, true, 16)) EditorGUIUtility.PingObject(shown);
            // プロジェクトの窓からマテリアルを落とす
            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && box.Contains(e.mousePosition) && GUI.enabled)
            {
                var dropped = DragAndDrop.objectReferences.FirstOrDefault(o => o is Material);
                DragAndDrop.visualMode = dropped != null && PreviewMaterialRefusal(dropped) == null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    var first = DragAndDrop.objectReferences.FirstOrDefault();
                    if (dropped != null) TryAction(() => UsePreviewMaterial((Material)dropped));
                    else message = PreviewMaterialRefusal(first);
                }
                e.Use();
            }
        }

        void HandlePreviewMaterialPicker(Event e)
        {
            if (e.type != EventType.ExecuteCommand || EditorGUIUtility.GetObjectPickerControlID() != PreviewMaterialPickerId) return;
            if (e.commandName != "ObjectSelectorClosed" && e.commandName != "ObjectSelectorUpdated") return;
            if (EditorGUIUtility.GetObjectPickerObject() is Material picked && picked != ViewMaterialOf(currentSet)) UsePreviewMaterial(picked);
            e.Use();
        }

        void ShowPreviewMaterialMenu(Rect at)
        {
            var m = new PaintMenu();
            var choice = MaterialChoice(); var source = choice?.source ?? PreviewMaterialSource.Original;
            Item(m, "Own Material", () => UseOriginalMaterial(), true, source == PreviewMaterialSource.Original);
            Item(m, "Project Material…", () => { previewMaterialPickerPending = true; Repaint(); RepaintPanelWindowsSoon(); }, true, source == PreviewMaterialSource.Material);
            m.AddSeparator("");
            foreach (var (name, shader) in PreviewShaderChoices())
            {
                var s = shader;
                m.AddItem(new GUIContent(L.Tr("Shader") + "/" + name), source == PreviewMaterialSource.Shader && choice.shaderName == name, () => { TryAction(() => UsePreviewShader(s)); Repaint(); });
            }
            m.DropDown(at);
        }

        /// <summary>メニューに出すシェーダー（隠し・壊れた・この GPU で使えないものと、ビルトインのパイプラインでは描けない SRP 専用のものを除く）。</summary>
        internal static List<(string name, Shader shader)> PreviewShaderChoices()
        {
            var list = new List<(string, Shader)>();
            foreach (var info in ShaderUtil.GetAllShaderInfo())
            {
                if (!info.supported || info.hasErrors || info.name.StartsWith("Hidden/", StringComparison.Ordinal) || info.name.StartsWith("Legacy Shaders/", StringComparison.Ordinal)) continue;
                var shader = Shader.Find(info.name);
                if (shader == null || PreviewMaterialBindings.ShaderProblem(shader) != null) continue;
                list.Add((info.name, shader));
            }
            return list.OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ───────── 欄: チャンネルの流し込み先 ─────────

        bool materialRoutesOpen;
        internal bool MaterialRoutesOpen { get => materialRoutesOpen; set => materialRoutesOpen = value; }

        /// <summary>「3D ビュー」の行の右の、チャンネルの流し込み先の一覧を開く・閉じるボタン（手で決めたものがあれば青い）。</summary>
        void DrawChannelRoutesToggle(Rect r, PreviewMaterialBinding binding)
        {
            int hand = MaterialChoice()?.routes?.Count ?? 0;
            string tip = L.Tr("Channels: which property of the material each painted channel goes into (the material view only)") + (hand > 0 ? "\n" + L.Tr("{0} set by hand", hand) : "");
            if (PaintGui.IconButton(MaterialSpot("material.routes", r), "tune", tip, materialRoutesOpen || hand > 0, binding != null && binding.CanShow, 16))
            { materialRoutesOpen = !materialRoutesOpen; Repaint(); }
        }

        /// <summary>チャンネルの流し込み先の一覧（開いているときだけ）。行ごとに「チャンネル → プロパティ・詰め方」、押すとメニュー（自動・
        /// 見せない・シェーダーの 2D テクスチャと詰め方）。手で決めたものは青いボタンで、全部を自動に戻すボタン。</summary>
        void DrawChannelRoutes(UiRows rows, Material shown, PreviewMaterialBinding binding)
        {
            if (!materialRoutesOpen || binding == null || !binding.CanShow) return;
            var choice = MaterialChoice();
            int hand = choice?.routes?.Count ?? 0;
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Channels") + (hand > 0 ? " · " + L.Tr("{0} set by hand", hand) : ""), L.Tr("Which property of the material each painted channel goes into (the material view only)"));
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                var route = choice?.Route(c);
                var bound = binding.For(c);
                string target = bound != null ? bound.Property + " · " + PreviewMaterialBindings.PackingLabel(bound.Packing) : L.Tr("not shown");
                var row = MaterialSpot("material.route." + c, rows.Row());
                float label = Mathf.Min(86, row.width * .35f);
                PaintGui.Text(new Rect(row.x, row.y, label, row.height), PaintGui.Fit(L.Tr(c.ToString()), label - 4, PaintTheme.Label), PaintTheme.Label);
                var button = new Rect(row.x + label, row.y, row.width - label, row.height);
                string tip = bound?.Reading ?? binding.Unmapped.FirstOrDefault(u => u.Channel == c).Reason;
                // 手で決めたものは青いボタン（自動のものは地の色）
                if (PaintGui.Button(button, PaintGui.Fit(target, button.width - 10, PaintTheme.Label, false), route != null, GUI.enabled, (route != null ? L.Tr("Set by hand.") + " " : "") + tip)) ShowRouteMenu(c, shown, binding, button);
            }
            if (hand > 0 && PaintGui.FitButton(MaterialSpot("material.routes.reset", rows.Row()), L.Tr("All Channels Automatic"), false, GUI.enabled, L.Tr("Forget the routes set by hand")))
                TryAction(() => ClearChannelRoutes());
        }

        void ShowRouteMenu(PaintChannel channel, Material shown, PreviewMaterialBinding binding, Rect at)
        {
            var m = new PaintMenu();
            var route = MaterialChoice()?.Route(channel);
            var c = channel;
            m.AddItem(new GUIContent(L.Tr("Automatic")), route == null, () => { TryAction(() => SetChannelRoute(c, null)); Repaint(); });
            m.AddItem(new GUIContent(L.Tr("Not Shown")), route != null && route.property.Length == 0, () => { TryAction(() => SetChannelRoute(c, "")); Repaint(); });
            m.AddSeparator("");
            var packings = PreviewMaterialBindings.PackingsFor(channel);
            foreach (var property in PreviewMaterialBindings.TextureProperties(shown.shader))
            {
                foreach (var packing in packings)
                {
                    var p = property; var k = packing;
                    string text = packings.Count == 1 ? property : property + "/" + PreviewMaterialBindings.PackingLabel(packing);
                    bool on = route != null && route.property == property && route.packing == packing;
                    m.AddItem(new GUIContent(text), on, () => { TryAction(() => SetChannelRoute(c, p, k)); Repaint(); });
                }
            }
            m.DropDown(at);
        }
    }
}
