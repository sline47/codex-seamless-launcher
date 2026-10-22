using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class RezeTransitionLauncher : Form
{
    readonly List<byte[]> frames = new List<byte[]>();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = new Stopwatch();
    readonly bool test;
    bool waitTest;
    bool waitVerified;
    Bitmap current;
    Bitmap blinkPatch, pullPatch;
    int frame = -1;
    bool finished;
    Process skinProcess;
    double fadeStarted = -1;
    double lastCheck = -1;
    bool ready;
    double handoffPrepared = -1;
    string helperPath;
    IntPtr appWindow;
    int expectedWindowPid;
    volatile bool sessionVerified;
    static readonly object logLock = new object();
    static readonly string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"launcher-start.log");
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd,int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd,int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd,IntPtr after,int x,int y,int w,int h,uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out NativeRect rect,int size);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct WindowPlacement {
        public int Length,Flags,ShowCmd;
        public Point MinPosition,MaxPosition;
        public NativeRect NormalPosition;
    }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd,out NativeRect rect);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd,ref WindowPlacement placement);
    static bool GetVisibleBounds(IntPtr hwnd,out NativeRect rect) {
        return DwmGetWindowAttribute(hwnd,9,out rect,Marshal.SizeOf(typeof(NativeRect)))==0 || GetWindowRect(hwnd,out rect);
    }
    bool AlignChatToAnimation() {
        NativeRect outer,visible;
        if(!GetWindowRect(appWindow,out outer) || !GetVisibleBounds(appWindow,out visible))return false;
        Rectangle target=Bounds;
        return SetWindowPos(appWindow,Handle,
            target.Left-(visible.Left-outer.Left),target.Top-(visible.Top-outer.Top),
            target.Width+(outer.Right-outer.Left)-(visible.Right-visible.Left),
            target.Height+(outer.Bottom-outer.Top)-(visible.Bottom-visible.Top),0x0010);
    }
    bool ChatIsAligned() {
        NativeRect visible;
        if(!IsWindowVisible(appWindow) || IsIconic(appWindow) || !GetVisibleBounds(appWindow,out visible))return false;
        return Math.Abs(visible.Left-Left)<=1 && Math.Abs(visible.Top-Top)<=1 &&
            Math.Abs(visible.Right-Right)<=1 && Math.Abs(visible.Bottom-Bottom)<=1;
    }
    bool MaintainAlignment(double now) {
        if(ChatIsAligned())return true;
        Opacity=1;fadeStarted=-1;
        NativeRect visible;
        if(!IsWindowVisible(appWindow) || IsIconic(appWindow) || !GetVisibleBounds(appWindow,out visible) ||
            visible.Right-visible.Left<300 || visible.Bottom-visible.Top<200) {
            handoffPrepared=-1;return false;
        }
        Bounds=Rectangle.FromLTRB(visible.Left,visible.Top,visible.Right,visible.Bottom);
        DwmFlush();
        handoffPrepared=now;
        Log("Handoff paused; resynchronized visible frame: "+Bounds);
        return false;
    }
    bool MatchChatBounds() {
        if(!FindAppWindow())return false;
        NativeRect rect;
        if(IsIconic(appWindow)) {
            var placement=new WindowPlacement();placement.Length=Marshal.SizeOf(typeof(WindowPlacement));
            if(!GetWindowPlacement(appWindow,ref placement))return false;
            if((placement.Flags&2)!=0) { Bounds=Screen.FromHandle(appWindow).WorkingArea;return true; }
            rect=placement.NormalPosition;
            Rectangle work=Screen.FromHandle(appWindow).WorkingArea;
            Rectangle screen=Screen.FromHandle(appWindow).Bounds;
            rect.Left+=work.Left-screen.Left;rect.Right+=work.Left-screen.Left;
            rect.Top+=work.Top-screen.Top;rect.Bottom+=work.Top-screen.Top;
        } else if(!GetVisibleBounds(appWindow,out rect))return false;
        Rectangle target=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
        if(target.Width<300 || target.Height<200)return false;
        Bounds=target;
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chat-window-bounds-physical.txt"),String.Join(",",target.X,target.Y,target.Width,target.Height)); } catch { }
        Log("Matched chat bounds: "+target);
        return true;
    }
    static void Log(string message) {
        try { lock(logLock) File.AppendAllText(logPath,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+message+Environment.NewLine); } catch { }
    }
    public RezeTransitionLauncher(bool testing) {
        test = testing;
        Text = "蕾塞 · Dream Skin 启动中";
        BackColor = Color.FromArgb(16,9,28);
        AutoScaleMode = AutoScaleMode.None;
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = area;
        using (Stream iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("codex.ico"))
            Icon = new Icon(iconStream);
        FormBorderStyle = FormBorderStyle.None;
        // Cover the native loading window from the first animation frame.
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        if(!MatchChatBounds()) {
            try {
                string saved=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chat-window-bounds-physical.txt");
                string[] parts=File.ReadAllText(saved).Split(',');
                if(parts.Length==4) {
                    var previous=new Rectangle(Int32.Parse(parts[0]),Int32.Parse(parts[1]),Int32.Parse(parts[2]),Int32.Parse(parts[3]));
                    if(previous.Width>=300 && previous.Height>=200 && Screen.FromRectangle(previous).WorkingArea.IntersectsWith(previous))Bounds=previous;
                }
            } catch { }
        }
        DoubleBuffered = true;
        KeyPreview = true;
        AccessibleName = "蕾塞 Codex 启动动画";
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("intro.zip"))
        using (ZipArchive zip = new ZipArchive(input,ZipArchiveMode.Read)) {
            var entries = new List<ZipArchiveEntry>(zip.Entries);
            entries.Sort(delegate(ZipArchiveEntry a,ZipArchiveEntry b){return String.CompareOrdinal(a.FullName,b.FullName);});
            foreach(var entry in entries) {
                using(Stream stream=entry.Open()) using(var buffer=new MemoryStream()) { stream.CopyTo(buffer);frames.Add(buffer.ToArray()); }
            }
        }
        using(Stream poseStream=Assembly.GetExecutingAssembly().GetManifestResourceStream("motion.png"))
        using(var poseImage=Image.FromStream(poseStream))
        using(var pose=new Bitmap(1280,720)) {
            using(var graphics=Graphics.FromImage(pose))graphics.DrawImage(poseImage,0,0,1280,720);
            blinkPatch=MakePatch(pose,new Rectangle(978,62,100,68),10);
            pullPatch=MakePatch(pose,new Rectangle(708,143,285,174),18);
        }
        if(frames.Count!=210) throw new InvalidDataException("动画帧不完整。");
        timer.Interval=15;
        timer.Tick+=delegate { UpdateFrame(); };
        Shown+=delegate { if(!test && !LaunchSkin()) { Close();return; } clock.Start(); UpdateFrame();timer.Start(); };
    }
    bool LaunchSkin() {
        try {
            string engine=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexDreamSkin","engine","scripts","start-dream-skin.ps1");
            if(!File.Exists(engine)) throw new FileNotFoundException("请先完成 Codex Dream Skin 安装，再使用视频效果版启动器。");
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
            helperPath=Path.Combine(Path.GetTempPath(),"reze-start-"+Guid.NewGuid().ToString("N")+".ps1");
            using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("start-skin.ps1"))
            using(var buffer=new MemoryStream()) { resource.CopyTo(buffer);File.WriteAllBytes(helperPath,buffer.ToArray()); }
            var info=new ProcessStartInfo(powershell,"-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File \""+helperPath+"\"") {
                UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(engine),RedirectStandardOutput=true,RedirectStandardError=true
            };
            info.EnvironmentVariables["PSModulePath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","Modules")+";"+info.EnvironmentVariables["PSModulePath"];
            Log("Launcher v4 starting; covering native loading window.");
            skinProcess=new Process {StartInfo=info};
            skinProcess.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e){
                if(e.Data==null)return;
                Log(e.Data);
                if(e.Data=="REZE_STARTED_VERIFIED_SESSION" || e.Data=="REZE_REUSED_VERIFIED_SESSION")sessionVerified=true;
                if(e.Data.StartsWith("REZE_WINDOW_PID=",StringComparison.Ordinal)) {
                    int processId;
                    if(Int32.TryParse(e.Data.Substring("REZE_WINDOW_PID=".Length),out processId) && processId>0)Interlocked.Exchange(ref expectedWindowPid,processId);
                }
            };
            skinProcess.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)Log(e.Data);};
            skinProcess.Start();skinProcess.BeginOutputReadLine();skinProcess.BeginErrorReadLine();
            return true;
        } catch(Exception ex) { MessageBox.Show(ex.Message,"蕾塞启动器",MessageBoxButtons.OK,MessageBoxIcon.Information);return false; }
    }
    bool FindAppWindow() {
        foreach(var process in Process.GetProcesses()) {
            using(process) {
                try {
                    if(process.ProcessName!="ChatGPT" && process.ProcessName!="Codex")continue;
                    if(expectedWindowPid>0 && process.Id!=expectedWindowPid)continue;
                    if(process.MainWindowHandle==IntPtr.Zero)continue;
                    string path=process.MainModule.FileName;
                    if(path.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0) { appWindow=process.MainWindowHandle;return true; }
                } catch { }
            }
        }
        return false;
    }
    void UpdateFrame() {
        double now=clock.Elapsed.TotalSeconds;
        if(now-lastCheck>.4) {
            lastCheck=now;
            if(test) ready=now>=(waitTest?15:9.3);
            else if(sessionVerified && Interlocked.CompareExchange(ref expectedWindowPid,0,0)>0) {
                ready=FindAppWindow();
            }
            else if(skinProcess!=null && skinProcess.HasExited && skinProcess.ExitCode!=0) {
                timer.Stop();Opacity=1;Log("Startup failed. ExitCode="+skinProcess.ExitCode);MessageBox.Show("Dream Skin 未完成启动或启动已取消。\n详细原因已保存到：\n"+logPath,"启动未完成");Close();return;
            }
        }
        if(!ready && handoffPrepared>=0) { Opacity=1;fadeStarted=-1;handoffPrepared=-1; }
        if((now>90 && !ready) || now>120) {timer.Stop();Opacity=1;Log("Timed out waiting for verified, aligned chat window.");MessageBox.Show("尚未确认聊天窗口已就绪并对齐。\n日志："+logPath,"等待超时");Close();return;}
        if(ready && now>=9.35) {
            if(handoffPrepared<0) {
                if(!test) {
                    TopMost=true;
                    // Fit the destination to the animation, so the visible
                    // animation never jumps to a different rectangle at handoff.
                    ShowWindow(appWindow,9);
                    if(!IsWindowVisible(appWindow) || IsIconic(appWindow))return;
                    if(!AlignChatToAnimation())return;
                    DwmFlush();
                    Log("Chat window aligned behind stationary animation: "+Bounds);
                }
                handoffPrepared=clock.Elapsed.TotalSeconds;return;
            }
            // Check every animation tick, including during the fade. Any move,
            // resize or DPI change restarts the stable alignment interval.
            if(!test && !MaintainAlignment(now))return;
            if(now-handoffPrepared<.2)return;
            if(fadeStarted<0)fadeStarted=now;
            Opacity=Math.Max(.01,1-Ease(fadeStarted,fadeStarted+.65,now));
            if(now-fadeStarted>=.65) {Finish();return;}
        }
        int next=Math.Min(209,Math.Max(0,(int)(now*21)));
        if(next!=frame) {
            using(var stream=new MemoryStream(frames[next])) using(var image=Image.FromStream(stream)) {
                Bitmap previous=current;current=new Bitmap(image);if(previous!=null)previous.Dispose();
            }
            frame=next;
        }
        if(test && waitTest && now>=12 && !waitVerified) {
            if(frame!=frames.Count-1 || Opacity!=1)throw new InvalidOperationException("等待阶段未保持完整亮度。");
            using(var shot=new Bitmap(ClientSize.Width,ClientSize.Height)) {
                DrawToBitmap(shot,ClientRectangle);
                shot.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"waiting-preview.png"));
            }
            waitVerified=true;
        }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        if(current!=null) {
            float imageScale=Math.Max((float)ClientSize.Width/current.Width,(float)ClientSize.Height/current.Height);
            float w=current.Width*imageScale, h=current.Height*imageScale;
            e.Graphics.DrawImage(current,(ClientSize.Width-w)/2,(ClientSize.Height-h)/2,w,h);
            DrawMotion(e.Graphics,clock.Elapsed.TotalSeconds*.7,new RectangleF((ClientSize.Width-w)/2,(ClientSize.Height-h)/2,w,h));
        }
        e.Graphics.ScaleTransform(ClientSize.Width/960f,ClientSize.Height/540f);
        double t=clock.Elapsed.TotalSeconds;
        DrawWelcome(e.Graphics,t,false);
    }
    static Bitmap MakePatch(Bitmap pose,Rectangle region,int feather) {
        Bitmap patch=pose.Clone(region,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for(int y=0;y<patch.Height;y++)for(int x=0;x<patch.Width;x++) {
            float edge=Math.Min(Math.Min(x,patch.Width-1-x),Math.Min(y,patch.Height-1-y));
            float a=Math.Min(1,edge/feather);a=a*a*(3-2*a);
            Color color=patch.GetPixel(x,y);
            patch.SetPixel(x,y,Color.FromArgb((int)(255*a),color.R,color.G,color.B));
        }
        return patch;
    }
    static float Pulse(double t,double start,double hold,double end) {
        if(t<start || t>end)return 0;
        double peak=start+(end-start-hold)/2;
        return Ease(start,peak,t)*(1-Ease(peak+hold,end,t));
    }
    void DrawMotion(Graphics g,double t,RectangleF imageRect) {
        float blink=Pulse(t,4.85,.25,5.95);
        float pull=Pulse(t,4.85,.25,5.95);
        DrawPatch(g,blinkPatch,new Rectangle(978,62,100,68),imageRect,blink);
        DrawPatch(g,pullPatch,new Rectangle(708,143,285,174),imageRect,pull);
    }
    static void DrawPatch(Graphics g,Bitmap patch,Rectangle region,RectangleF imageRect,float opacity) {
        if(opacity<=0)return;
        float sx=imageRect.Width/1280,sy=imageRect.Height/720;
        Rectangle dest=Rectangle.Round(new RectangleF(imageRect.X+region.X*sx,imageRect.Y+region.Y*sy,region.Width*sx,region.Height*sy));
        using(var attributes=new System.Drawing.Imaging.ImageAttributes()) {
            var matrix=new System.Drawing.Imaging.ColorMatrix();matrix.Matrix33=opacity;
            attributes.SetColorMatrix(matrix);
            g.DrawImage(patch,dest,0,0,patch.Width,patch.Height,GraphicsUnit.Pixel,attributes);
        }
    }
    static float Ease(double start,double end,double t) {
        float v=(float)Math.Max(0,Math.Min(1,(t-start)/(end-start)));
        return v*v*(3-2*v);
    }
    static void DrawWelcome(Graphics g,double t,bool skip) {
        float outline=skip?1:Ease(6.0,7.1,t);
        float fill=skip?1:Ease(7.0,8.2,t);
        float greeting=skip?1:Ease(8.0,8.9,t);
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(outline<=0)return;
        using(var path=new System.Drawing.Drawing2D.GraphicsPath())
        using(var family=new FontFamily("Arial")) {
            path.AddString("Chatgpt",family,(int)FontStyle.Regular,92,new PointF(48,176),StringFormat.GenericTypographic);
            RectangleF bounds=path.GetBounds();
            using(var glow=new Pen(Color.FromArgb((int)(22*outline),125,219,235),4))
            using(var edge=new Pen(Color.FromArgb((int)(145*outline),144,217,228),1)) {
                g.DrawPath(glow,path);g.DrawPath(edge,path);
            }
            if(fill>0) {
                using(var gradient=new System.Drawing.Drawing2D.LinearGradientBrush(bounds,Color.FromArgb((int)(255*fill),209,255,249),Color.FromArgb((int)(255*fill),89,175,205),0f))g.FillPath(gradient,path);
            }
        }
        if(greeting>0) {
            using(var line=new Pen(Color.FromArgb((int)(120*greeting),154,224,228),1)) {
                g.DrawLine(line,54,291,410,291);
                line.Width=2;g.DrawLine(line,54,291,80,291);
            }
            using(var chinese=new Font("Microsoft YaHei UI",15,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var english=new Font("Arial",7,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var brush=new SolidBrush(Color.FromArgb((int)(255*greeting),191,235,239))) {
                g.DrawString("电次那天的赴约我去了",chinese,brush,54,303);
                brush.Color=Color.FromArgb((int)(155*greeting),193,218,224);
                g.DrawString("SEAMLESS STARTUP",english,brush,56,332);
            }
        }
    }
    void SavePreview(string name,double time) {
        using(var bitmap=new Bitmap(960,540))
        using(var g=Graphics.FromImage(bitmap))
        using(var stream=new MemoryStream(frames[Math.Min(209,Math.Max(0,(int)(time*21)))]))
        using(var background=Image.FromStream(stream)) {
            g.DrawImage(background,0,0,960,540);
            DrawMotion(g,time*.7,new RectangleF(0,0,960,540));
            DrawWelcome(g,time,false);
            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name),System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    void Finish() {
        if(finished)return;finished=true;timer.Stop();
        // Remove the fully faded overlay before changing activation/z-order.
        // Lowering a still-visible overlay first can expose a desktop flash.
        Hide();
        if(!test && appWindow!=IntPtr.Zero) { SetForegroundWindow(appWindow);DwmFlush();Log("Animation finished; aligned chat window revealed."); }
        if(test)File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"transition-test.txt"),"Bounds="+Bounds+"; workingArea="+Screen.FromControl(this).WorkingArea+"; Transition completed; frame="+frame+"; opacity="+Opacity.ToString("F2")+"; elapsed="+clock.Elapsed.TotalSeconds.ToString("F2")+"; waitBrightnessVerified="+waitVerified);
        Close();
    }
    protected override void Dispose(bool disposing) { if(disposing){timer.Dispose();if(blinkPatch!=null)blinkPatch.Dispose();if(pullPatch!=null)pullPatch.Dispose();if(current!=null)current.Dispose();if(skinProcess!=null)skinProcess.Dispose();if(helperPath!=null){try{File.Delete(helperPath);}catch{}}}base.Dispose(disposing); }
    [STAThread] static int Main(string[] args) {
        // All window measurements and positioning use physical pixels, including
        // when the chat app and launcher are on monitors with different scales.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        bool owner;
        using(var mutex=new Mutex(true,"Local\\RezeTransitionAnimatedLauncher",out owner)) {
            if(!owner)return 0;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try { using(var window=new RezeTransitionLauncher(args.Length>0)) { window.waitTest=args.Length>0 && args[0]=="--test-wait"; if(args.Length>0 && args[0]=="--render-clip") { for(int i=0;i<210;i++)window.SavePreview("motion-"+i.ToString("D3")+".png",i/21.0); } else if(args.Length>0 && args[0]=="--render-preview") { window.SavePreview("welcome-outline.png",7.0);window.SavePreview("welcome-final.png",9.2); window.SavePreview("motion-blink.png",7.8);window.SavePreview("motion-pull.png",7.7); } else Application.Run(window); } return 0; }
            catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"animation-error.txt"),ex.ToString());if(args.Length==0)MessageBox.Show(ex.Message,"启动器错误");return 1;}
        }
    }
}

