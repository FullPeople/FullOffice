using System.Windows;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    TaskCompletionSource? pendingPdfSave;
    void BeginPdfSave(){saving=true;pendingPdfSave=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    void FinishPdfSave(){saving=false;pendingPdfSave?.TrySetResult();pendingPdfSave=null;Host.ReaderChanged(this);}
    async Task<bool> SaveCurrent(bool showErrors=true){
        if(currentFile==""||saving||disposed)return false;
        BeginPdfSave();Host.ReaderChanged(this);status.Text="正在保存到当前 PDF…";
        try{
            await bookmarkAppearanceTask;
            if(!engine.Modified){status.Text="当前 PDF 已保存";return true;}
            engine.SetBookmarks(bookmarks,false);await engine.SaveInPlaceAsync();status.Text="已保存到当前 PDF";return true;
        }catch(Exception ex){status.Text="保存失败，修改仍保留在当前窗口";if(showErrors)MessageBox.Show(Host,"未能保存到当前 PDF。\n"+ex.Message+"\n未保存的修改仍保留在当前窗口。","保存失败",MessageBoxButton.OK,MessageBoxImage.Error);return false;}
        finally{FinishPdfSave();}
    }
    async Task SaveCopy(){
        if(currentFile==""||saving||disposed)return;
        var dialog=new SaveFileDialog{Filter="PDF 文件|*.pdf",FileName=Path.GetFileNameWithoutExtension(currentFile)+"-阅读标记.pdf",OverwritePrompt=true};if(dialog.ShowDialog(Host)!=true)return;
        BeginPdfSave();Host.ReaderChanged(this);status.Text="正在另存副本…";
        try{await bookmarkAppearanceTask;engine.SetBookmarks(bookmarks,false);await engine.SaveCopyAsync(dialog.FileName);status.Text=engine.Modified?"副本已保存，当前 PDF 仍有未保存的修改":"副本已保存";}
        finally{FinishPdfSave();}
    }
}
