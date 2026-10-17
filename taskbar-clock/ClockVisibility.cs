using System;

// Geometry is in physical pixels. Visibility recovery never activates another window.
public static class ClockVisibility {
 public static bool Fullscreen(Native.Rect frame,Native.Rect screen,string windowClass,bool visible,bool minimized,bool cloaked,bool maximizedWithCaption){
  if(!visible||minimized||cloaked||maximizedWithCaption||windowClass=="Progman"||windowClass=="WorkerW"||windowClass=="Shell_TrayWnd"||windowClass=="Shell_SecondaryTrayWnd")return false;
  return frame.Width>0&&frame.Height>0&&frame.Left<=screen.Left&&frame.Top<=screen.Top&&frame.Right>=screen.Right&&frame.Bottom>=screen.Bottom;
 }
 public static bool ForegroundFullscreen(IntPtr foreground,IntPtr clock,IntPtr taskbar,Native.Rect screen){
  if(foreground==IntPtr.Zero||foreground==clock||foreground==taskbar)return false;
  Native.Rect frame;
  // GetWindowRect includes invisible resize borders and can misclassify maximized windows.
  if(!Native.VisibleFrame(foreground,out frame))return false;
  bool caption=(Native.GetWindowLongPtrW(foreground,-16).ToInt64()&0x00C00000L)!=0;
  return Fullscreen(frame,screen,Native.ClassName(foreground),Native.IsWindowVisible(foreground),Native.IsIconic(foreground),Native.Cloaked(foreground),Native.IsZoomed(foreground)&&caption);
 }
 public static bool BehindTaskbar(IntPtr clock,IntPtr taskbar){
  if(taskbar==IntPtr.Zero)return false;
  IntPtr previous=clock;
  for(int i=0;i<512;i++){
   previous=Native.GetWindow(previous,3); // GW_HWNDPREV: next higher window in Z order.
   if(previous==IntPtr.Zero)return false;
   if(previous==taskbar)return true;
  }
  return false;
 }
 public static bool EnsureShown(IntPtr clock,IntPtr taskbar,Native.Rect desired,bool returningFromHidden){
  Native.Rect actual;
  bool same=Native.GetWindowRect(clock,out actual)&&actual.Left==desired.Left&&actual.Top==desired.Top&&actual.Right==desired.Right&&actual.Bottom==desired.Bottom;
  bool minimized=Native.IsIconic(clock);
  bool topmost=(Native.GetWindowLongPtrW(clock,-20).ToInt64()&8L)!=0;
  if(same&&Native.IsWindowVisible(clock)&&!minimized&&topmost&&!returningFromHidden&&!BehindTaskbar(clock,taskbar))return true;
  if(minimized)Native.ShowWindow(clock,4); // SW_SHOWNOACTIVATE restores without focus.
  return Native.SetWindowPos(clock,new IntPtr(-1),desired.Left,desired.Top,desired.Width,desired.Height,0x0010|0x0040|0x0200);
 }
}
