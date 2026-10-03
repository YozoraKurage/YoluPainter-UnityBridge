using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Api
{
    /// <summary>
    /// コマンドを呼んだ YoluPainter のウィンドウで開いているプロジェクト（今のテクスチャセット）。読むのはいつでも、変えるのは
    /// ストロークの最中でないときだけ（最中なら InvalidOperationException）。変える操作はそれぞれ 1 回の Undo になる。
    /// 画像はどれも straight RGBA8、左下が原点、文書の大きさ（<see cref="Width"/> × <see cref="Height"/> × 4 バイト）。
    /// </summary>
    public interface IPainterSession
    {
        int Width { get; }
        int Height { get; }
        /// <summary>保存先の .ylp の絶対パス（まだ保存していなければ null）。</summary>
        string ProjectPath { get; }
        /// <summary>プロジェクトのモデル（無ければ null）。読むだけにする（YoluPainter は元のアセットを変えない約束）。</summary>
        GameObject Model { get; }
        /// <summary>今描いているチャンネル。</summary>
        PaintChannel Channel { get; }
        /// <summary>選んでいるレイヤーの ID（無ければ Guid.Empty）。</summary>
        Guid SelectedLayer { get; }
        /// <summary>レイヤー（下から上の順）。</summary>
        IReadOnlyList<PainterLayerInfo> Layers { get; }

        /// <summary>チャンネルの全レイヤーの合成（YoluPainter の画面と同じ結果）。</summary>
        byte[] ReadComposite(PaintChannel channel);
        /// <summary>レイヤー（ペイントのレイヤー）のチャンネルの画素。</summary>
        byte[] ReadLayer(Guid layerId, PaintChannel channel);

        /// <summary>選んでいるレイヤーの上に、画像を中身にした新しいペイントのレイヤーを足して選ぶ。返り値はその ID。</summary>
        Guid AddImageLayer(string name, PaintChannel channel, byte[] rgba);
        /// <summary>ペイントのレイヤーのチャンネルを画像で置き換える（選択範囲があればその中だけ）。何も変わらなければ false。
        /// 画像ピクセルのロック・すべてのロックのレイヤー（ロックしたグループの中も）は断る（LayerLockedException。何も変えない）。
        /// 透明ピクセルのロックでは色だけを置き換え、各画素の透明度は変えない（透明な画素はそのまま）。</summary>
        bool ReplaceLayerPixels(Guid layerId, PaintChannel channel, byte[] rgba);

        /// <summary>ステータスバーに出す。</summary>
        void ShowMessage(string message);
    }

    /// <summary>レイヤーの要約（読むだけ）。</summary>
    public readonly struct PainterLayerInfo
    {
        public readonly Guid Id, ParentId;
        public readonly string Name;
        public readonly LayerKind Kind;
        public readonly bool Visible, IsGroup;
        public readonly double Opacity;
        public readonly IReadOnlyList<PaintChannel> Channels;
        public PainterLayerInfo(Guid id, Guid parentId, string name, LayerKind kind, bool visible, bool isGroup, double opacity, IReadOnlyList<PaintChannel> channels)
        { Id = id; ParentId = parentId; Name = name; Kind = kind; Visible = visible; IsGroup = isGroup; Opacity = opacity; Channels = channels; }
    }
}
