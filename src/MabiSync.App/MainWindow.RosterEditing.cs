using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    public sealed record RoleChoice(string Value,string Label);
    public IReadOnlyList<RoleChoice> EnsembleRoles {get;}=[new("initiator","发起者"),new("joiner","参与者")];
    public ObservableCollection<string> InstrumentOptions {get;}=["未指定",..InstrumentCatalog.Defaults];
    private int _rosterEdits;
    private bool _loadingInstruments;
    private readonly HashSet<TextBox> _numberSubmissions=[];
    private string InstrumentFilePath {
        get {var app=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);return Path.Combine(Path.GetFileName(app)=="App"?Path.GetDirectoryName(app)!:app,"Config","乐器列表.txt");}
    }
    private void ReloadInstruments()
    {
        _loadingInstruments=true;
        try {
            var lines=File.Exists(InstrumentFilePath)?File.ReadAllLines(InstrumentFilePath):Array.Empty<string>();
            foreach(var instrument in InstrumentCatalog.Merge(lines,_members.Select(m=>m.Instrument)))
                if(!InstrumentOptions.Contains(instrument))InstrumentOptions.Add(instrument);
        } catch(Exception error) when(error is IOException or UnauthorizedAccessException) {Feedback("乐器文件暂时无法读取，已保留内置与房间乐器。",FeedbackTone.Warning);}
        finally {_loadingInstruments=false;}
    }
    private void InstrumentDropdownOpened(object sender,EventArgs e)=>ReloadInstruments();
    private async void RosterRoleChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_refreshing || !CanEditEnsemble || sender is not ComboBox {DataContext:MemberRow row,SelectedValue:string role} box || !(box.IsKeyboardFocusWithin||box.IsDropDownOpen) || role==row.EnsembleRole)return;
        await EditRoster(()=>_client.UpdateRosterAsync("edit-ensemble-member",row.Id,musicRole:role),$"已更新 {row.Nickname} 的合奏身份。");
    }
    private async void RosterInstrumentChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_refreshing || _loadingInstruments || !CanEditEnsemble || sender is not ComboBox {DataContext:MemberRow row,SelectedItem:string instrument} box || !(box.IsKeyboardFocusWithin||box.IsDropDownOpen) || instrument==row.Instrument)return;
        await EditRoster(()=>_client.UpdateRosterAsync("edit-ensemble-member",row.Id,instrument:instrument),$"已为 {row.Nickname} 设置乐器：{instrument}。");
    }
    private async void RowSelectionChanged(MemberRow row)
    {
        if(_refreshing)return;
        if(!IsRoomHost || !ViewConnected || RoundActive){Refresh();Feedback("参与者由房主在开始前调整。",FeedbackTone.Warning);return;}
        await EditRoster(()=>_client.UpdateRosterAsync("set-member-selection",row.Id,selected:row.Selected),$"已更新 {row.Nickname} 的本轮参与状态。");
    }
    private async Task EditRoster(Func<Task> edit,string success)
    {
        _rosterEdits++;RefreshFlow();
        try {await edit();if(!_closing){Refresh();Feedback(success);}}
        catch(Exception error){if(!_closing){Refresh();Feedback(Friendly(error.Message),FeedbackTone.Error);}}
        finally {_rosterEdits--;if(!_closing)Refresh();}
    }
    private async void RosterNumberBlurred(object sender,KeyboardFocusChangedEventArgs e)=>await CommitRosterNumber((TextBox)sender);
    private async void RosterNumberKeyDown(object sender,KeyEventArgs e)
    {
        if(sender is not TextBox {DataContext:MemberRow row} box)return;
        if(e.Key==Key.Escape){box.Text=(box.Tag as string=="group"?row.Group:row.Order).ToString(CultureInfo.InvariantCulture);e.Handled=true;Feedback("已取消本次数字修改。",FeedbackTone.Info);}
        else if(e.Key==Key.Enter){e.Handled=true;await CommitRosterNumber(box);}
    }
    private async Task CommitRosterNumber(TextBox box)
    {
        if(_refreshing || _closing || !CanEditEnsemble || box.DataContext is not MemberRow row || !_numberSubmissions.Add(box))return;
        var group=box.Tag as string=="group";var current=group?row.Group:row.Order;
        try {
            if(!int.TryParse(box.Text,NumberStyles.None,CultureInfo.InvariantCulture,out var value) || value is <1 or >24) {
                Feedback(group?"合奏组请输入 1–24。":"顺序请输入 1–24；组内会自动连续排列。",FeedbackTone.Error);return;
            }
            if(value==current)return;
            await EditRoster(()=>_client.UpdateRosterAsync("edit-ensemble-member",row.Id,group:group?value:null,order:group?null:value),$"已更新 {row.Nickname} 的{(group?"分组":"顺序")}，列表已同步。");
            if(!group && row.Order!=value)Feedback($"已排为组内第 {row.Order} 位；发起者固定首位，其余成员连续排列。",FeedbackTone.Info);
        } finally {box.Text=(group?row.Group:row.Order).ToString(CultureInfo.InvariantCulture);_numberSubmissions.Remove(box);}
    }
}
