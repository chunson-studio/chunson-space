using System;
using System.Windows.Interop;

public static class ClockChecks {
 static int count;
 static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);count++;}
 public static int Run(){
  count=0;
  var screen=new Native.Rect{Left=0,Top=0,Right=1920,Bottom=1080};
  Check(ClockVisibility.Fullscreen(screen,screen,"Browser",true,false,false,false),"Borderless fullscreen hides clock");
  var maximized=new Native.Rect{Left=0,Top=0,Right=1920,Bottom=1032};
  Check(!ClockVisibility.Fullscreen(maximized,screen,"Browser",true,false,false,false),"Work-area window keeps clock");
  Check(!ClockVisibility.Fullscreen(screen,screen,"Browser",true,false,false,true),"Maximized captioned window is not fullscreen");
  foreach(string cls in new[]{"Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd"})
   Check(!ClockVisibility.Fullscreen(screen,screen,cls,true,false,false,false),"Shell window: "+cls);
  Check(!ClockVisibility.Fullscreen(screen,screen,"Browser",false,false,false,false),"Invisible foreground ignored");
  Check(!ClockVisibility.Fullscreen(screen,screen,"Browser",true,true,false,false),"Minimized foreground ignored");
  Check(!ClockVisibility.Fullscreen(screen,screen,"Browser",true,false,true,false),"Other virtual desktop ignored");
  var otherMonitor=new Native.Rect{Left=-1920,Top=0,Right=0,Bottom=1080};
  Check(!ClockVisibility.Fullscreen(otherMonitor,screen,"Browser",true,false,false,false),"Other monitor fullscreen ignored");
  Check(ClockVisibility.Fullscreen(otherMonitor,otherMonitor,"Browser",true,false,false,false),"Negative-coordinate monitor fullscreen");
  Check(!ClockVisibility.Fullscreen(new Native.Rect(),screen,"Browser",true,false,false,false),"Empty frame ignored");
  // Only disposable test HWNDs are changed. They are off every display and cannot take focus.
  Native.Dpi();
  IntPtr foreground=Native.GetForegroundWindow();
  var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
  var expected=new Native.Rect{Left=desktop.Right+100,Top=desktop.Bottom+100,Right=desktop.Right+180,Bottom=desktop.Bottom+140};
  var options=new HwndSourceParameters("Clock isolated visibility checks");
  options.PositionX=expected.Left;options.PositionY=expected.Top;options.Width=expected.Width;options.Height=expected.Height;
  options.WindowStyle=unchecked((int)0x80000000);options.ExtendedWindowStyle=0x08000080;
  using(var test=new HwndSource(options)){
   IntPtr h=test.Handle;
   Check(!Native.IsWindowVisible(h),"Test window starts hidden");
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,false)&&Native.IsWindowVisible(h),"Hidden window restored at unchanged coordinates");
   Native.ShowWindow(h,0);
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,false)&&Native.IsWindowVisible(h),"Repeated hidden recovery");
   Native.SetWindowPos(h,new IntPtr(-2),0,0,0,0,0x0010|0x0200|0x0001|0x0002);
   Check((Native.GetWindowLongPtrW(h,-20).ToInt64()&8L)==0,"Topmost flag removed on fixture");
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,false)&&(Native.GetWindowLongPtrW(h,-20).ToInt64()&8L)!=0,"Lost topmost restored");
   Native.SetWindowPos(h,IntPtr.Zero,expected.Left+10,expected.Top+10,expected.Width,expected.Height,0x0010|0x0200|0x0004);
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,false),"Moved window restored");
   Native.Rect actual;Native.GetWindowRect(h,out actual);
   Check(actual.Left==expected.Left&&actual.Top==expected.Top&&actual.Right==expected.Right&&actual.Bottom==expected.Bottom,"Actual position matches");
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,true),"Fullscreen return reasserts visibility");
   Check(ClockVisibility.EnsureShown(h,IntPtr.Zero,expected,false),"Stable window remains unchanged");
   Check(Native.GetForegroundWindow()==foreground,"Recovery preserves foreground window");
  }
  return count;
 }
}
