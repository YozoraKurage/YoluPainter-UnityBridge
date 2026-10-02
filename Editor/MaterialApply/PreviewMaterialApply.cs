using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor.MaterialApply
{
    /// <summary>反映する 1 つの値: プロパティの今の値と、マテリアルの欄で変えた値。</summary>
    public sealed class MaterialApplyChange
    {
        public MaterialPropertyEdit Edit { get; internal set; }
        public string Property => Edit.property;
        public Vector4 Before { get; internal set; }
        public bool KeywordBefore { get; internal set; }
        public string Describe()
        {
            var sb = new StringBuilder(Property).Append(": ").Append(MaterialPropertyEdit.Format(Before, Edit.type)).Append(" → ").Append(MaterialPropertyEdit.Format(Edit.value, Edit.type));
            if (!string.IsNullOrEmpty(Edit.keyword) && KeywordBefore != Edit.keywordEnabled) sb.Append(" (").Append(L.Tr("keyword {0} {1}", Edit.keyword, Edit.keywordEnabled ? L.Tr("on") : L.Tr("off"))).Append(")");
            return sb.ToString();
        }
    }

    /// <summary>何を元のマテリアルに入れるかの一覧。作るだけでは何も変えない。</summary>
    public sealed class MaterialApplyPlan
    {
        public Material Material { get; internal set; }
        public string MaterialPath { get; internal set; }
        public IReadOnlyList<MaterialApplyChange> Changes { get; internal set; } = Array.Empty<MaterialApplyChange>();
        /// <summary>入れられない（シェーダーが変わってプロパティが無い・型が違う）ので外した変更。</summary>
        public IReadOnlyList<string> Skipped { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> Refusals { get; internal set; } = Array.Empty<string>();
        public bool CanApply => Refusals.Count == 0 && Changes.Count > 0;

        /// <summary>確かめのダイアログの文。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            if (Refusals.Count > 0) { foreach (var r in Refusals) sb.Append("• ").Append(r).Append('\n'); return sb.ToString(); }
            sb.Append(L.Tr("Material: {0}", string.IsNullOrEmpty(MaterialPath) ? Material.name : MaterialPath)).Append("\n\n");
            sb.Append(L.Tr("These values are written to the material (Unity's Undo reverts them):")).Append('\n');
            foreach (var c in Changes) sb.Append("• ").Append(c.Describe()).Append('\n');
            if (Skipped.Count > 0) { sb.Append('\n').Append(L.Tr("Not applied:")).Append('\n'); foreach (var s in Skipped) sb.Append("• ").Append(s).Append('\n'); }
            sb.Append('\n').Append(L.Tr("Nothing else on the material, its textures or the model is changed."));
            return sb.ToString();
        }
    }

    /// <summary>
    /// マテリアルの欄で変えた値を元のマテリアルに入れる、ただ 1 つの口（「マテリアルに反映…」）。ほかの操作は元のマテリアルに触れない。
    /// 計画を一覧にして確かめてから、Unity の Undo に 1 つで記録して入れる（Edit ▸ Undo で戻る）。モデルに埋め込まれたマテリアル・
    /// ビルトインのマテリアル（.mat でないアセット）とバージョン管理で編集できないものは断る。
    /// </summary>
    public static class PreviewMaterialApply
    {
        public static MaterialApplyPlan Plan(Material material, PreviewMaterialEdits edits)
        {
            var plan = new MaterialApplyPlan { Material = material };
            var refusals = new List<string>();
            if (material == null) { refusals.Add(L.Tr("This material slot has no source material.")); plan.Refusals = refusals; return plan; }
            string path = AssetDatabase.GetAssetPath(material);
            plan.MaterialPath = path;
            if (!string.IsNullOrEmpty(path) && !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                refusals.Add(L.Tr("The material is part of {0} (an imported model or a built-in asset), which Unity re-creates or does not save. Extract the material (the model's Materials tab) and use the extracted one.", path));
            else if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsOpenForEdit(material))
                refusals.Add(L.Tr("The material {0} is not open for editing (version control).", path));
            var changes = new List<MaterialApplyChange>(); var skipped = new List<string>();
            foreach (var e in edits?.For(material) ?? Enumerable.Empty<MaterialPropertyEdit>())
            {
                int i = material.shader != null ? material.shader.FindPropertyIndex(e.property) : -1;
                if (i < 0 || material.shader.GetPropertyType(i) != e.type) { skipped.Add(L.Tr("{0}: the material's shader no longer has this property with the same type.", e.property)); continue; }
                changes.Add(new MaterialApplyChange { Edit = e, Before = MaterialPropertyEdit.Read(material, e.property, e.type), KeywordBefore = !string.IsNullOrEmpty(e.keyword) && material.IsKeywordEnabled(e.keyword) });
            }
            if (changes.Count == 0 && refusals.Count == 0) refusals.Add(L.Tr("Nothing to apply: no property of this material was changed in the Material panel."));
            plan.Changes = changes; plan.Skipped = skipped; plan.Refusals = refusals;
            return plan;
        }

        /// <summary>計画どおりに入れ（Unity の Undo に 1 つ）、入れた変更をマテリアルの欄の印から外す。入れた数を返す。</summary>
        public static int Apply(MaterialApplyPlan plan, PreviewMaterialEdits edits)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Refusals.Count > 0 ? plan.Refusals : new[] { "Nothing to apply." }));
            var material = plan.Material;
            // 確かめの間に変わっていないこと（シェーダーの差し替え・値の変更）
            foreach (var c in plan.Changes)
            {
                int i = material.shader != null ? material.shader.FindPropertyIndex(c.Property) : -1;
                if (i < 0 || material.shader.GetPropertyType(i) != c.Edit.type || MaterialPropertyEdit.Read(material, c.Property, c.Edit.type) != c.Before)
                    throw new InvalidOperationException(L.Tr("The material changed since the list was made; nothing was applied. Try again."));
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("YoluPainter: apply material changes");
            Undo.RecordObject(material, "YoluPainter: apply material changes");
            foreach (var c in plan.Changes) c.Edit.WriteTo(material);
            EditorUtility.SetDirty(material);
            Undo.CollapseUndoOperations(group);
            foreach (var c in plan.Changes) edits?.Revert(material, c.Property);
            return plan.Changes.Count;
        }
    }
}
