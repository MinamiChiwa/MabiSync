using System.ComponentModel;
using System.Text.Json;
using System.Windows.Media;

namespace MabiSync.App;

internal sealed class MemberRow(string id,Action<MemberRow>? selectionChanged=null) : INotifyPropertyChanged
{
    public string Id {get;}=id;
    public string Nickname {get;private set;}="";
    public string Role {get;private set;}="";
    public string Status {get;private set;}="";
    public int Group {get;private set;}=1;
    public int Order {get;private set;}
    public string Instrument {get;private set;}="";
    public string EnsembleRole {get;private set;}="joiner";
    public bool Ready {get;private set;}
    public bool CanEdit {get;private set;}
    public bool IsSelf {get;private set;}
    public System.Windows.FontWeight NameWeight=>IsSelf?System.Windows.FontWeights.SemiBold:System.Windows.FontWeights.Normal;
    public Brush NameBrush=>Brush(IsSelf?"#9DC7FF":"#E2E8F0");
    public bool ReadOnly=>!CanEdit;
    public string JoinState {get;private set;}="unconfirmed";
    public string JoinFeedback {get;private set;}="等待游戏事件核对";
    public Brush JoinBrush=>Brush(JoinState switch{"correct"=>"#86E1B2","wrong"=>"#FFAFB9","pending"=>"#F6D58B",_=>"#C2CEE0"});
    public Brush JoinBackground=>Brush(JoinState switch{"correct"=>"#173D32","wrong"=>"#482730","pending"=>"#44391F",_=>"#2A3547"});
    public Brush StatusBrush=>Brush(Status switch{"已就绪"=>"#86E1B2","未就绪"=>"#FFAFB9","房主"=>"#9DC7FF",_=>"#B6C3D5"});
    public Brush StatusBackground=>Brush(Status switch{"已就绪"=>"#173D32","未就绪"=>"#482730","房主"=>"#223D60",_=>"#2A3547"});
    private bool _ensemble;
    private static readonly Brush[] GroupColors=[Brush("#24303B"),Brush("#3B2429"),Brush("#243B26"),Brush("#2C243B"),Brush("#3B3324"),Brush("#243B39"),Brush("#3B2436"),Brush("#2F3B24"),Brush("#24293B"),Brush("#3B2724"),Brush("#243B2D"),Brush("#34243B"),Brush("#3B3A24"),Brush("#24353B"),Brush("#3B242E"),Brush("#283B24"),Brush("#28243B"),Brush("#3B2E24"),Brush("#243B35"),Brush("#3B243A"),Brush("#343B24"),Brush("#242D3B"),Brush("#3B2427"),Brush("#243B29")];
    public Brush GroupBackground=>_ensemble?GroupColors[(Math.Max(1,Group)-1)%GroupColors.Length]:Brush("#1A2331");
    public string GroupText=>$"第 {Group} 组";
    public string OrderText=>Order.ToString();
    public string EnsembleRoleText=>EnsembleRole=="initiator"?"乐谱使用者":"参与者";
    private bool _selected=true;
    public bool Selected {get=>_selected;set{if(_selected==value)return;_selected=value;Changed(nameof(Selected));selectionChanged?.Invoke(this);}}
    public event PropertyChangedEventHandler? PropertyChanged;
    private static Brush Brush(string color) {var result=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));result.Freeze();return result;}
    private void Changed(string property)=>PropertyChanged?.Invoke(this,new(property));
    public void Update(JsonElement member,bool canEdit=false,bool isSelf=false,bool ensemble=false)
    {
        // Notify only values that changed. Clock/state refreshes must not overwrite a number being typed.
        var oldEnsemble=_ensemble;_ensemble=ensemble;
        var old=(Nickname,Role,Status,Group,Order,Instrument,EnsembleRole,Ready,JoinState,JoinFeedback,CanEdit,_selected,IsSelf);
        Nickname=member.GetProperty("nickname").GetString()??"";
        Role=member.GetProperty("role").GetString()=="host"?"房主":"成员";
        Ready=member.GetProperty("ready").GetBoolean();
        Status=!member.GetProperty("connected").GetBoolean()?"已离线":Role=="房主"?"房主":Ready?"已就绪":"未就绪";
        Group=member.TryGetProperty("ensembleGroup",out var group)?group.GetInt32():1;
        Order=member.TryGetProperty("ensembleOrder",out var order)?order.GetInt32():0;
        EnsembleRole=member.TryGetProperty("ensembleRole",out var role)?role.GetString()??"joiner":"joiner";
        Instrument=member.TryGetProperty("instrument",out var instrument)&&!string.IsNullOrWhiteSpace(instrument.GetString())?instrument.GetString()!:"未指定";
        CanEdit=canEdit;
        IsSelf=isSelf;
        _selected=!member.TryGetProperty("selected",out var selected)||selected.GetBoolean();
        JoinState=member.TryGetProperty("ensembleFeedback",out var feedback)&&feedback.ValueKind==JsonValueKind.Object?feedback.GetProperty("state").GetString()??"unconfirmed":"unconfirmed";
        JoinFeedback=feedback.ValueKind==JsonValueKind.Object?feedback.GetProperty("text").GetString()??"等待核对":"等待游戏事件核对";
        if(feedback.ValueKind==JsonValueKind.Object && feedback.TryGetProperty("actualOrder",out var actual))JoinFeedback+=$" · 实际第 {actual.GetInt32()} 位";
        if(old.Nickname!=Nickname)Changed(nameof(Nickname));if(old.Role!=Role)Changed(nameof(Role));
        if(old.Status!=Status){Changed(nameof(Status));Changed(nameof(StatusBrush));Changed(nameof(StatusBackground));}
        if(old.Ready!=Ready)Changed(nameof(Ready));
        if(old.Group!=Group){Changed(nameof(Group));Changed(nameof(GroupText));}
        if(old.Group!=Group || oldEnsemble!=_ensemble)Changed(nameof(GroupBackground));
        if(old.Order!=Order){Changed(nameof(Order));Changed(nameof(OrderText));}
        if(old.Instrument!=Instrument)Changed(nameof(Instrument));
        if(old.EnsembleRole!=EnsembleRole){Changed(nameof(EnsembleRole));Changed(nameof(EnsembleRoleText));}
        if(old.CanEdit!=CanEdit){Changed(nameof(CanEdit));Changed(nameof(ReadOnly));}
        if(old.IsSelf!=IsSelf){Changed(nameof(IsSelf));Changed(nameof(NameBrush));Changed(nameof(NameWeight));}
        if(old._selected!=_selected)Changed(nameof(Selected));
        if(old.JoinState!=JoinState){Changed(nameof(JoinState));Changed(nameof(JoinBrush));Changed(nameof(JoinBackground));}
        if(old.JoinFeedback!=JoinFeedback)Changed(nameof(JoinFeedback));
    }
}
