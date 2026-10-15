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
    public Brush NameBrush=>Brush(IsSelf?"#245AC1":"#24334C");
    public bool ReadOnly=>!CanEdit;
    public string JoinState {get;private set;}="unconfirmed";
    public string JoinFeedback {get;private set;}="等待游戏事件核对";
    public Brush JoinBrush=>Brush(JoinState switch{"correct"=>"#176B42","wrong"=>"#B42332","pending"=>"#875B0A",_=>"#4D607D"});
    public Brush JoinBackground=>Brush(JoinState switch{"correct"=>"#E4F5EB","wrong"=>"#FDECEF","pending"=>"#FFF3D8",_=>"#ECF1F8"});
    public Brush StatusBrush=>Brush(Status switch{"已就绪"=>"#176B42","未就绪"=>"#B42332","房主"=>"#245AC1",_=>"#5C687A"});
    public Brush StatusBackground=>Brush(Status switch{"已就绪"=>"#E4F5EB","未就绪"=>"#FDECEF","房主"=>"#E8F0FF",_=>"#EDF0F5"});
    public string GroupText=>$"第 {Group} 组";
    public string OrderText=>Order.ToString();
    public string EnsembleRoleText=>EnsembleRole=="initiator"?"发起者":"参与者";
    private bool _selected=true;
    public bool Selected {get=>_selected;set{if(_selected==value)return;_selected=value;Changed(nameof(Selected));selectionChanged?.Invoke(this);}}
    public event PropertyChangedEventHandler? PropertyChanged;
    private static Brush Brush(string color) {var result=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));result.Freeze();return result;}
    private void Changed(string property)=>PropertyChanged?.Invoke(this,new(property));
    public void Update(JsonElement member,bool canEdit=false,bool isSelf=false)
    {
        // Notify only values that changed. Clock/state refreshes must not overwrite a number being typed.
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
