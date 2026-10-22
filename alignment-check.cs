using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
class AlignmentCheck {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int command);
    [STAThread] static int Main() {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        try {
            var assembly=Assembly.Load(File.ReadAllBytes("CodexSeamlessLauncher.exe"));
            var type=assembly.GetType("RezeTransitionLauncher");
            using(var overlay=(Form)Activator.CreateInstance(type,new object[]{true}))
            using(var destination=new Form()) {
                overlay.Bounds=new Rectangle(320,180,1200,740);
                destination.StartPosition=FormStartPosition.Manual;
                destination.Bounds=new Rectangle(100,100,800,500);
                destination.Show();
                ShowWindow(destination.Handle,5);
                var handle=overlay.Handle;
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                type.GetField("appWindow",flags).SetValue(overlay,destination.Handle);
                if(!(bool)type.GetMethod("AlignChatToAnimation",flags).Invoke(overlay,null))throw new Exception("Position failed");
                Application.DoEvents();
                System.Threading.Thread.Sleep(250);
                if(!(bool)type.GetMethod("ChatIsAligned",flags).Invoke(overlay,null))throw new Exception("Visible frame mismatch");
                for(int i=0;i<12;i++) {
                    overlay.Opacity=.4;
                    type.GetField("fadeStarted",flags).SetValue(overlay,1.0);
                    destination.Bounds=new Rectangle(150+i*11,120+i*7,900+i*13,620+i*5);
                    Application.DoEvents();
                    if((bool)type.GetMethod("MaintainAlignment",flags).Invoke(overlay,new object[]{2.0+i}))throw new Exception("Drift was not detected");
                    if(overlay.Opacity!=1 || (double)type.GetField("fadeStarted",flags).GetValue(overlay)!=-1)throw new Exception("Fade did not reset");
                    if(!(bool)type.GetMethod("ChatIsAligned",flags).Invoke(overlay,null))throw new Exception("Realignment failed");
                }
                destination.Hide();
                if((bool)type.GetMethod("MaintainAlignment",flags).Invoke(overlay,new object[]{20.0}))throw new Exception("Hidden window accepted");
                File.WriteAllText("alignment-check.txt","PASS: initial alignment, 12 move/resize corrections during fade, opacity reset, hidden-window rejection; DPI="+GetDpiForWindow(destination.Handle));
            }
            return 0;
        } catch(Exception ex) { File.WriteAllText("alignment-check.txt",ex.ToString());return 1; }
    }
}
