using System.Windows;
using System.Windows.Interop;
using MabiSync.Core;
using MabiSync.Windows;

namespace MabiSync.App;

public partial class MainWindow
{
    private Action<string>? _clipboardWriterOverride=null;
    private bool _copyingInvitation;
    private double _copiedInvitationUntil;
    private async Task CopyInvitationAsync()
    {
        if(_copyingInvitation)return;
        if(CurrentInvitation is not{} invitation)
            throw new InvalidOperationException("公网邀请尚未就绪，请等待连接建立后重试。");
        _copyingInvitation=true;CopyStatusText.Text="正在复制邀请……";CopyStatusText.Visibility=Visibility.Visible;
        FallbackInvitationPanel.Visibility=Visibility.Collapsed;
        try {
            Action<string> write=_clipboardWriterOverride??(text=>ClipboardCopy.WriteText(new WindowInteropHelper(this).EnsureHandle(),text));
            await ClipboardCopy.WriteAsync(invitation,write);
            _copiedInvitationUntil=_clock.NowMs+6000;
            CopyStatusText.Text="邀请已复制，可以发送给好友。";CopyStatusText.Foreground=ColorBrush("#86E1B2");
            Feedback("已复制完整邀请。好友粘贴后点击“加入邀请”即可。");
        } catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException) {
            _copiedInvitationUntil=0;
            FallbackInvitationBox.Text=invitation;FallbackInvitationPanel.Visibility=Visibility.Visible;
            CopyStatusText.Text="自动复制暂未完成，可在下框选中邀请后按 Ctrl+C。";CopyStatusText.Foreground=ColorBrush("#F6D58B");
            Feedback("邀请已显示在复制按钮下方，可手动复制。",FeedbackTone.Warning);
        } finally {_copyingInvitation=false;RefreshCopyStatus();}
    }
    private void RefreshCopyStatus()
    {
        CopyPublicButton.Content=_copyingInvitation?"正在复制…":_clock.NowMs<_copiedInvitationUntil?"已复制 ✓":"复制邀请";
        if(ViewSession is null || !ShareReady){
            _copiedInvitationUntil=0;CopyStatusText.Visibility=Visibility.Collapsed;
            FallbackInvitationPanel.Visibility=Visibility.Collapsed;FallbackInvitationBox.Clear();
        }
    }
}
