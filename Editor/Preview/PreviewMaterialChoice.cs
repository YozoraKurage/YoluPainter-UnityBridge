using System;
using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>マテリアル表示でスロットを何で見せるか（Substance の Shader settings に当たる）。</summary>
    public enum PreviewMaterialSource
    {
        /// <summary>モデルのそのスロットの元のマテリアル（前の版と同じ）。</summary>
        Original,
        /// <summary>Unity のプロジェクトのマテリアルのアセット（lilToon・Standard など）。</summary>
        Material,
        /// <summary>シェーダー（既定の値の新しいマテリアルをプレビューの中だけに作る）。</summary>
        Shader,
    }

    /// <summary>手で決めた、1 つのチャンネルの流し込み先（プロパティが空ならそのチャンネルを見せない）。</summary>
    [Serializable]
    public sealed class PreviewChannelRoute
    {
        public PaintChannel channel;
        public string property = "";
        public PreviewPacking packing;

        public PreviewChannelRoute() { }
        public PreviewChannelRoute(PaintChannel channel, string property, PreviewPacking packing) { this.channel = channel; this.property = property ?? ""; this.packing = packing; }
        public PreviewChannelRoute Clone() => new PreviewChannelRoute(channel, property, packing);
        internal string Key => (int)channel + ":" + property + ":" + (int)packing;
    }

    /// <summary>
    /// テクスチャセット（スロット）ごとの、マテリアル表示で見せるもの（窓の状態に覚える。.ylp には入らない）。マテリアルはアセットの GUID と
    /// ファイルの中の番号で、シェーダーは名前で覚える（消えていたら窓が元のマテリアルに戻して知らせる）。<see cref="routes"/> は欄で手で決めた
    /// 流し込み先（無いチャンネルは自動）。どれもプレビューの複製だけに効き、元のマテリアル・シェーダー・アセットは変えない。
    /// </summary>
    [Serializable]
    public sealed class PreviewMaterialChoice
    {
        /// <summary>テクスチャセットの ID（Guid の "N"）。</summary>
        public string set = "";
        public PreviewMaterialSource source;
        public string materialGuid = "";
        public long materialFileId;
        public string shaderName = "";
        public List<PreviewChannelRoute> routes = new List<PreviewChannelRoute>();

        public PreviewChannelRoute Route(PaintChannel channel) => routes?.FirstOrDefault(r => r != null && r.channel == channel);
        /// <summary>手で決めた流し込み先を比べる鍵（変わったときだけプレビューが対応を決め直す）。</summary>
        internal string RoutesKey => routes == null ? "" : string.Join("|", routes.Where(r => r != null).Select(r => r.Key));
    }
}
