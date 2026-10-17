using System;
using System.IO;
using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

public class ClockWindow : Window {
 const string WindowTitle="Chun Beijing Taskbar Clock";
 readonly TextBlock time = new TextBlock();
 readonly TextBlock date = new TextBlock();
 readonly TextBlock label = new TextBlock();
 readonly Border panel = new Border();
 readonly Border zoneBadge = new Border();
 readonly TextBlock zoneLabel = new TextBlock();
 readonly Border divider = new Border();
 readonly Border dateDivider = new Border();
 readonly Border skyIcon = new Border();
 readonly DispatcherTimer timer = new DispatcherTimer();
 readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChunsonWidgets","position.txt");
 IntPtr hwnd, taskbar;
 double offset=0;
 int anchor;
 bool moving;
 string lastText="";
 DateTime nextTheme=DateTime.MinValue;
 bool lightTheme=true;
 int lastHour=-1;
 public static DateTimeOffset Beijing(DateTimeOffset utc){return utc.ToOffset(TimeSpan.FromHours(8));}
 public ClockWindow(){
  Title=WindowTitle;Width=220;Height=38;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
  AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
  FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
  string savedPath=File.Exists(settings)?settings:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"position.txt");
  double saved;try{if(File.Exists(savedPath)&&double.TryParse(File.ReadAllText(savedPath),NumberStyles.Float,CultureInfo.InvariantCulture,out saved))offset=saved;}catch(IOException){}catch(UnauthorizedAccessException){}
  panel.CornerRadius=new CornerRadius(10);panel.Padding=new Thickness(8,2,10,2);panel.BorderThickness=new Thickness(1);
  var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(4)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(29)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(57)});
  divider.Width=2;divider.Height=16;divider.CornerRadius=new CornerRadius(1);divider.VerticalAlignment=VerticalAlignment.Center;divider.Visibility=Visibility.Hidden;grid.Children.Add(divider);
  skyIcon.Width=25;skyIcon.Height=25;skyIcon.CornerRadius=new CornerRadius(7);skyIcon.VerticalAlignment=VerticalAlignment.Center;skyIcon.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(skyIcon,1);grid.Children.Add(skyIcon);
  var stack=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,0,0)};Grid.SetColumn(stack,2);
  var line=new StackPanel{Orientation=Orientation.Horizontal};
  label.Text="北京时间";label.FontSize=9.5;label.FontWeight=FontWeights.Medium;label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(0,0,6,0);
  zoneLabel.Text="UTC+8";zoneLabel.FontFamily=new FontFamily("Segoe UI");zoneLabel.FontSize=8;zoneLabel.FontWeight=FontWeights.SemiBold;
  zoneBadge.CornerRadius=new CornerRadius(4);zoneBadge.Padding=new Thickness(4,0,4,0);zoneBadge.VerticalAlignment=VerticalAlignment.Center;zoneBadge.Child=zoneLabel;
  line.Children.Add(label);line.Children.Add(zoneBadge);stack.Children.Add(line);
  time.FontFamily=new FontFamily("Segoe UI");time.FontSize=16;time.FontWeight=FontWeights.SemiBold;time.Margin=new Thickness(0,-3,0,-2);Typography.SetNumeralAlignment(time,FontNumeralAlignment.Tabular);stack.Children.Add(time);grid.Children.Add(stack);
  dateDivider.Width=1;dateDivider.Height=22;dateDivider.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(dateDivider,3);grid.Children.Add(dateDivider);
  date.FontSize=10;date.FontWeight=FontWeights.Medium;date.TextAlignment=TextAlignment.Right;date.VerticalAlignment=VerticalAlignment.Center;date.LineHeight=14;Grid.SetColumn(date,4);grid.Children.Add(date);
  panel.Child=grid;Content=panel;ToolTip="北京时间 · UTC+8\n左键拖动调整位置，右键打开菜单";
  skyIcon.RenderTransformOrigin=new Point(.5,.9);var sway=new RotateTransform();skyIcon.RenderTransform=sway;
  MouseEnter+=delegate{divider.Visibility=Visibility.Visible;UpdateCardBrushes();sway.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,-5,TimeSpan.FromMilliseconds(160)){AutoReverse=true,RepeatBehavior=new RepeatBehavior(2)});};
  PreviewMouseLeftButtonDown+=delegate{divider.Visibility=Visibility.Visible;UpdateCardBrushes();};
  MouseLeave+=delegate{divider.Visibility=Visibility.Hidden;UpdateCardBrushes();};
  var menu=new ContextMenu();
  var reset=new MenuItem{Header="恢复默认位置"};reset.Click+=delegate{offset=0;Save();Tick();};menu.Items.Add(reset);
  var quit=new MenuItem{Header="退出北京时间"};quit.Click+=delegate{Close();};menu.Items.Add(quit);ContextMenu=menu;
  MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){
   if(e.ButtonState!=MouseButtonState.Pressed)return;
   moving=true;try{DragMove();Native.Rect r;Native.GetWindowRect(hwnd,out r);offset=(r.Left-anchor)/Scale();Save();}finally{moving=false;Tick();}
  };
  SourceInitialized+=delegate{
   hwnd=new WindowInteropHelper(this).Handle;
   long ex=Native.GetWindowLongPtrW(hwnd,-20).ToInt64();Native.SetWindowLongPtrW(hwnd,-20,new IntPtr(ex|0x08000080L));
   new WindowInteropHelper(this).EnsureHandle();
   HwndSource.FromHwnd(hwnd).AddHook(Hook);
   Tick();timer.Interval=TimeSpan.FromMilliseconds(250);timer.Tick+=delegate{Tick();};timer.Start();
  };
  Closed+=delegate{timer.Stop();};
 }
 IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;}
 double Scale(){return taskbar==IntPtr.Zero?1:Math.Max(1,Native.GetDpiForWindow(taskbar)/96.0);}
 void Save(){Directory.CreateDirectory(Path.GetDirectoryName(settings));File.WriteAllText(settings,offset.ToString(CultureInfo.InvariantCulture));}
 void UpdateSky(int hour){
  if(hour==lastHour)return;lastHour=hour;
  var face=new Canvas{Width=30,Height=30};
  skyIcon.Background=Brushes.Transparent;
  var ground=new System.Windows.Shapes.Ellipse{Width=20,Height=4,Fill=new SolidColorBrush(Color.FromArgb(35,78,108,47))};Canvas.SetLeft(ground,5);Canvas.SetTop(ground,25);face.Children.Add(ground);
  var outline=new SolidColorBrush(Color.FromRgb(73,101,47));
  face.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M15,26 C16,20 14,13 16,6"),Stroke=outline,StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
  face.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M15,16 C6,15 3,8 4,4 C11,4 16,10 15,16 Z"),Fill=new SolidColorBrush(Color.FromRgb(120,169,80)),Stroke=outline,StrokeThickness=.7});
  face.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M16,17 C17,9 24,7 28,8 C25,15 22,18 16,17 Z"),Fill=new SolidColorBrush(Color.FromRgb(157,186,112)),Stroke=outline,StrokeThickness=.7});
  face.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M15,22 C9,22 8,18 8,15 C12,15 15,18 15,22 Z"),Fill=new SolidColorBrush(Color.FromRgb(174,195,137)),Stroke=outline,StrokeThickness=.6});
  skyIcon.Child=new Viewbox{Stretch=Stretch.Uniform,Child=face};
  string greeting=hour<6?"夜深了，早点休息呀":hour<11?"早上好，今天也慢慢来":hour<14?"中午好，记得好好吃饭":hour<18?"下午好，伸个懒腰吧":hour<22?"晚上好，给自己一点放松时间":"夜深了，早点休息呀";
  ToolTip=greeting+"\n北京时间 · UTC+8\n左键拖动调整位置，右键打开菜单";
 }
 void UpdateCardBrushes(){
  if(divider.Visibility!=Visibility.Visible){panel.Background=Brushes.Transparent;panel.BorderBrush=Brushes.Transparent;return;}
  panel.Background=new SolidColorBrush(lightTheme?Color.FromArgb(125,243,241,232):Color.FromArgb(125,25,41,31));
  panel.BorderBrush=new SolidColorBrush(lightTheme?Color.FromArgb(70,145,165,117):Color.FromArgb(55,173,195,147));
 }
 void Tick(){
  if(moving||hwnd==IntPtr.Zero)return;
  var found=Native.FindWindow("Shell_TrayWnd",null);
  taskbar=found;
  if(Native.GetWindowLongPtrW(hwnd,-8)!=taskbar)Native.SetWindowLongPtrW(hwnd,-8,taskbar);
  Native.Rect r;if(taskbar==IntPtr.Zero||!Native.GetWindowRect(taskbar,out r)||!Native.IsWindowVisible(taskbar)||r.Width<r.Height){Opacity=0;return;}
  var screen=System.Windows.Forms.Screen.FromHandle(taskbar).Bounds;
  int visible=Math.Min(screen.Bottom,r.Bottom)-Math.Max(screen.Top,r.Top);
  if(visible<r.Height/2){Opacity=0;return;}
  var screenRect=new Native.Rect{Left=screen.Left,Top=screen.Top,Right=screen.Right,Bottom=screen.Bottom};
  if(ClockVisibility.ForegroundFullscreen(Native.GetForegroundWindow(),hwnd,taskbar,screenRect)){Opacity=0;return;}
  double scale=Scale();int width=(int)Math.Round(Width*scale),height=Math.Min(r.Height,(int)Math.Round(Height*scale));
  int trayLeft=r.Right-(int)(300*scale);
  Native.Callback cb=delegate(IntPtr child,IntPtr p){if(Native.ClassName(child)=="TrayNotifyWnd"){Native.Rect tr;if(Native.GetWindowRect(child,out tr)&&tr.Width>0)trayLeft=tr.Left;}return true;};
  Native.EnumChildWindows(taskbar,cb,IntPtr.Zero);
  anchor=trayLeft-width-(int)(16*scale);
  int x=Math.Max(r.Left,Math.Min(trayLeft-width,anchor+(int)(offset*scale)));
  int y=r.Top+(r.Height-height)/2;
  bool returningFromHidden=Opacity==0;
  ClockVisibility.EnsureShown(hwnd,taskbar,new Native.Rect{Left=x,Top=y,Right=x+width,Bottom=y+height},returningFromHidden);
  Opacity=1;
  if(returningFromHidden)InvalidateVisual();
  var now=Beijing(DateTimeOffset.UtcNow);string value=now.ToString("HH:mm:ss",CultureInfo.InvariantCulture);
  UpdateSky(now.Hour);
  if(lastText!=value){time.Text=value;date.Text=now.ToString("MM月dd日\ndddd",CultureInfo.GetCultureInfo("zh-CN"));lastText=value;}
  if(DateTime.UtcNow>=nextTheme){
   bool light=true;using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")){if(key!=null)light=Convert.ToInt32(key.GetValue("SystemUsesLightTheme",1))!=0;}
   lightTheme=light;
   time.Foreground=new SolidColorBrush(light?Color.FromRgb(32,51,36):Color.FromRgb(239,243,228));
   date.Foreground=new SolidColorBrush(light?Color.FromRgb(62,77,53):Color.FromRgb(214,226,194));
   label.Foreground=date.Foreground;
   divider.Background=new SolidColorBrush(light?Color.FromArgb(52,70,75,80):Color.FromArgb(62,220,225,230));
   dateDivider.Background=new SolidColorBrush(light?Color.FromArgb(36,60,70,83):Color.FromArgb(45,220,225,230));
   zoneBadge.Background=new SolidColorBrush(light?Color.FromArgb(185,220,232,201):Color.FromArgb(150,65,86,43));
   zoneLabel.Foreground=new SolidColorBrush(light?Color.FromRgb(67,93,52):Color.FromRgb(207,232,170));
   UpdateCardBrushes();
   nextTheme=DateTime.UtcNow.AddSeconds(5);
  }
 }
 [STAThread] public static int Main(string[] args){
  if(args.Length>0&&args[0]=="--check"){
   var before=TimeZoneInfo.Local.Id;
   if(Beijing(new DateTimeOffset(2026,10,13,23,59,59,TimeSpan.Zero)).ToString("yyyy-MM-dd HH:mm:ss")!="2026-10-14 07:59:59")return 1;
   if(Beijing(new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)).Offset!=TimeSpan.FromHours(8))return 2;
   if(before!=TimeZoneInfo.Local.Id)return 3;
   string checkName="Local\\Chunson.BeijingClock.Check."+Guid.NewGuid().ToString("N");bool first,second;
   using(var one=new System.Threading.Mutex(true,checkName,out first)){
    using(var two=new System.Threading.Mutex(true,checkName,out second)){if(!first||second)return 4;}
    one.ReleaseMutex();
   }
   int checks=ClockChecks.Run();
   string evidence=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"evidence");Directory.CreateDirectory(evidence);
   File.WriteAllText(Path.Combine(evidence,"checks.txt"),"PASS: UTC+8 conversion, date rollover, fixed offset, shared-singleton ownership; local timezone unchanged. "+checks+" visibility checks passed (isolated off-screen windows; no real taskbar/fullscreen interaction).");return 0;
  }
  // Older standalone clocks have no shared mutex. Reuse their existing window without stopping it.
  if(Native.FindWindow(null,WindowTitle)!=IntPtr.Zero)return 0;
  bool created;
  using(var singleton=new System.Threading.Mutex(true,"Local\\Chunson.BeijingClock",out created)){
   if(!created)return 0;
   try{Native.Dpi();var app=new Application();app.Run(new ClockWindow());return 0;}
   finally{singleton.ReleaseMutex();}
  }
 }
}
