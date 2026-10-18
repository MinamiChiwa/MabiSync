using System.Windows;
using System.Windows.Input;

namespace MabiSync.App;

public partial class InformationWindow : Window
{
    internal Action? ReadyRequested, StartRequested;
    internal Action<double>? OpacityUpdated;
    public InformationWindow()
    {
        InitializeComponent();
        Opacity=OpacitySlider.Value;
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if(e.ChangedButton==MouseButton.Left && e.ButtonState==MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
            DragMove();
    }
    private void CloseClick(object sender,RoutedEventArgs e)=>Close();
    private void ReadyClick(object sender,RoutedEventArgs e)=>ReadyRequested?.Invoke();
    private void StartClick(object sender,RoutedEventArgs e)=>StartRequested?.Invoke();
    private void MembersExpansionChanged(object sender,RoutedEventArgs e)
    {
        // Manual resizing disables automatic height; restore it for both toggle directions.
        SizeToContent=SizeToContent.Height;
    }
    private void OpacityChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity=e.NewValue;
        if(OpacityText is not null)OpacityText.Text=$"{e.NewValue:P0}";
        OpacityUpdated?.Invoke(e.NewValue);
    }
}
