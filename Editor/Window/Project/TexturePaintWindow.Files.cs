using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ファイル: .ylp の保存と開く、復旧 checkpoint、PSD の取り込みと書き出し、画像の書き出し。.ylp と復旧 checkpoint は
    /// 全部のテクスチャセットを同じ並び（.ylp の形式 3: project.json と sets/&lt;ID&gt;/）で書き、同じ <see cref="YlpFormat.Open"/> で読む。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>view.json: モデル（GUID）と選んだチャンネル。形式 2 までの materialSlot は project.json に移った（読まない・書かない）。</summary>
        [Serializable] sealed class ViewState { public string modelAssetGuid; public int selectedChannel; }
        /// <summary>開いた .ylp の中身の形式（新しく作った・取り込んだものは今の形式）と、最初に作ったアプリ（形式 1 のファイルは分からないので null）。</summary>
        int openedFormat=YlpFormat.Current; YlpWriterInfo projectCreatedBy;
        internal int OpenedFormat=>openedFormat;
        /// <summary>新しく作った・取り込んだ文書: 今の形式で、作ったのはこのアプリ。</summary>
        void NewProjectRecord(){openedFormat=YlpFormat.Current;projectCreatedBy=YlpContent.Writer;}
        bool ConfirmDiscard() => ProjectUnchanged() || Dialogs.Confirm("Keep current work?","Current work has unsaved changes. A native recovery checkpoint will be kept before opening another document.","Continue","Cancel") && SaveRecoveryAndWait();
        /// <summary>復旧 checkpoint が全部のセットとセットの並びの今の中身と同じか。</summary>
        bool RecoveryIsCurrent()
        {
            if(currentSet==null)return true;
            SyncCurrentSet();
            return setsRevision==recoveredSetsRevision&&textureSets.All(s=>s.Document.Revision==s.RecoveredRevision)&&recoveredProjectState==RecoveryState();
        }
        /// <summary>保存した選択範囲をセットの文書に戻す（履歴も版も増やさない）。読めなければ選択なしで開き、そのことを知らせる（文書は開ける）。</summary>
        void RestoreSavedSelection(TextureSet set,IReadOnlyDictionary<string,byte[]> files,List<string> notes)
        {
            if(!files.TryGetValue(SelectionBinary.EntryName,out var bytes))return;
            try{set.Document.RestoreSelection(SelectionBinary.Read(bytes,set.Document));}
            catch(InvalidDataException ex){notes.Add(SetNotePrefix(set)+"The saved selection was not restored ("+ex.Message+"); nothing is selected, and saving will leave it out.");}
        }
        /// <summary>知らせの頭に付けるセットの名前（セットが 1 つなら付けない）。</summary>
        string SetNotePrefix(TextureSet set)=>textureSets.Count>1?set.Name+": ":"";
        /// <summary>開いたプロジェクトのテクスチャセットを読む（全部の正本を読めたときだけ返す。1 つでも読めなければ例外で、開いている
        /// プロジェクトはそのまま）。</summary>
        static List<TextureSet> ReadTextureSets(YlpOpened opened)
        {
            var sets=new List<TextureSet>();
            foreach(var info in opened.Project.Sets)
            {
                var files=opened.SetFiles(info.Id);
                PaintDocument document;
                try{document=DocumentBinary.Read(files[YlpArchive.NativeName]);}
                catch(InvalidDataException ex){throw new InvalidDataException("Texture set \""+info.Name+"\": "+ex.Message,ex);}
                sets.Add(new TextureSet(info.Id,info.Name,info.MaterialSlot,document)
                {
                    SelectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty,
                    ImportedOriginal=files.TryGetValue(YlpContent.ImportedOriginalName,out var original)?original:null,
                });
            }
            return sets;
        }
        /// <summary>.ylp に保存する。上書きは開いた/保存した時点から外で変わっていないときだけで、直前の版は
        /// &lt;名前&gt;.ylp-backups~ に退避する（保持数は設定）。Assets の中のファイルなら保存後に取り込み直して、Project ウィンドウの情報とサムネイルを更新する。</summary>
        internal void SaveProject(bool saveAs)
        {
            if(stroke!=null)return;
            string target=projectPath;
            if(saveAs||String.IsNullOrEmpty(target))
            {
                var (suggestFolder,suggestName)=SaveSuggestion();
                target=Dialogs.SaveFile("Save YoluPainter file",suggestFolder,suggestName,"ylp");
                if(String.IsNullOrEmpty(target))return;
                if(!target.EndsWith(YlpArchive.Extension,StringComparison.OrdinalIgnoreCase))target+=YlpArchive.Extension;
                target=Path.GetFullPath(target);
            }
            bool sameFile=projectPath!=null&&String.Equals(target,projectPath,PainterSettings.PathComparison);
            int keep=PainterSettings.BackupsToKeep;
            // 別のファイルを上書きするとき: 退避するなら元の版は残るので尋ねない。退避しない設定なら元の版が消えるので確かめる。
            if(!sameFile&&File.Exists(target)&&keep==0&&!Dialogs.Confirm("Replace file?",Path.GetFileName(target)+" already exists and backups are turned off in Project Settings > YoluPainter. Replace it? The old file will be gone.","Replace","Cancel"))return;
            // 古い形式で開いたファイルを、退避なしで今の形式に書き換えるときは確かめる（古い YoluPainter では開けなくなる）
            if(sameFile&&openedFormat<YlpFormat.Current&&keep==0&&!Dialogs.Confirm("Upgrade the file format?",Path.GetFileName(target)+" uses .ylp format "+openedFormat+". Saving writes format "+YlpFormat.Current+", which older YoluPainter versions cannot open, and backups are turned off in Project Settings > YoluPainter, so the old file will be gone.","Save","Cancel"))return;
            TryAction(()=>
            {
                Dialogs.Progress("YoluPainter","Freezing native source and writing a verified .ylp. Input is paused.",.1f);
                SyncCurrentSet();
                var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
                foreach(var set in textureSets)
                {
                    foreach(var entry in YlpContent.Composites(set.Document,thumbnail:false))files.Add(YlpFormat.SetEntry(set.Id,entry.Key),entry.Value);
                    files.Add(YlpFormat.SetEntry(set.Id,YlpArchive.NativeName),DocumentBinary.Write(set.Document));
                    if(set.ImportedOriginal!=null)files.Add(YlpFormat.SetEntry(set.Id,YlpContent.ImportedOriginalName),set.ImportedOriginal);
                    if(set.Document.Selection!=null)files.Add(YlpFormat.SetEntry(set.Id,SelectionBinary.EntryName),SelectionBinary.Write(set.Document.Selection));
                }
                files.Add(YlpFormat.ProjectName,YlpFormat.WriteProject(ProjectInfo()));
                var state=new ViewState{modelAssetGuid=model==null?"":AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)),selectedChannel=(int)channel};
                files.Add(YlpContent.ViewName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(state,true)));
                files.Add(YlpContent.BrushName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(brush,true)));
                var thumbnail=ProjectThumbnail(); if(thumbnail!=null)files.Add(YlpContent.ThumbnailName,thumbnail);
                AddMeshMapFiles(files);
                ResourceIndex.AddTo(files,resources); // プロジェクトのリソース（形式 4）
                YlpFormat.Stamp(files,YlpContent.Writer,projectCreatedBy);
                var saved=YlpStore.Save(target,files,sameFile?projectToken:null,!sameFile,keep);
                projectPath=saved.Path;projectToken=saved.Token;externalConflict=false;MeshMapsWereSaved();openedFormat=YlpFormat.Current;
                foreach(var set in textureSets)set.SavedRevision=set.Document.Revision;
                savedSetsRevision=setsRevision;
                message="Saved "+Path.GetFileName(saved.Path)+(saved.Backup!=null?"; the previous version is kept in "+Path.GetFileName(Path.GetDirectoryName(saved.Backup))+".":".")+InactiveGeneratorSaveNote();
                string asset=AssetPathOf(saved.Path);
                if(asset!=null)AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
            });
            Dialogs.ClearProgress();
        }
        /// <summary>.ylp の thumbnail.png: 今のセットの Color（無ければ最初のチャンネル）、何も描くチャンネルが無ければ並びの最初の描いたセット。</summary>
        byte[] ProjectThumbnail()
        {
            foreach(var set in new[]{currentSet}.Concat(textureSets.Where(s=>s!=currentSet)))
            {
                var thumbnail=YlpContent.Thumbnail(set.Document);
                if(thumbnail!=null)return thumbnail;
            }
            return null;
        }
        /// <summary>開いた .ylp の形式についての知らせ（古い形式から移した・知らないエントリがある）。</summary>
        static IEnumerable<string> FormatNotes(YlpOpened opened)
        {
            if(opened.Upgraded)yield return "Saved with an older .ylp format ("+opened.Info.Format+"); saving writes format "+YlpFormat.Current+", which older YoluPainter versions cannot open.";
            foreach(var note in opened.Notes)yield return note;
            if(opened.UnknownEntries.Count>0)yield return "This file holds data this YoluPainter does not know ("+String.Join(", ",opened.UnknownEntries.Take(5))+(opened.UnknownEntries.Count>5?", …":"")+"), probably from a newer version; it is not kept when you save.";
        }
        /// <summary>Unity プロジェクトの Assets の中なら "Assets/..." の形、外なら null。</summary>
        static string AssetPathOf(string fullPath)
        {
            string assets=Path.GetFullPath(Application.dataPath).TrimEnd('/','\\')+Path.DirectorySeparatorChar;
            return fullPath.StartsWith(assets,PainterSettings.PathComparison)?"Assets/"+fullPath.Substring(assets.Length).Replace('\\','/'):null;
        }
        internal void OpenProject()
        {
            string path=Dialogs.OpenFile("Open YoluPainter file",projectPath!=null?Path.GetDirectoryName(projectPath):Application.dataPath,"ylp");
            OpenProjectAt(path);
        }
        /// <summary>.ylp を YoluPainter のウィンドウで開く（ダブルクリックなど）。未保存の作業があれば確認する。</summary>
        internal static TexturePaintWindow OpenFileInWindow(string path)
        {
            var w=GetWindow<TexturePaintWindow>("Texture Painter"); w.Show(); w.Focus(); w.OpenProjectAt(path); return w;
        }
        internal void OpenProjectAt(string path)
        {
            if(String.IsNullOrEmpty(path))return;
            path=Path.GetFullPath(path);
            if(projectPath!=null&&String.Equals(path,projectPath,PainterSettings.PathComparison)&&IsSaved&&!externalConflict){message="Already open: "+Path.GetFileName(path);return;}
            if(!ConfirmDiscard())return;
            TryAction(()=>
            {
                var snapshot=YlpStore.Load(path);var opened=YlpFormat.Open(snapshot.Files);var files=opened.Files;
                var sets=ReadTextureSets(opened); // 全部の正本を読めてから入れ替える
                var loadedResources=ResourceIndex.Load(files,opened.Resources); // リソースも全部読めてから（壊れていれば何も変えずに断る）
                FinishStroke(false);CancelToolDrag();
                ReplaceProject(sets,sets.First(s=>s.Id==opened.Project.CurrentSet));BindDocument();
                projectPath=snapshot.Path;projectToken=snapshot.Token;externalConflict=false;
                openedFormat=opened.Info.Format; projectCreatedBy=opened.Info.CreatedBy;
                var notes=new List<string>();
                var budgetNote=ApplyBudgets(); if(budgetNote!=null)notes.Add(budgetNote);
                if(files.TryGetValue(YlpContent.BrushName,out var preset))
                {
                    // 状態のエントリ: 読めなければ今のブラシのまま開いて知らせる（正本は読めているので開くのを止めない）
                    try{brush=ReadBrushState(System.Text.Encoding.UTF8.GetString(preset));var missing=MissingTipNote();if(missing!=null)notes.Add(missing);}
                    catch(Exception ex) when(ex is InvalidDataException||ex is ArgumentException){notes.Add("The saved brush settings could not be read ("+ex.Message+"); the brush keeps its current settings.");}
                }
                if(files.TryGetValue(YlpContent.ViewName,out var view))
                {
                    var state=JsonUtility.FromJson<ViewState>(System.Text.Encoding.UTF8.GetString(view));channel=(PaintChannel)state.selectedChannel;
                    var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(state.modelAssetGuid));
                    if(loaded!=null){model=loaded;preview.Load(model);if(sets.Count==1)materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1));}
                    else if(!String.IsNullOrEmpty(state.modelAssetGuid))notes.Add("Model asset is unavailable; assign it explicitly.");
                }
                SyncCurrentSet();
                // 形式 2 までのファイルの 1 つのセットは、移行で仮の名前になっている: モデルのマテリアルの名前（無ければ訳した既定の名前）にする
                if(opened.Info.Format<3)foreach(var set in sets)set.Name=DefaultSetName(set.MaterialSlot,set);
                meshMapsLoadedInto.Clear();
                foreach(var set in sets)
                {
                    var setFiles=opened.SetFiles(set.Id);
                    LoadMeshMapFiles(set,setFiles,notes);
                    RestoreSavedSelection(set,setFiles,notes);
                    set.SavedRevision=set.Document.Revision;
                }
                var resourceNote=AdoptResources(loadedResources); if(resourceNote!=null)notes.Add(resourceNote);
                var missingImages=MissingFillImageNote(); if(missingImages!=null)notes.Add(missingImages); // 無い画像を読む層は値のまま（参照は残す）
                ResetSetsBaseline(true);
                var sourceNote=AskAboutChangedResources(); if(sourceNote!=null)notes.Add(sourceNote); // 出どころが変わっていれば尋ねる（更新すると未保存になる）
                notes.AddRange(FormatNotes(opened));
                message="Opened "+Path.GetFileName(path)+" (verified)"+(sets.Count>1?" with "+sets.Count+" texture sets":"")+(notes.Count>0?". "+String.Join(" ",notes):"");
            });
        }
        internal void ImportPsd()
        {
            string path=Dialogs.OpenFile("Inspect/import RGB8 PSD",Application.dataPath,"psd");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                if(new FileInfo(path).Length>128L*1024*1024)throw new InvalidOperationException("PSD exceeds 128 MiB prototype input budget.");
                var result=PsdCodec.Read(File.ReadAllBytes(path));
                if(result.Mode!=PsdCompatibilityMode.EditableRaster){Dialogs.Inform("PSD protected: "+result.Mode,String.Join("\n",result.Diagnostics.Select(d=>d.ToString()))+"\nOriginal file was not modified. Unsupported features cannot be edited here.");return;}
                var next=PsdBridge.Import(result);if(!ConfirmDiscard())return;
                if(next.Layers.Count==0)next.AddLayer(L.Tr("Layer")+" 1");
                // PSD は 1 つのテクスチャセットのプロジェクトになる（スロットは今のセットのまま）
                int slot=currentSet!=null?materialSlot:0;
                var set=new TextureSet(next.Id,BaseSetName(slot),slot,next){SelectedLayer=next.Layers.Last().Id,ImportedOriginal=result.CopyOriginalBytes()};
                FinishStroke(false);CancelToolDrag();
                ReplaceProject(new[]{set},set);BindDocument();
                ForgetProjectFile();importedPsdPath=Path.GetFullPath(path);channel=PaintChannel.Color;
                message="Imported "+Path.GetFileName(path)+". Save keeps it as a .ylp (with the original PSD inside); Export PSD writes a PSD. The PSD itself is never rewritten.";
                // 編集できる取り込みでも、書き出す PSD に含まれない情報や合成結果の差などの注意があれば一覧で見せる（黙って捨てない）
                var notes=result.Diagnostics.Select(d=>d.ToString()).ToList();
                if(notes.Count>0)Dialogs.Inform("PSD imported with notes",String.Join("\n",notes.Take(40))+(notes.Count>40?"\n… and "+(notes.Count-40)+" more.":"")+"\n\nThe original PSD bytes are kept inside the .ylp when you save.");
            });
        }
        /// <summary>書き出すファイルの名前の元（開いているファイル、取り込んだ PSD、無ければ Texture）。</summary>
        string ExportStem()=>projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):importedPsdPath!=null?Path.GetFileNameWithoutExtension(importedPsdPath):"Texture";
        /// <summary>今のセットのファイルの名前の部分（セットが複数なら _&lt;セット名&gt;、1 つなら空）。</summary>
        string SetFileSuffix(TextureSet set)=>textureSets.Count>1?"_"+Sanitize(set.Name):"";
        /// <summary>選んだチャンネルを PSD に書き出す（今のテクスチャセット）。PSD で表せないもの（通常以外の合成・マスク・Fill・調整・クリッピングなど）が
        /// あれば、平らにせずに理由を示して書かない。</summary>
        internal void ExportPsd()
        {
            byte[] bytes;
            var psdNotes=new List<PsdDiagnostic>(); // 画像の塗りつぶしは画素の層として書き、投影が残らないことを知らせる
            try{bytes=PsdCodec.Write(PsdBridge.Export(document,channel,psdNotes));}
            catch(Exception ex){message="PSD export unavailable: "+ex.Message;Dialogs.Inform("PSD export unavailable",ex.Message+"\n\nNothing was written. The .ylp keeps everything losslessly.");return;}
            string source=projectPath??importedPsdPath;
            string stem=(source!=null?Path.GetFileNameWithoutExtension(source):"Texture")+SetFileSuffix(currentSet);
            string path=Dialogs.SaveFile("Export selected channel PSD",source!=null?Path.GetDirectoryName(source):Application.dataPath,channel==PaintChannel.Color?stem:stem+"_"+channel,"psd");if(String.IsNullOrEmpty(path))return;
            // 取り込み元の PSD を上書きするときは確かめる（原本のバイト列は .ylp に残るが、外の PSD そのものは置き換わる）
            if(importedPsdPath!=null&&String.Equals(Path.GetFullPath(path),importedPsdPath,PainterSettings.PathComparison)&&!Dialogs.Confirm("Overwrite the imported PSD?",Path.GetFileName(path)+" is the PSD this document was imported from. Replace it with the exported PSD?"+(importedOriginal!=null?" Its original bytes stay inside the .ylp once you save.":""),"Replace","Cancel"))return;
            TryAction(()=>{File.WriteAllBytes(path,bytes);message="Exported "+channel+" PSD. No material was changed."+NormalExportNote(channel==PaintChannel.Normal,psd:true)+(psdNotes.Count>0?" "+String.Join(" ",psdNotes.Select(n=>n.Message)):"");
                if(psdNotes.Count>0)Dialogs.Inform(L.Tr("PSD exported with notes"),String.Join("\n",psdNotes.Select(n=>"• "+n.Message))+"\n\n"+L.Tr("The .ylp keeps the images and projections."));});
        }
        /// <summary>Export Images のファイルの名前: テクスチャセットが 1 つなら &lt;名前&gt;_&lt;チャンネル&gt;.png、複数なら &lt;名前&gt;_&lt;セット名&gt;_&lt;チャンネル&gt;.png。</summary>
        internal string ExportImageName(string stem,TextureSet set,PaintChannel c)=>stem+SetFileSuffix(set)+"_"+c+".png";
        /// <summary>全部のテクスチャセットの使っている全チャンネルを PNG としてフォルダに書き出す（名前は <see cref="ExportImageName"/>）。既存のファイルを
        /// 置き換えるときは確かめる。名前が重なる（セットの名前がファイル名で同じになる）ときは書かない。Assets の中なら取り込み直し、新しく作った
        /// テクスチャにだけ色空間（Color/Emission は sRGB、他はリニア）を設定する。既にあるテクスチャの取り込み設定は変えず、合っていなければ
        /// 知らせる。マテリアルには割り当てない。</summary>
        internal void ExportImages()
        {
            SyncCurrentSet();
            string stem=projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):"Texture";
            var planned=textureSets.SelectMany(set=>YlpContent.UsedChannels(set.Document).Select(c=>(set,channel:c,name:ExportImageName(stem,set,c)))).ToList();
            if(planned.Count==0){message="Nothing to export: no layer uses any channel.";return;}
            var clash=planned.GroupBy(t=>t.name,StringComparer.OrdinalIgnoreCase).FirstOrDefault(g=>g.Count()>1);
            if(clash!=null){message=L.Tr("Nothing was exported: texture sets {0} would write the same file {1}. Rename one in File ▸ Project Configuration.",String.Join(", ",clash.Select(t=>t.set.Name).Distinct()),clash.Key);return;}
            if(!ConfirmInactiveGenerators(planned.Select(t=>t.set).Distinct().Select(set=>(set.Name,set.Document))))return;
            string folder=Dialogs.OpenFolder("Export images into folder",projectPath!=null?Path.GetDirectoryName(projectPath):Application.dataPath);if(String.IsNullOrEmpty(folder))return;
            var targets=planned.Select(t=>(t.set,t.channel,path:Path.Combine(folder,t.name))).ToList();
            var existing=targets.Where(t=>File.Exists(t.path)).Select(t=>Path.GetFileName(t.path)).ToList();
            if(existing.Count>0&&!Dialogs.Confirm("Replace images?","These files will be replaced:\n"+String.Join("\n",existing),"Replace","Cancel"))return;
            TryAction(()=>
            {
                var notes=new List<string>();
                foreach(var t in targets)
                {
                    bool isNew=!File.Exists(t.path); var d=t.set.Document;
                    File.WriteAllBytes(t.path,YlpContent.EncodePng(PadForExport(t.set,YlpContent.FileImage(d,t.channel),notes),d.Width,d.Height));
                    string asset=AssetPathOf(Path.GetFullPath(t.path));
                    if(asset==null)continue;
                    AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
                    var importer=AssetImporter.GetAtPath(asset) as TextureImporter; if(importer==null)continue;
                    bool srgb=YlpContent.IsColor(t.channel);
                    if(isNew){importer.sRGBTexture=srgb;importer.alphaIsTransparency=t.channel==PaintChannel.Color;importer.SaveAndReimport();}
                    else if(importer.sRGBTexture!=srgb)notes.Add(Path.GetFileName(t.path)+" is imported as "+(importer.sRGBTexture?"sRGB":"linear")+" but "+t.channel+" is "+(srgb?"colour (sRGB)":"data (linear)")+"; its import settings were left as they are.");
                }
                bool normal=targets.Any(t=>t.channel==PaintChannel.Normal);
                message="Exported "+targets.Count+" image(s) to "+folder+"."+(notes.Count>0?" "+String.Join(" ",notes):"")+" No material was changed."+(normal?NormalExportNote(true):"");
            });
        }
        internal void ExportPng()
        {
            if(!ConfirmInactiveGenerators(new[]{(currentSet.Name,document)}))return;
            string path=Dialogs.SaveFile("Export selected channel PNG",Application.dataPath,(textureSets.Count>1?Sanitize(currentSet.Name)+"_":"")+channel+".png","png");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                var texture=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true);
                var notes=new List<string>();
                try{texture.LoadRawTextureData(PadForExport(currentSet,YlpContent.FileImage(document,channel),notes));texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());message="Exported "+channel+" PNG. No material was changed; ICC is not applied."+NormalExportNote(channel==PaintChannel.Normal)+(notes.Count>0?" "+String.Join(" ",notes):"");}
                finally{DestroyImmediate(texture);}
            });
        }
    }
}
