using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.LilToonApply;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>lilToon への割り当て: 今のテクスチャセットのマテリアルスロットの元のマテリアルが、確かめた lilToon のバージョン・バリアントなら、
    /// チャンネルを lilToon 用の PNG に書き出してマテリアルに入れる。変えることをすべて一覧にして確かめてから行う。テクスチャセットが複数なら
    /// 書き出す名前とフォルダにセットの名前を足す（セットごとに別のファイル）。全部のセットを一度に割り当てる口はまだ無い。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>書き出し先の既定: 保存した .ylp が Assets の中ならその隣の &lt;名前&gt;_lilToon、そうでなければ尋ねる。</summary>
        string LilToonFolder(string stem)
        {
            if(projectPath!=null){string asset=AssetPathOf(Path.GetFullPath(projectPath));if(asset!=null){string dir=Path.GetDirectoryName(asset).Replace('\\','/');return dir+"/"+stem+"_lilToon";}}
            string picked=Dialogs.OpenFolder("Folder for the lilToon textures (inside Assets)",Application.dataPath);
            return String.IsNullOrEmpty(picked)?null:AssetPathOf(Path.GetFullPath(picked));
        }

        internal void AssignToLilToon()
        {
            if(stroke!=null){message="Finish the stroke first.";return;}
            var material=preview.SourceMaterial(materialSlot);
            string stem=(projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):"Texture")+SetFileSuffix(currentSet);
            string folder=material==null?null:LilToonFolder(stem);
            if(material!=null&&folder==null){message="Choose a folder inside Assets for the lilToon textures.";return;}
            var plan=LilToonAssignment.Plan(document,material,folder,stem);
            if(!plan.CanApply){message="lilToon: "+String.Join(" ",plan.Refusals);Dialogs.Inform("Cannot assign to lilToon",plan.Describe());return;}
            if(!ConfirmInactiveGenerators(new[]{(currentSet.Name,document)}))return; // マップの無い Generator を効きなしで書き出さない（確かめる）
            if(!Dialogs.Confirm("Assign to lilToon material?",plan.Describe(),"Assign","Cancel")){message="lilToon assignment cancelled; nothing was changed.";return;}
            var after=LilToonAssignment.Apply(plan,document);
            var ineffective=after.Channels.Where(c=>plan.Items.Any(i=>i.Channel==c.Channel)&&!c.IsEffective).Select(c=>c.Channel.ToString()).ToList();
            message="Assigned "+plan.Items.Count+" map(s) to "+Path.GetFileName(plan.MaterialPath)+" (lilToon "+after.Version+")."+(ineffective.Count>0?" Not effective yet: "+String.Join(", ",ineffective)+" (see the notes).":"")+" Unity's Undo reverts the material.";
        }
    }
}
