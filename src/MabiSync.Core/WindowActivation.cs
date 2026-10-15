namespace MabiSync.Core;

public static class WindowActivation
{
    public static async Task ActivateAsync(GameWindowId window,Func<GameWindowId,GameWindowState?> inspect,
        Action<GameWindowId> restore,Action<GameWindowId> foreground,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state=inspect(window)??throw new InvalidOperationException("游戏窗口已失效，请重新选择。");
        if(state.Identity!=window)throw new InvalidOperationException("游戏进程已更换，请重新选择窗口。");
        if(!state.InputPermission)throw new InvalidOperationException("工具与游戏输入权限不匹配，请使用相同权限运行。");
        if(state.Foreground && state.Visible)return;
        if(!state.Visible)restore(window);
        foreground(window);
        for(var i=0;i<20;i++) {
            token.ThrowIfCancellationRequested();
            var current=inspect(window);
            if(current?.Identity!=window)throw new InvalidOperationException("游戏窗口已改变，请重新选择。");
            if(current.Visible && current.Foreground)return;
            if(i==5)foreground(window);
            await Task.Delay(50,token);
        }
        throw new InvalidOperationException("系统未允许激活游戏窗口，请手动切回游戏后重试开始。");
    }
}
