using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal sealed class CursorTracker : IAsyncDisposable
{
    private readonly RelayClient _relay; private CancellationTokenSource? _cts; private CaptureSource? _source; private Func<bool>? _visiblePolicy;
    private double _lastX=-1,_lastY=-1; private bool _lastVisible;
    public CursorTracker(RelayClient relay) => _relay = relay;
    public void Start(CaptureSource source, Func<bool> visiblePolicy) { Stop(); _source=source; _visiblePolicy=visiblePolicy; _cts=new CancellationTokenSource(); _=Task.Run(()=>Loop(_cts.Token)); }
    public void Stop(){ try{_cts?.Cancel();}catch{} _cts?.Dispose(); _cts=null; _source=null; }
    private async Task Loop(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var src=_source; var bounds=src?.Bounds??Rectangle.Empty; if(src?.Kind==SourceKind.Window && SourceEnumerator.TryGetBounds(src.WindowHandle,out var current)) bounds=current;
                var show=_visiblePolicy?.Invoke()==true; var vis=false; var x=0d; var y=0d;
                if(show && bounds.Width>0 && bounds.Height>0 && GetCursorPos(out var p)) { vis=bounds.Contains(p.X,p.Y); if(vis){x=Math.Clamp((p.X-bounds.Left)/(double)bounds.Width,0,1);y=Math.Clamp((p.Y-bounds.Top)/(double)bounds.Height,0,1);} }
                if(vis!=_lastVisible || Math.Abs(x-_lastX)>.0015 || Math.Abs(y-_lastY)>.0015){_lastVisible=vis;_lastX=x;_lastY=y;_relay.QueueControl(new{type="cursor",x,y,visible=vis,w=32d/Math.Max(1,bounds.Width),h=32d/Math.Max(1,bounds.Height),hx=.05,hy=.05});}
            }catch{}
            try{await Task.Delay(33,token);}catch{break;}
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT{public int X,Y;}
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    public ValueTask DisposeAsync(){Stop();return ValueTask.CompletedTask;}
}
