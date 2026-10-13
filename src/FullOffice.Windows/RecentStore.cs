using System.Text.Json;

namespace FlowOfficeLab;

public sealed class RecentDocument
{
    public string Path {get;set;}="";
    public DateTimeOffset Opened {get;set;}=DateTimeOffset.UtcNow;
    public int Page {get;set;}
    public double Zoom {get;set;}=1;
    public bool FitWholePage {get;set;}
    public bool WheelPageMode {get;set;}
    public bool SidebarCollapsed {get;set;}
    public double Offset {get;set;}
}
public sealed class RecentStore
{
    readonly string path;
    public List<RecentDocument> Items {get;}=new();
    public bool RadialEnabled {get;set;}=true;
    public bool OfficeAutoSave {get;set;}
    public string? Error {get;private set;}
    record State(int Version,bool RadialEnabled,List<RecentDocument> Recent,bool OfficeAutoSave = false);
    public RecentStore(string directory){
        path=System.IO.Path.Combine(directory,"reading-state.json");
        try{if(File.Exists(path)){var data=JsonSerializer.Deserialize<State>(File.ReadAllText(path));if(data!=null){RadialEnabled=data.RadialEnabled;OfficeAutoSave=data.OfficeAutoSave;Items.AddRange(data.Recent.Where(i=>!string.IsNullOrWhiteSpace(i.Path)).DistinctBy(i=>i.Path,StringComparer.OrdinalIgnoreCase).Take(20));}}}catch(Exception ex){Error="最近打开记录无法读取："+ex.Message;}
    }
    public RecentDocument? Find(string file){file=System.IO.Path.GetFullPath(file);return Items.FirstOrDefault(i=>string.Equals(i.Path,file,StringComparison.OrdinalIgnoreCase));}
    public void Record(RecentDocument state,bool opened){state.Path=System.IO.Path.GetFullPath(state.Path);var previous=Find(state.Path);state.Opened=opened?DateTimeOffset.UtcNow:previous?.Opened??state.Opened;Items.RemoveAll(i=>string.Equals(i.Path,state.Path,StringComparison.OrdinalIgnoreCase));Items.Add(state);Items.Sort((a,b)=>b.Opened.CompareTo(a.Opened));if(Items.Count>20)Items.RemoveRange(20,Items.Count-20);Save();}
    public void Remove(string file){file=System.IO.Path.GetFullPath(file);Items.RemoveAll(i=>string.Equals(i.Path,file,StringComparison.OrdinalIgnoreCase));Save();}
    public void Clear(){Items.Clear();Save();}
    public void Save(){string? temporary=null;try{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(new State(1,RadialEnabled,Items,OfficeAutoSave),new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,path,true);Error=null;}catch(Exception ex){Error="阅读记录暂时无法保存："+ex.Message;}finally{if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);}}
}
