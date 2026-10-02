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
    /// <summary>ファイル: .ylp の保存と開く、復旧 checkpoint、PSD の取り込みと書き出し、画像の書き出し。</summary>
    public sealed partial class TexturePaintWindow
    {
        [Serializable] sealed class ViewState { public string modelAssetGuid; public int materialSlot; public int selectedChannel; }
        /// <summary>開いた .ylp の中身の形式（新しく作った・取り込んだものは今の形式）と、最初に作ったアプリ（形式 1 のファイルは分からないので null）。</summary>
        int openedFormat=YlpFormat.Current; YlpWriterInfo projectCreatedBy;
        internal int OpenedFormat=>openedFormat;
        /// <summary>新しく作った・取り込んだ文書: 今の形式で、作ったのはこのアプリ。</summary>
        void NewProjectRecord(){openedFormat=YlpFormat.Current;projectCreatedBy=YlpContent.Writer;}
        /// <summary>作った直後で何も手を加えていないドキュメントの版（New / 最初に開いたとき）。捨てても失うものが無いので確かめない。</summary>
        long pristineRevision=-1;
        bool ConfirmDiscard() => document.Revision==savedRevision || document.Revision==pristineRevision || Dialogs.Confirm("Keep current work?","Current work has unsaved changes. A native recovery checkpoint will be kept before opening another document.","Continue","Cancel") && SaveRecovery();
        bool SaveRecovery()
        {
            if(document==null||stroke!=null||document.Revision==recoveredRevision)return true;
            try
            {
                var files=new Dictionary<string,byte[]>{{"document.utpaint",DocumentBinary.Write(document)}};
                if(document.Selection!=null)files.Add(SelectionBinary.EntryName,SelectionBinary.Write(document.Selection));
                YlpFormat.Stamp(files,YlpContent.Writer,projectCreatedBy);
                var snapshot=GenerationStore.Commit(recoveryRoot,files,recoveryToken); recoveryToken=snapshot.Token;
                recoveredRevision=document.Revision;lastRecovery=EditorApplication.timeSinceStartup;return true;
            }
            catch(Exception ex){message="Recovery checkpoint failed: "+ex.Message;return false;}
        }
        /// <summary>保存した選択範囲を戻す（履歴も版も増やさない）。読めなければ選択なしで開き、そのことを知らせる（文書は開ける）。</summary>
        void RestoreSavedSelection(IReadOnlyDictionary<string,byte[]> files,List<string> notes)
        {
            if(!files.TryGetValue(SelectionBinary.EntryName,out var bytes))return;
            try{document.RestoreSelection(SelectionBinary.Read(bytes,document));}
            catch(InvalidDataException ex){notes.Add("The saved selection was not restored ("+ex.Message+"); nothing is selected, and saving will leave it out.");}
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
                var files=YlpContent.Composites(document);
                files.Add(YlpArchive.NativeName,DocumentBinary.Write(document));
                var state=new ViewState{modelAssetGuid=model==null?"":AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model)),materialSlot=materialSlot,selectedChannel=(int)channel};
                files.Add(YlpContent.ViewName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(state,true)));
                files.Add(YlpContent.BrushName,System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(brush,true)));
                if(importedOriginal!=null)files.Add(YlpContent.ImportedOriginalName,importedOriginal);
                AddMeshMapFiles(files);
                if(document.Selection!=null)files.Add(SelectionBinary.EntryName,SelectionBinary.Write(document.Selection));
                YlpFormat.Stamp(files,YlpContent.Writer,projectCreatedBy);
                var saved=YlpStore.Save(target,files,sameFile?projectToken:null,!sameFile,keep);
                projectPath=saved.Path;projectToken=saved.Token;savedRevision=document.Revision;externalConflict=false;MeshMapsWereSaved();openedFormat=YlpFormat.Current;
                message="Saved "+Path.GetFileName(saved.Path)+(saved.Backup!=null?"; the previous version is kept in "+Path.GetFileName(Path.GetDirectoryName(saved.Backup))+".":".");
                string asset=AssetPathOf(saved.Path);
                if(asset!=null)AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
            });
            Dialogs.ClearProgress();
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
            if(projectPath!=null&&String.Equals(path,projectPath,PainterSettings.PathComparison)&&document.Revision==savedRevision&&!externalConflict){message="Already open: "+Path.GetFileName(path);return;}
            if(!ConfirmDiscard())return;
            TryAction(()=>
            {
                var snapshot=YlpStore.Load(path);var opened=YlpFormat.Open(snapshot.Files);var files=opened.Files;
                var next=DocumentBinary.Read(files[YlpArchive.NativeName]);
                document=next;BindDocument();selectedLayer=document.Layers.Count>0?document.Layers[document.Layers.Count-1].Id:Guid.Empty;
                projectPath=snapshot.Path;projectToken=snapshot.Token;savedRevision=document.Revision;externalConflict=false;
                importedOriginal=files.TryGetValue(YlpContent.ImportedOriginalName,out var original)?original:null;
                openedFormat=opened.Info.Format; projectCreatedBy=opened.Info.CreatedBy;
                var notes=new List<string>();
                var budgetNote=ApplyBudgets(); if(budgetNote!=null)notes.Add(budgetNote);
                if(files.TryGetValue(YlpContent.BrushName,out var preset)){brush=ReadBrushState(System.Text.Encoding.UTF8.GetString(preset));var missing=MissingTipNote();if(missing!=null)notes.Add(missing);}
                if(files.TryGetValue(YlpContent.ViewName,out var view))
                {
                    var state=JsonUtility.FromJson<ViewState>(System.Text.Encoding.UTF8.GetString(view));materialSlot=state.materialSlot;channel=(PaintChannel)state.selectedChannel;
                    var loaded=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(state.modelAssetGuid));
                    if(loaded!=null){model=loaded;preview.Load(model);materialSlot=Mathf.Clamp(materialSlot,0,Mathf.Max(0,preview.MaterialSlotCount-1));}else if(!String.IsNullOrEmpty(state.modelAssetGuid))notes.Add("Model asset is unavailable; assign it explicitly.");
                }
                LoadMeshMapFiles(files,notes);
                RestoreSavedSelection(files,notes);
                notes.AddRange(FormatNotes(opened));
                message="Opened "+Path.GetFileName(path)+" (verified)"+(notes.Count>0?". "+String.Join(" ",notes):"");
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
                document=next;if(document.Layers.Count==0)document.AddLayer(L.Tr("Layer")+" 1");BindDocument();selectedLayer=document.Layers.Last().Id;projectPath=null;projectToken=null;savedRevision=-1;
                importedOriginal=result.CopyOriginalBytes();importedPsdPath=Path.GetFullPath(path);channel=PaintChannel.Color;NewProjectRecord();
                message="Imported "+Path.GetFileName(path)+". Save keeps it as a .ylp (with the original PSD inside); Export PSD writes a PSD. The PSD itself is never rewritten.";
                // 編集できる取り込みでも、書き出す PSD に含まれない情報や合成結果の差などの注意があれば一覧で見せる（黙って捨てない）
                var notes=result.Diagnostics.Select(d=>d.ToString()).ToList();
                if(notes.Count>0)Dialogs.Inform("PSD imported with notes",String.Join("\n",notes.Take(40))+(notes.Count>40?"\n… and "+(notes.Count-40)+" more.":"")+"\n\nThe original PSD bytes are kept inside the .ylp when you save.");
            });
        }
        /// <summary>選んだチャンネルを PSD に書き出す。PSD で表せないもの（通常以外の合成・マスク・Fill・調整・クリッピングなど）が
        /// あれば、平らにせずに理由を示して書かない。</summary>
        internal void ExportPsd()
        {
            byte[] bytes;
            try{bytes=PsdCodec.Write(PsdBridge.Export(document,channel));}
            catch(Exception ex){message="PSD export unavailable: "+ex.Message;Dialogs.Inform("PSD export unavailable",ex.Message+"\n\nNothing was written. The .ylp keeps everything losslessly.");return;}
            string source=projectPath??importedPsdPath;
            string stem=source!=null?Path.GetFileNameWithoutExtension(source):"Texture";
            string path=Dialogs.SaveFile("Export selected channel PSD",source!=null?Path.GetDirectoryName(source):Application.dataPath,channel==PaintChannel.Color?stem:stem+"_"+channel,"psd");if(String.IsNullOrEmpty(path))return;
            // 取り込み元の PSD を上書きするときは確かめる（原本のバイト列は .ylp に残るが、外の PSD そのものは置き換わる）
            if(importedPsdPath!=null&&String.Equals(Path.GetFullPath(path),importedPsdPath,PainterSettings.PathComparison)&&!Dialogs.Confirm("Overwrite the imported PSD?",Path.GetFileName(path)+" is the PSD this document was imported from. Replace it with the exported PSD?"+(importedOriginal!=null?" Its original bytes stay inside the .ylp once you save.":""),"Replace","Cancel"))return;
            TryAction(()=>{File.WriteAllBytes(path,bytes);message="Exported "+channel+" PSD. No material was changed."+NormalExportNote(channel==PaintChannel.Normal,psd:true);});
        }
        /// <summary>使っている全チャンネルを &lt;名前&gt;_&lt;チャンネル&gt;.png としてフォルダに書き出す。既存のファイルを置き換えるときは
        /// 確かめる。Assets の中なら取り込み直し、新しく作ったテクスチャにだけ色空間（Color/Emission は sRGB、他はリニア）を設定する。
        /// 既にあるテクスチャの取り込み設定は変えず、合っていなければ知らせる。マテリアルには割り当てない。</summary>
        internal void ExportImages()
        {
            var channels=YlpContent.UsedChannels(document);
            if(channels.Count==0){message="Nothing to export: no layer uses any channel.";return;}
            string folder=Dialogs.OpenFolder("Export images into folder",projectPath!=null?Path.GetDirectoryName(projectPath):Application.dataPath);if(String.IsNullOrEmpty(folder))return;
            string stem=projectPath!=null?Path.GetFileNameWithoutExtension(projectPath):"Texture";
            var targets=channels.Select(c=>(channel:c,path:Path.Combine(folder,stem+"_"+c+".png"))).ToList();
            var existing=targets.Where(t=>File.Exists(t.path)).Select(t=>Path.GetFileName(t.path)).ToList();
            if(existing.Count>0&&!Dialogs.Confirm("Replace images?","These files will be replaced:\n"+String.Join("\n",existing),"Replace","Cancel"))return;
            TryAction(()=>
            {
                var created=new List<(PaintChannel channel,string asset)>(); var notes=new List<string>();
                foreach(var t in targets)
                {
                    bool isNew=!File.Exists(t.path);
                    File.WriteAllBytes(t.path,YlpContent.EncodePng(YlpContent.FileImage(document,t.channel),document.Width,document.Height));
                    string asset=AssetPathOf(Path.GetFullPath(t.path));
                    if(asset==null)continue;
                    AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceUpdate);
                    var importer=AssetImporter.GetAtPath(asset) as TextureImporter; if(importer==null)continue;
                    bool srgb=YlpContent.IsColor(t.channel);
                    if(isNew){importer.sRGBTexture=srgb;importer.alphaIsTransparency=t.channel==PaintChannel.Color;importer.SaveAndReimport();created.Add((t.channel,asset));}
                    else if(importer.sRGBTexture!=srgb)notes.Add(Path.GetFileName(t.path)+" is imported as "+(importer.sRGBTexture?"sRGB":"linear")+" but "+t.channel+" is "+(srgb?"colour (sRGB)":"data (linear)")+"; its import settings were left as they are.");
                }
                message="Exported "+targets.Count+" image(s) to "+folder+"."+(notes.Count>0?" "+String.Join(" ",notes):"")+" No material was changed."+NormalExportNote(channels.Contains(PaintChannel.Normal));
            });
        }
        internal void ExportPng()
        {
            string path=Dialogs.SaveFile("Export selected channel PNG",Application.dataPath,channel+".png","png");if(String.IsNullOrEmpty(path))return;
            TryAction(()=>
            {
                var texture=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true);
                try{texture.LoadRawTextureData(YlpContent.FileImage(document,channel));texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());message="Exported "+channel+" PNG. No material was changed; ICC is not applied."+NormalExportNote(channel==PaintChannel.Normal);}
                finally{DestroyImmediate(texture);}
            });
        }
    }
}
