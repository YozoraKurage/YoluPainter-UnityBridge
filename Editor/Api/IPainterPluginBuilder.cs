using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Api
{
    /// <summary>プラグインが <see cref="PainterPlugin.Configure"/> の中で使う登録の口。</summary>
    public interface IPainterPluginBuilder
    {
        /// <summary>
        /// メニューのコマンドを足す。menuPath の先頭が YoluPainter のメニューの名前（File・Edit・Layer・Select・Filter・3D・View・
        /// Window・Help）ならそのメニューの終わりに、それ以外は「Plugins」メニューに入る（例: "Filter/Noise/Add Noise Layer"）。
        /// run は文書を変えられる（変更は 1 回ずつ Undo できる）。enabled が false を返すあいだは灰色にする。
        /// </summary>
        void AddCommand(string menuPath, Action<IPainterSession> run, Func<IPainterSession, bool> enabled = null);

        /// <summary>ツールのアイコンを差し替える（ツールの帯の絵。selected を省くと選択中も同じ絵）。テクスチャの持ち主はプラグインのまま。</summary>
        void SetToolIcon(string toolId, Texture2D icon, Texture2D selected = null);
    }
}
