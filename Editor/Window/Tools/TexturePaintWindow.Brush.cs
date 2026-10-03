using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ブラシの設定（BrushState）、プリセットの適用・保存・読み込み、ブラシの取り込みと削除。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>ウィンドウのブラシ設定。brush.json とブラシプリセットのファイルにそのまま JSON で書く。
        /// schema 2 で筆先・ゆらぎ・紙の質感を、schema 3 でダイナミクス（TexturePaintWindow.BrushDynamics.cs）を足した
        /// （古い schema のファイルも読める。足した項目は既定値になる）。</summary>
        [Serializable] internal sealed partial class BrushState
        {
            public int schema = 3;
            public string presetId = "", presetName = "Custom";
            public float radius = 16, hardness = .8f, spacing = .15f, opacity = 1, flow = 1;
            public Color color = new Color(.2f,.6f,1,1);
            public bool pressureSize = true, pressureOpacity = true, pressureFlow, erase;
            public AnimationCurve pressureCurve = AnimationCurve.Linear(0,0,1,1);
            // schema 2
            public string tipId = "", textureId = "";
            public float angle, roundness = 1, sizeJitter, angleJitter, roundnessJitter, opacityJitter, flowJitter, scatter, textureDepth, textureScale = 1;
            public int count = 1;
            public bool followDirection, randomSeedPerStroke = true;
            // 手ぶれ補正と入り抜き（キャンバスの画素、0 で無し）。無い版のファイルは 0 として読む
            public float stabilizer, taperIn, taperOut;
            public int blurRadius = 3;
            public float smudgeStrength = .5f;
            public bool cloneAligned = true;
        }
        static readonly System.Random seeds = new System.Random();
        static BrushState ReadBrushState(string json)
        {
            var b=JsonUtility.FromJson<BrushState>(json);
            if(b==null||b.schema<1||b.schema>3||b.pressureCurve==null)throw new InvalidDataException("Unsupported brush settings");
            if(b.schema<2){b.roundness=1;b.textureScale=1;b.count=1;b.randomSeedPerStroke=true;b.schema=2;}
            UpgradeBrushState(b);
            ValidateMaterial(b);
            if (b.blurRadius < 1 || b.blurRadius > 64 || float.IsNaN(b.smudgeStrength) || b.smudgeStrength < 0 || b.smudgeStrength > 1) throw new InvalidDataException("Unsupported pixel effect brush settings");
            return b;
        }
        internal BrushSettings GetBrush() { var s = NewBrushSettings(); BrushTips.Apply(s, brush.tipId);
            ApplyBrushDynamics(s);
            return s; }
        BrushSettings NewBrushSettings() => new BrushSettings { Radius=brush.radius, Hardness=brush.hardness, Spacing=brush.spacing, Opacity=brush.opacity, Flow=brush.flow,
            Color=new Rgba32((byte)Mathf.RoundToInt(brush.color.r*255),(byte)Mathf.RoundToInt(brush.color.g*255),(byte)Mathf.RoundToInt(brush.color.b*255),(byte)Mathf.RoundToInt(brush.color.a*255)),
            PressureSize=brush.pressureSize,PressureOpacity=brush.pressureOpacity,PressureFlow=brush.pressureFlow,Erase=CurrentBrushEffect == BrushEffect.Paint && brush.erase, Effect=CurrentBrushEffect, BlurRadius=brush.blurRadius, SmudgeStrength=brush.smudgeStrength, CloneOffsetX=cloneStrokeOffset.x, CloneOffsetY=cloneStrokeOffset.y,
            Texture=BrushTips.Resolve(brush.textureId), Angle=brush.angle, Roundness=brush.roundness, FollowDirection=brush.followDirection,
            SizeJitter=brush.sizeJitter, AngleJitter=brush.angleJitter, RoundnessJitter=brush.roundnessJitter, OpacityJitter=brush.opacityJitter, FlowJitter=brush.flowJitter,
            Scatter=brush.scatter, Count=brush.count, TextureDepth=brush.textureDepth, TextureScale=brush.textureScale,
            Seed=brush.randomSeedPerStroke ? seeds.Next() : 0, Stabilizer=brush.stabilizer, TaperIn=brush.taperIn, TaperOut=brush.taperOut,
            CurveInterpolation=true }; // 入力の点の間を曲線で結ぶ（速く描いて点がまばらでも線が角張らない）。設定に出さず、いつも使う
        /// <summary>プリセットの設定を今のブラシに写す。色は今のまま残す（チャンネルの値として選んだものだから）。</summary>
        internal void ApplyPreset(Core.BrushPreset preset)
        {
            var s=preset.CreateSettings(); var color=brush.color; var curve=brush.pressureCurve; var assist=(brush.stabilizer,brush.taperIn,brush.taperOut);
            var secondary=brush.secondaryColor; var previous=brush;
            brush=new BrushState{ presetId=preset.Id, presetName=preset.Name, radius=(float)s.Radius, hardness=(float)s.Hardness, spacing=(float)s.Spacing, opacity=(float)s.Opacity, flow=(float)s.Flow,
                color=color, pressureCurve=curve, pressureSize=s.PressureSize, pressureOpacity=s.PressureOpacity, pressureFlow=s.PressureFlow, erase=s.Erase,
                tipId=BrushTips.IdOf(s), textureId=BrushTips.IdOf(s.Texture), angle=(float)s.Angle, roundness=(float)s.Roundness, followDirection=s.FollowDirection,
                sizeJitter=(float)s.SizeJitter, angleJitter=(float)s.AngleJitter, roundnessJitter=(float)s.RoundnessJitter, opacityJitter=(float)s.OpacityJitter, flowJitter=(float)s.FlowJitter,
                scatter=(float)s.Scatter, count=s.Count, textureDepth=(float)s.TextureDepth, textureScale=(float)s.TextureScale,
                stabilizer=assist.Item1, taperIn=assist.Item2, taperOut=assist.Item3 }; // 補正と入り抜きは描き手の設定として残す
            CopyPresetDynamics(s,secondary);
            brush.blurRadius=previous.blurRadius; brush.smudgeStrength=previous.smudgeStrength; brush.cloneAligned=previous.cloneAligned;
            CopyMaterial(previous,brush); // マテリアル（塗るチャンネルと値）は描画色と同じく描き手のものとして残す
        }
        void SavePreset(){string p=Dialogs.SaveFile("Save brush",Application.dataPath,"brush","json");if(!String.IsNullOrEmpty(p))TryAction(()=>File.WriteAllText(p,JsonUtility.ToJson(brush,true)));}
        /// <summary>ブラシのファイルを取り込み、プロジェクトのライブラリに入れて 1 つ目を選ぶ。対応していない設定は
        /// 取り込み後に一覧で知らせる（黙って捨てない）。</summary>
        internal void ImportBrushes()
        {
            string path=Dialogs.OpenFile("Import brushes",PainterSettings.BrushImportFolder,BrushImport.Extensions);if(String.IsNullOrEmpty(path))return;
            TryAction(()=>PainterSettings.UpdatePersonal(p=>p.brushImportFolder=Path.GetDirectoryName(path)));
            TryAction(()=>
            {
                IReadOnlyList<Core.Brushes.ImportedBrush> brushes;
                try{brushes=BrushImport.ReadFile(path);}
                catch(Core.Brushes.BrushImportException ex){message="Brush import failed: "+ex.Message;Dialogs.Inform("Brush import failed",Path.GetFileName(path)+"\n\n"+ex.Message);return;}
                // 共有の置き場があるときは、どちらに入れるかを尋ねる（共有に入れたものはバージョン管理で全員に渡る）。
                var library=BrushLibrary.Project.Enabled&&Dialogs.Confirm("Import brushes","Store the imported brushes in the project's shared brush folder (shared through version control) or only for you?\n\nShared: "+BrushLibrary.Project.Folder+"\nOnly you: "+BrushLibrary.Personal.Folder,"Shared with the project","Only for me")?BrushLibrary.Project:BrushLibrary.Personal;
                var added=library.Add(brushes,BrushImport.PrettyName(Path.GetFileName(path)));
                if(added.Count>0)ApplyPreset(added[0]);
                var notes=brushes.SelectMany(b=>b.Warnings.Select(w=>(b.Name,w))).GroupBy(x=>x.w).Select(g=>g.Key+(g.Count()>1?" ("+g.Count()+" brushes)":" ("+g.First().Name+")")).ToList();
                message="Imported "+added.Count+" brush"+(added.Count==1?"":"es")+" from "+Path.GetFileName(path)+(notes.Count>0?"; "+notes.Count+" unsupported setting(s) left out.":".");
                if(notes.Count>0)Dialogs.Inform("Brush import notes",String.Join("\n",notes.Take(30))+(notes.Count>30?"\n… and "+(notes.Count-30)+" more.":"")+"\n\nThe brushes were imported without these settings.");
            });
        }
        internal void DeleteImportedBrush()
        {
            if(!BrushLibrary.IsLibraryPreset(brush.presetId))return;
            string where=BrushLibrary.Owning(brush.presetId)==BrushLibrary.Project?"the project's shared brush folder (this affects everyone after you commit)":"your brush folder";
            if(!Dialogs.Confirm("Delete imported brush","Delete \""+brush.presetName+"\" from "+where+"? The original file is not touched.","Delete","Cancel"))return;
            TryAction(()=>{BrushLibrary.Owning(brush.presetId).Remove(brush.presetId);ApplyPreset(BuiltInBrushes.Presets[0]);message="Deleted the imported brush.";});
        }
        void LoadPreset(){string p=Dialogs.OpenFile("Load brush",Application.dataPath,"json");if(!String.IsNullOrEmpty(p))TryAction(()=>{if(new FileInfo(p).Length>65536)throw new InvalidDataException("Preset too large");var b=ReadBrushState(File.ReadAllText(p));var previous=brush;brush=b;try{GetBrush().Validate();}catch{brush=previous;throw;}message=MissingTipNote()??"Brush preset loaded.";});}
        /// <summary>保存されたブラシの筆先・紙の質感がこの Unity プロジェクトに無いときの知らせ（取り込んだブラシはプロジェクトの
        /// UserSettings にあるので、別のプロジェクトや別の人の環境では見つからない）。見つからない筆先は丸い筆先で描き、ID は残す。</summary>
        string MissingTipNote()
        {
            var missing=new List<string>();
            if(!String.IsNullOrEmpty(brush.tipId)&&BrushTips.ResolveRef(brush.tipId)==null)missing.Add("tip "+brush.tipId);
            if(!String.IsNullOrEmpty(brush.textureId)&&BrushTips.ResolveRef(brush.textureId)==null)missing.Add("texture "+brush.textureId);
            return missing.Count==0?null:"Brush "+String.Join(" and ",missing)+" is not available in this Unity project; painting uses a round tip / no texture instead.";
        }
    }
}
