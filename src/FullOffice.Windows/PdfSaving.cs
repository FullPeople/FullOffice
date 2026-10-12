using System.Runtime.InteropServices;
using static FlowOfficeLab.PdfNative;

namespace FlowOfficeLab;

public sealed partial class PdfEngine
{
    readonly record struct SourceStamp(long Length,long WrittenTicks)
    {
        public static SourceStamp Read(string path){var file=new FileInfo(path);if(!file.Exists)throw new IOException("当前 PDF 已被移动或删除，无法写回。可以另存副本保留修改。");return new(file.Length,file.LastWriteTimeUtc.Ticks);}
    }
    SourceStamp sourceStamp;
    GCHandle documentBuffer;
    public string? LastBackupPath {get;private set;}
    void ReleaseDocument(){if(document!=IntPtr.Zero){FPDF_CloseDocument(document);document=IntPtr.Zero;}if(documentBuffer.IsAllocated)documentBuffer.Free();}
    void CheckSourceForSave(){
        if(SourceStamp.Read(sourcePath)!=sourceStamp)throw new IOException("当前 PDF 已被其他程序修改，未覆盖这些修改。可以另存副本，或重新打开文件后继续编辑。");
        var attributes=File.GetAttributes(sourcePath);if((attributes&FileAttributes.ReadOnly)!=0)throw new IOException("当前 PDF 为只读文件。请取消文件的只读属性后重试，或另存副本。");
        if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("当前 PDF 是文件链接。请直接打开实际文件后保存，或另存副本。");
    }
    public Task SaveCopyAsync(string path)=>SaveDocument(path,false);
    public Task SaveInPlaceAsync()=>SaveDocument(null,true);
    Task SaveDocument(string? destination,bool inPlace){
        var outlines=currentBookmarks.Select(b=>b.Clone()).ToList();var tags=currentPageTags.ToArray();bool saveOutlines=writeOutlines,saveTags=writeTags;
        return PdfScheduler.Run(()=>{lock(Gate){
            EnsureDocument();string path=inPlace?sourcePath:Path.GetFullPath(destination!);
            if(!inPlace&&string.Equals(path,sourcePath,StringComparison.OrdinalIgnoreCase))throw new IOException("另存副本需要使用不同的文件名。写回当前文件请使用“保存”。");
            if(inPlace){if(!Modified)return;CheckSourceForSave();}
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp",outlineTemporary=temporary+".outlines.pdf";
            GCHandle buffer=default;IntPtr candidate=IntPtr.Zero;
            try{
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write)){
                    Exception? writeError=null;WriteCallback callback=(_,ptr,size)=>{try{var bytes=new byte[checked((int)size)];Marshal.Copy(ptr,bytes,0,bytes.Length);stream.Write(bytes);return 1;}catch(Exception ex){writeError=ex;return 0;}};
                    var writer=new Writer{version=1,WriteBlock=callback};int saved=FPDF_SaveAsCopy(document,ref writer,2);GC.KeepAlive(callback);
                    if(writeError!=null)throw new IOException("保存失败。",writeError);if(saved==0)throw new IOException("PDF 写入失败。");stream.Flush(true);
                }
                // Preserve the native document's outline identities for undo and
                // repeated saves; UI outline edits are applied only to the output.
                if(inPlace){var bytes=File.ReadAllBytes(temporary);buffer=GCHandle.Alloc(bytes,GCHandleType.Pinned);candidate=FPDF_LoadMemDocument64(buffer.AddrOfPinnedObject(),(nuint)bytes.LongLength,null);if(candidate==IntPtr.Zero||FPDF_GetPageCount(candidate)!=PageCount)throw new IOException("保存内容未通过读取检查，原文件没有被替换。");}
                if(saveOutlines||saveTags){PdfOutlineStore.Rewrite(temporary,outlineTemporary,outlines,saveTags?tags:null,saveOutlines);File.Move(outlineTemporary,temporary,true);}
                var verify=FPDF_LoadDocument(temporary,null);if(verify==IntPtr.Zero)throw new IOException("保存的 PDF 未通过读取检查。");try{if(FPDF_GetPageCount(verify)!=PageCount)throw new IOException("保存后的页数发生变化。");}finally{FPDF_CloseDocument(verify);}
                if(!inPlace){File.Move(temporary,path,true);return;}
                CheckSourceForSave();string folder=Path.Combine(Path.GetDirectoryName(path)!,".FlowOfficeBackups");
                if(Directory.Exists(folder)&&(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("恢复备份目录是文件链接，无法安全保存。请移除该链接后重试。");
                Directory.CreateDirectory(folder);File.SetAttributes(folder,File.GetAttributes(folder)|FileAttributes.Hidden);
                string backup=Path.Combine(folder,Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".bak"),latest=Path.Combine(folder,Path.GetFileName(path)+".previous.pdf");
                // The candidate owns its pinned buffer before the original file
                // handle is closed, so failed replacement retains every edit.
                ReleaseDocument();document=candidate;candidate=IntPtr.Zero;documentBuffer=buffer;buffer=default;
                CheckSourceForSave();File.Replace(temporary,path,backup,true);LastBackupPath=backup;
                sourceStamp=SourceStamp.Read(path);annotationModified=false;bookmarkModified=false;tagModified=false;
                try{File.Move(backup,latest,true);LastBackupPath=latest;}catch(IOException){}catch(UnauthorizedAccessException){}
            }finally{
                if(candidate!=IntPtr.Zero)FPDF_CloseDocument(candidate);if(buffer.IsAllocated)buffer.Free();
                TryRemoveTemporary(temporary);TryRemoveTemporary(outlineTemporary);
            }
        }});
    }
    static void TryRemoveTemporary(string path){try{if(File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
