using System.Windows;
using System.Windows.Controls;

namespace MabiSync.App;

public partial class MainWindow
{
    private void RosterViewportChanged(object sender, SizeChangedEventArgs e)=>LayoutRosterColumns();
    private void LayoutRosterColumns()
    {
        if(MembersGrid is null || NicknameColumn is null || JoinFeedbackColumn is null)return;
        var ensemble=SelectedMode=="ensemble";
        MembersGrid.MinWidth=ensemble?920:0;
        var width=Math.Max(MembersGrid.MinWidth,RosterViewport.ActualWidth);
        if(width<=0)return;
        var fixedWidth=MembersGrid.Columns.Where(column=>column.Visibility==Visibility.Visible && column!=NicknameColumn && column!=JoinFeedbackColumn).Sum(column=>column.Width.Value);
        var available=Math.Max(130,width-fixedWidth-4);
        var nameWidth=ensemble?130+Math.Max(0,available-300)*.45:available;
        NicknameColumn.Width=new DataGridLength(nameWidth);
        if(ensemble)JoinFeedbackColumn.Width=new DataGridLength(Math.Max(170,available-nameWidth));
    }
}
