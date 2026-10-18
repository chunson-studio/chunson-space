using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class ResultsGalleryVerification
{
    private const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 600'><rect width='960' height='600' fill='#f9f4e9'/><path d='M0 505 Q260 440 480 490 T960 480 V600 H0' fill='#d4dfcb'/><g fill='none' stroke='#415e64' stroke-width='7'><circle cx='280' cy='405' r='100'/><circle cx='700' cy='405' r='100'/><path d='M280 405 L395 245 500 405 Z M395 245 H635 L500 405 M635 245 L700 405 M610 210 L635 245 M610 210 H675 M375 240 H425'/></g><g stroke='#95a59d' stroke-width='1'>" +
        "<path d='M180 405 H380 M280 305 V505 M210 335 L350 475 M210 475 L350 335 M600 405 H800 M700 305 V505 M630 335 L770 475 M630 475 L770 335'/></g><path d='M390 172 Q400 65 508 60 Q560 50 575 100 L690 138 564 145 Q550 192 490 220 L445 300 390 285 432 200 Z' fill='#fffdf7' stroke='#63736b' stroke-width='4'/><path d='M570 100 L690 138 565 145 Z' fill='#dbaf64'/><circle cx='550' cy='95' r='6' fill='#334d51'/><path d='M450 210 L440 320 500 405 M490 220 L555 310 525 390' fill='none' stroke='#b38648' stroke-width='10'/><path d='M458 130 Q430 110 410 172 L475 180 Z' fill='#dce4d9'/><text x='45' y='55' font-family='serif' font-size='24' fill='#415e64'>鹈鹕骑车 · 本机示例图</text><text x='45' y='560' font-size='14' fill='#415e64'>960 × 600 · 放大看细节 · 没有请求模型</text></svg>";
    private const string Html = "<html><head><style>body{margin:0;background:#f9f4e9;font:20px sans-serif}button{position:absolute;top:24px;width:140px;height:50px}#pause{left:24px}#speed{left:180px}#dot{position:absolute;top:240px;width:140px;height:140px;background:#77978c;border-radius:50%}</style></head><body><button id='pause' onclick='running=!running;this.textContent=running?\"暂停\":\"继续\"'>暂停</button><button id='speed' onclick='speed=3;this.textContent=\"3倍\"'>加快</button><div id='dot'></div><script>let running=true,speed=1,x=20;setInterval(()=>{if(running){x=(x+12*speed)%760;document.getElementById('dot').style.left=x+'px'}},80);</script></body></html>";
    internal static async Task RunStandalone(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var renders = new List<string>(); var passed = false;
        var listener = new UiVerification.BindingListener(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        try
        {
            await FakeStorage(directory, Check);
            await SecurityEngine(directory,Check);
            foreach (var dark in new[] { false, true }) foreach (var size in new[] { (1040,670), (1280,850) })
            {
                var label = (dark ? "dark" : "light") + $"-{size.Item1}x{size.Item2}";
                var fixture = Path.Combine(directory,"fixtures",label); var accounts = Path.Combine(fixture,"accounts"); Directory.CreateDirectory(accounts);
                File.WriteAllText(Path.Combine(accounts,"ChunsonSpace-fixture.json"),"{\"fixture\":true}");
                var workspace = new DesktopWorkspace(accounts,Path.Combine(fixture,"home"),Path.Combine(fixture,"desktop"),fixture:true);
                var services = workspace.Accounts!;
                foreach(var name in new[] { "画室 A", "画室 B" }) { services.Repository.Profiles.CreateBoundAccount(name,new("http://127.0.0.1:7901")); services.Repository.Profiles.SaveAccount(name,new JsonObject { ["tokens"]=new JsonObject { ["account_id"]="fixture-"+name,["access_token"]="fixture-access" } },overwrite:true); }
                var model = new ShellViewModel(workspace) { DarkTheme=dark }; var window = UiVerification.CreateWindow(model); var root=window.RenderRoot;
                void Layout() => UiVerification.ArrangeForCapture(root,size.Item1,size.Item2);
                void Capture(string name) { Layout(); var path=Path.Combine(directory,label+"-"+name+".png"); var bitmap=new RenderTargetBitmap(size.Item1,size.Item2,96,96,PixelFormats.Pbgra32); bitmap.Render(root); Save(bitmap,path); renders.Add(path); }
                void Assert(string name,bool ok) => Check(label+"."+name,ok);
                try
                {
                    Layout(); var nav=(Button)window.FindName("NavResults"); nav.Command.Execute(nav.CommandParameter); await model.ResultsGallery.CurrentLoad; Layout();
                    Assert("native navigation automatically reads empty history",model.IsResultsPage && model.ResultsGallery.IsEmpty && services.OfflineTransport!.RequestCount==0); Capture("empty");
                    var old=Seed(services,"画室 A",VisualTestKind.Static,"visual-v2-responses",0, "请生成一张鹈鹕骑自行车的 SVG。",false,false);
                    var revision=Seed(services,"画室 A",VisualTestKind.Revision,VisualTestCatalog.Version,2,VisualTestCatalog.Prompt(VisualTestKind.Revision,""),true,false);
                    var animation=Seed(services,"画室 B",VisualTestKind.Animation,VisualTestCatalog.Version,3,VisualTestCatalog.Prompt(VisualTestKind.Animation,""),true,false);
                    Seed(services,"画室 B",VisualTestKind.Variation,VisualTestCatalog.Version,1,VisualTestCatalog.Prompt(VisualTestKind.Variation,"octopus-organ"),false,true);
                    var ciallo=Path.Combine(services.Repository.AccountPath("画室 B"),"ciallo-tests"); Directory.CreateDirectory(ciallo);
                    File.WriteAllText(Path.Combine(ciallo,"legacy.json"),JsonSerializer.Serialize(new CialloHistoryEntry(DateTimeOffset.Parse("2021-01-01T00:00:00Z"),new([new("gpt-6-sol","saved-model","high","failed","Saved historical error: model unavailable.",TimeSpan.FromSeconds(1))],false),false)));
                    services.Repository.Profiles.SetTestsFrozen("画室 A",true);
                    var originals=Directory.EnumerateFiles(accounts,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
                    model.ResultsGallery.RefreshCommand.Execute(null); await model.ResultsGallery.CurrentLoad; Layout();
                    var rows=model.ResultsGallery.Items.Cast<SavedResultRow>().ToArray();
                    Assert("all histories stages partial cancellation and Ciallo auto visible",rows.Length==7 && rows.Any(r=>r.State=="已取消") && rows.Any(r=>r.State=="未完成") && rows.Count(r=>r.Visual?.Kind==VisualTestKind.Revision)==3);
                    Assert("newest first distinct prompt versions and preserved Chinese",rows[0].Visual?.Kind==VisualTestKind.Animation && rows.Any(r=>r.Version=="visual-v2-responses" && r.Prompt.StartsWith("请生成")));
                    Assert("English cache key does not load prior catalog as identical",services.VisualTests.Load("画室 A","gpt-6-astra",VisualTestKind.Static,"octopus-organ") is null);
                    var list=(ListBox)window.ResultsPage.FindName("AllResultsList"); Assert("real bound virtualized rows",list.Items.Count==7 && VirtualizingPanel.GetIsVirtualizing(list));
                    await Until(()=> { Layout(); var images=UiVerification.VisualDescendants(list).OfType<Image>().Where(i=>i.Source is BitmapSource && i.ActualWidth>0).ToArray(); return images.Length>0 && images.All(i=>Painted((BitmapSource)i.Source) && ThumbnailPainted(i)); });
                    Assert("visible thumbnails actually paint artwork",true); Capture("all-results");
                    model.ResultsGallery.Profile="画室 A"; Assert("account filter exact local scope",model.ResultsGallery.Items.Cast<SavedResultRow>().All(r=>r.Profile=="画室 A") && model.ResultsGallery.Items.Cast<object>().Count()==4);
                    model.ResultsGallery.Search="visual-v2"; Assert("version search retains legacy",model.ResultsGallery.Items.Cast<object>().Count()==1); Capture("legacy-filter");
                    model.ResultsGallery.ClearCommand.Execute(null); model.ResultsGallery.Order="最早优先"; Assert("oldest ordering",model.ResultsGallery.Items.Cast<SavedResultRow>().First().TaskType=="Ciallo"); model.ResultsGallery.Order="最新优先";
                    model.ResultsGallery.Type="鹈鹕动画"; Assert("task filter",model.ResultsGallery.Items.Cast<object>().Count()==1); model.ResultsGallery.ClearCommand.Execute(null);
                    model.ResultsGallery.State="已取消"; Assert("cancelled filter",model.ResultsGallery.Items.Cast<object>().Count()==1); model.ResultsGallery.ClearCommand.Execute(null);
                    var pair=rows.Where(r=>r.Visual?.Directory==revision.Directory && r.HasArtwork).ToArray(); pair[0].Selected=true; pair[1].Selected=true;
                    var compare=(Button)window.ResultsPage.FindName("CompareSavedResults"); Assert("real compare command enabled for two works",compare.Command.CanExecute(null)); compare.Command.Execute(null);
                    var viewer=model.ArtworkViewer!; await Task.WhenAll(viewer.Panes.Select(p=>p.Ready)).WaitAsync(TimeSpan.FromSeconds(35)); Layout();
                    Assert("simultaneous large live SVG rendering",viewer.Panes.Count==2 && viewer.Panes.All(p=>p.Image?.PixelWidth==1920 && p.FrameCount>0 && Painted(p.Image)));
                    var canvases=UiVerification.VisualDescendants(window.ArtworkOverlay).OfType<ArtworkCanvas>().ToArray();
                    Assert("large usable comparison panes",canvases.Length==2 && canvases.All(c=>c.ActualWidth>450 && c.ActualHeight>300));
                    await Task.Delay(400); Assert("static artwork not repeatedly rendered",viewer.Panes.All(p=>p.FrameCount==1));
                    Assert("both sides show Chinese safety and conclusion by default", UiVerification.VisualDescendants(window.ArtworkOverlay).OfType<TextBlock>().Count(t => t.Name == "PrimarySafetySummary" && t.ActualHeight > 0 && t.Visibility == Visibility.Visible && new[] { "账号正常", "账号异常", "本次没判断成功" }.Contains(t.Text)) == 2 && viewer.Panes.All(p => p.Row.ResultSummary == ""));
                    Assert("stage identities and nonidentical prompt warning",viewer.Panes[0].Row.Stage!=viewer.Panes[1].Row.Stage && viewer.Comparison.Contains("不同")); Capture("compare");
                    viewer.Zoom=3; Layout(); await Until(()=>viewer.Panes.All(p=>p.Image?.PixelWidth==2880)); Assert("synchronized zoom rerenders SVG details",canvases.All(c=>c.Zoom==3 && c.ArtworkWidth>1000)); Capture("compare-zoom");
                    foreach(var canvas in canvases) canvas.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); canvases[0].PanTo(.5,.5); Layout(); Layout(); Assert("simultaneous comparison synchronized pan",canvases.All(c=>c.PanPosition.X>0 && c.PanPosition.Y>0) && Math.Abs(canvases[0].PanPosition.X-canvases[1].PanPosition.X)<2);
                    viewer.FitCommand.Execute(null); Layout(); Assert("fit returns scale",viewer.Zoom==1); Assert("escape closes only artwork",window.HandleShortcut(Key.Escape,ModifierKeys.None) && !model.IsArtworkOpen && model.IsResultsPage); await Task.WhenAll(viewer.Panes.Select(p=>p.Operation));
                    var legacy=rows.Single(r=>r.Visual?.Directory==old.Directory); legacy.OpenCommand.Execute(null); var single=model.ArtworkViewer!; await single.Panes[0].Ready.WaitAsync(TimeSpan.FromSeconds(35)); Layout(); Assert("saved opens in app single large canvas",single.Panes.Count==1 && Painted(single.Panes[0].Image!) && UiVerification.VisualDescendants(window.ArtworkOverlay).OfType<ArtworkCanvas>().Single().ActualWidth>990); Capture("single-svg"); model.CloseArtwork(); await single.Panes[0].Operation;
                    model.OpenSavedWorks([legacy,pair[0]]); var mixed=model.ArtworkViewer!; await Task.WhenAll(mixed.Panes.Select(p=>p.Ready)); Assert("legacy English mismatch explicitly labeled",mixed.Comparison.Contains("版本") && mixed.Panes[0].Row.Version!=mixed.Panes[1].Row.Version); Capture("version-compare"); model.CloseArtwork(); await Task.WhenAll(mixed.Panes.Select(p=>p.Operation));
                    var anim=rows.Single(r=>r.Visual?.Directory==animation.Directory); anim.OpenCommand.Execute(null); var motion=model.ArtworkViewer!; var pane=motion.Panes[0]; await pane.Ready.WaitAsync(TimeSpan.FromSeconds(35)); await Until(()=>pane.FrameCount>=3); var hash=Pixels(pane.Image!); var count=pane.FrameCount; await Until(()=>pane.FrameCount>count+2); Assert("HTML animation actually changes frames",pane.IsInteractive && hash!=Pixels(pane.Image!));
                    await pane.ClickAsync(80,48); count=pane.FrameCount; await Until(()=>pane.FrameCount>count+2); hash=Pixels(pane.Image!); count=pane.FrameCount; await Until(()=>pane.FrameCount>count+2); Assert("HTML pause control actually works",hash==Pixels(pane.Image!)); Capture("animation-paused");
                    var speedBefore=SpeedLabel(pane.Image!); var pausedArtwork=MotionArea(pane.Image!);
                    await pane.ClickAsync(80,48); count=pane.FrameCount; await Until(()=>pane.FrameCount>count+2 && pausedArtwork!=MotionArea(pane.Image!)); Assert("HTML resume control actually changes frames",pane.IsInteractive);
                    await pane.ClickAsync(220,48); count=pane.FrameCount;
                    try { await Until(()=>pane.FrameCount>count+2 && speedBefore!=SpeedLabel(pane.Image!)); }
                    catch { Save(pane.Image!,Path.Combine(directory,label+"-speed-failure.png")); File.WriteAllText(Path.Combine(directory,label+"-speed-failure.json"),JsonSerializer.Serialize(new { pane.Status,pane.IsInteractive,pane.FrameCount,afterClickFrame=count,scale=pane.Image!.PixelWidth,speedBefore,speedAfter=SpeedLabel(pane.Image!) })); throw; }
                    Assert("HTML speed button changes its rendered state",pane.IsInteractive); Capture("animation-running"); model.CloseArtwork(); await pane.Operation;
                    var fallback=new ArtworkPaneViewModel(anim,()=>null); fallback.Start(); await fallback.Ready; Assert("missing engine explicit noninteractive saved image fallback",!fallback.IsInteractive && fallback.Status.Contains("不可交互") && fallback.Image?.PixelWidth==480); fallback.Stop(); await fallback.Operation;
                    rows.Single(r=>r.TaskType=="Ciallo").OpenCommand.Execute(null); var textViewer=model.ArtworkViewer!; await textViewer.Panes[0].Ready; Layout(); var fullText=UiVerification.VisualDescendants(window.ArtworkOverlay).OfType<TextBox>().Single(t=>t.Name=="FullSavedReply"); Assert("legacy text reply readable in full size without engine",fullText.Text=="Saved historical error: model unavailable." && fullText.ActualHeight>300 && textViewer.Panes[0].FrameCount==0); Capture("text-result"); model.CloseArtwork(); await textViewer.Panes[0].Operation;
                    Assert("old English reply is unchanged behind Chinese explanation", fullText.Text == "Saved historical error: model unavailable." && textViewer.Panes[0].Row.SafetySummary == "本次没判断成功" && !textViewer.Panes[0].Row.Excerpt.Contains("unavailable"));
                    foreach(var name in new[] { "../escape.svg", "C:\\outside.svg", "missing.svg" })
                    { try { ArtworkFiles.Scoped(revision,name); Assert("reject "+name,false); } catch(IOException) { Assert("reject "+name,true); } }
                    var corrupted=revision with { Steps=[revision.Steps[0] with { Artifact="invalid.svg" }] }; File.WriteAllText(Path.Combine(revision.Directory,"invalid.svg"),"bad markup");
                    var bad=SavedResultRow.FromVisual(corrupted).Single(); var badPane=new ArtworkPaneViewModel(bad,()=>throw new InvalidOperationException("must reject before engine")); badPane.Start(); await badPane.Ready; Assert("invalid artwork clear fallback",badPane.Status.Contains("格式无效") && !badPane.IsInteractive); badPane.Stop(); await badPane.Operation;
                    Assert("all original saved prompts results credentials unchanged",originals.All(p=>File.Exists(p.Key) && p.Value==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))));
                    File.WriteAllText(Path.Combine(ciallo,"corrupt.json"),"{bad json"); model.ResultsGallery.RefreshCommand.Execute(null); await model.ResultsGallery.CurrentLoad; Assert("corrupt record warning retains valid histories",model.ResultsGallery.Status.Contains("无法读取") && model.ResultsGallery.Items.Cast<object>().Count()==7);
                    Assert("frozen account history remains readable",services.Repository.Profiles.AreTestsFrozen("画室 A") && rows.Any(r=>r.Profile=="画室 A"));
                    model.OpenModelTestCommand.Execute(null); model.ModelTestProfile="画室 A"; model.ChooseVisualTestCommand.Execute("Revision"); Layout(); var retained=model.VisualTests!; retained.OpenGalleryCommand.Execute(null); var fromPanel=model.ArtworkViewer!; await Task.WhenAll(fromPanel.Panes.Select(p=>p.Ready)); Assert("existing test panel defaults to in app before after viewer",fromPanel.Panes.Count==2 && retained.IsSelectedFrozen && !retained.StartCommand.CanExecute(null)); model.CloseArtwork(); await Task.WhenAll(fromPanel.Panes.Select(p=>p.Operation)); Assert("back preserves test panel",ReferenceEquals(retained,model.VisualTests)); retained.CloseCommand.Execute(null);
                    Assert("selection filter compare engine performs zero account/model requests",services.OfflineTransport!.RequestCount==0 && services.VisualRuntimeFixture!.Requests.Count==0);
                    var cancelled = new CialloHistoryEntry(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), new([new("gpt-6-astra", "saved-model", "high", "completed", "unchanged English original", TimeSpan.FromSeconds(1)), new("gpt-6-sol", "", "high", "cancelled", "cancelled original", TimeSpan.Zero)], true, Version: CialloTest.Version), false);
                    File.WriteAllText(Path.Combine(ciallo, "cancelled-mixed.json"), JsonSerializer.Serialize(cancelled));
                    model.ResultsGallery.Enter(); await model.ResultsGallery.CurrentLoad;
                    var mixedRows = model.ResultsGallery.Items.Cast<SavedResultRow>().Where(r => r.Started == cancelled.Started).ToArray();
                    Assert("cancelled batch preserves successful reply status and new instruction version", mixedRows.Length == 2 && mixedRows.Single(r => r.Model == "gpt-6-astra").State == "已完成" && mixedRows.Single(r => r.Model == "gpt-6-sol").State == "已取消" && mixedRows.All(r => r.Version == CialloTest.Version));
                    Assert("English historical raw text never becomes primary conclusion", mixedRows.All(r => !r.Excerpt.Contains("original")) && File.ReadAllText(Path.Combine(ciallo,"cancelled-mixed.json")).Contains("unchanged English original"));
                    model.Navigate("Accounts"); Assert("back navigation intact",model.IsAccountsPage && !model.IsResultsPage);
                }
                finally { model.CloseArtwork(); window.Close(); await services.DisposeAsync(); }
            }
            Check("zero binding errors",listener.Errors.Count==0); passed=true;
        }
        finally { File.WriteAllText(Path.Combine(directory,"ui-verification.json"),JsonSerializer.Serialize(new { passed,checks,renders,bindingErrors=listener.Errors,syntheticOnly=true,normalStartup=false },new JsonSerializerOptions { WriteIndented=true })); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }
    private static async Task FakeStorage(string directory, Action<string,bool> check)
    {
        var row=new SavedResultRow { Id="one",Profile="local",Model="gpt-6-astra",Started=DateTimeOffset.UtcNow,TaskType="Ciallo",Stage="model",Version="v1",State="未完成",Prompt="ciallo",Text="partial" };
        var vm=new ResultsGalleryViewModel(()=> { Thread.Sleep(150); return new([],[]); },_=>throw new InvalidOperationException()); vm.Enter(); check("fake loading yields UI thread",vm.IsLoading && !vm.CurrentLoad.IsCompleted); await vm.CurrentLoad; check("fake 0 clear empty state",vm.IsEmpty);
        vm=new(()=>new([row],["local"]),_=>{}); vm.Enter(); await vm.CurrentLoad; check("fake 1 auto visible",vm.Items.Cast<object>().Count()==1);
        vm.Search="no such model"; check("fake search empty state",vm.IsEmpty && vm.EmptyText.Contains("匹配"));
        vm=new(()=>new(Enumerable.Range(0,2000).Select(i=>new SavedResultRow { Id=i.ToString(),Profile="local",Model="gpt-6-astra",Started=row.Started.AddSeconds(i),TaskType="Ciallo",Stage="model",Version="v1",State="未完成",Prompt="ciallo",Text="partial" }).ToArray(),["local"]),_=>{}); vm.Enter(); await vm.CurrentLoad; check("fake many all visible sorted without manual picker",vm.Items.Cast<object>().Count()==2000 && vm.Items.Cast<SavedResultRow>().First().Id=="1999");
        var page=new ResultsGallery { DataContext=vm }; UiVerification.ArrangeForCapture(page,900,600); var list=(ListBox)page.FindName("AllResultsList"); check("real 2000 item page realizes only visible rows",list.Items.Count==2000 && UiVerification.VisualDescendants(list).OfType<ListBoxItem>().Count() is >0 and <30);
        var watch = Stopwatch.StartNew(); var ticks = 0; double lastTick = 0, maxGap = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { var now=watch.Elapsed.TotalMilliseconds; maxGap=Math.Max(maxGap,now-lastTick); lastTick=now; ticks++; };
        timer.Start();
        var heavy = new ResultsGalleryViewModel(() => { Thread.Sleep(100); return new(Enumerable.Range(0,2000).Select(i=>new SavedResultRow { Id=i.ToString(),Profile="本机示例",Model="gpt-6-astra",Started=row.Started.AddSeconds(i),TaskType="检查并改进",Stage="看图检查",Version="visual-v4-en-zh",State="已完成",Prompt=new string('x',16000),Text="原始回复" }).ToArray(),["本机示例"]); },_=>{});
        heavy.Enter(); await heavy.CurrentLoad; var loadMs=watch.Elapsed.TotalMilliseconds;
        heavy.Search="visual-v4"; UiVerification.ArrangeForCapture(new ResultsGallery { DataContext=heavy },900,600); await Task.Delay(50); timer.Stop();
        File.WriteAllText(Path.Combine(directory,"responsiveness.json"),JsonSerializer.Serialize(new { rows=2000,promptCharactersPerRow=16000,loadMs,ticks,maxUiTickGapMs=maxGap,syntheticOnly=true }));
        check("2000 long prompts load hash filter and bind while dispatcher stays responsive",ticks>=3 && maxGap<1000 && heavy.Items.Cast<object>().Count()==2000);
        vm=new(()=>throw new IOException("fixture denied"),_=>{}); vm.Enter(); await vm.CurrentLoad; check("fake error recoverable",vm.Status.Contains("无法读取") && vm.RefreshCommand.CanExecute(null));
    }
    private static async Task SecurityEngine(string directory,Action<string,bool> check)
    {
        var browsers=InstalledBrowsers.Find(); File.WriteAllText(Path.Combine(directory,"local-engines.json"),JsonSerializer.Serialize(browsers.Select(b=>new { b.Name,b.Executable,version=FileVersionInfo.GetVersionInfo(b.Executable).FileVersion }),new JsonSerializerOptions{WriteIndented=true}));
        var browser=browsers.FirstOrDefault() ?? throw new IOException("Local Chromium required for actual animation verification.");
        var trap=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);trap.Start();
        try
        {
            var url="http://127.0.0.1:"+((System.Net.IPEndPoint)trap.LocalEndpoint).Port+"/blocked";
            var hostile="<html><body>offline boundary<script>fetch('"+url+"').catch(()=>{});window.open('"+url+"');try{top.location='"+url+"'}catch(e){}</script><img src='"+url+"'><iframe src='"+url+"'></iframe><form action='"+url+"' id='f'></form><script>try{document.getElementById('f').submit()}catch(e){};setTimeout(()=>location.href='"+url+"',100);</script></body></html>";
            await using var engine=await LocalArtworkSession.OpenAsync(browser.Executable,hostile,default); await engine.CaptureAsync(1,default); await Task.Delay(300);check("untrusted HTML fetch image iframe popup self/top navigation and forms cannot reach loopback trap",!trap.Pending());
        }
        finally { trap.Stop(); }
        var scopedRoot=Path.Combine(directory,"scope-fixture");var inside=Path.Combine(scopedRoot,"inside");var outside=Path.Combine(scopedRoot,"outside");Directory.CreateDirectory(inside);Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"result.json"),"{}");File.WriteAllText(Path.Combine(outside,"step-1.svg"),Svg);
        LinkFixtures.CreateDirectory(scopedRoot,Path.Combine(inside,"link"),outside,"gallery scope junction");
        var result=new VisualTestResult("fixture","gpt-6-astra",VisualTestKind.Static,"octopus-organ",VisualTestCatalog.Version,DateTimeOffset.UtcNow,Path.Combine(inside,"link"),[],false,false,"");
        try { ArtworkFiles.Scoped(result,"step-1.svg");check("reparse artwork scope rejected",false); } catch(StorageException) { check("reparse artwork scope rejected",true); }
        var zero=result with { Directory=inside,FailedRequest=new VisualRequestObservation(1,"未完成",null) };
        check("zero reply failed request retained as a result row",SavedResultRow.FromVisual(zero).Single().Stage.Contains("未完成"));
        check("missing prompt is labeled unknown without fabricated hash",SavedResultRow.FromVisual(zero).Single().PromptHash == "" && SavedResultRow.FromVisual(zero).Single().VersionLabel.Contains("未保存"));
    }
    private static VisualTestResult Seed(DesktopAccountServices services,string profile,VisualTestKind kind,string version,int day,string prompt,bool complete,bool cancelled)
    {
        var variant=kind==VisualTestKind.Variation?"octopus-organ":"octopus-organ"; var model="gpt-6-astra";
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-"+profile+"\n"+model+"\n"+kind+"\n"+variant+"\n"+version)));
        var directory=Path.Combine(services.Repository.AccountPath(profile),"visual-tests",key,"fixture-run-"+day); Directory.CreateDirectory(directory);
        var steps=new List<VisualTestStep>();
        for(var i=0;i<(kind==VisualTestKind.Revision?3:1);i++)
        {
            var review=kind==VisualTestKind.Revision && i==1; var stage=kind==VisualTestKind.Revision?new[]{"初稿","看图检查","修改稿"}[i]:"生成作品";
            var art=review?null:$"step-{i+1}"+(kind==VisualTestKind.Animation?".html":".svg"); var text=review?"Align the feet with the pedals.":kind==VisualTestKind.Animation?Html:i==2?Svg.Replace("#d4dfcb","#c1d7c4"):Svg;
            steps.Add(new(stage,i==0?prompt:review?VisualTestCatalog.ReviewPrompt(prompt):VisualTestCatalog.RevisionPrompt(prompt,Svg,"Align the feet with the pedals."),text,null,1,"fixture",art,review?null:$"step-{i+1}-preview.html","本机示例","responses","fixture-response", i == 0 ? Safety(false) : i == 2 ? Safety(true) : null));
            if(art is not null) { File.WriteAllText(Path.Combine(directory,art),text); var drawing=new DrawingVisual(); using(var dc=drawing.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(249,244,233)),null,new Rect(0,0,480,300)); dc.DrawEllipse(null,new Pen(Brushes.SlateGray,4),new Point(140,200),50,50);dc.DrawEllipse(null,new Pen(Brushes.SlateGray,4),new Point(350,200),50,50);dc.DrawLine(new Pen(Brushes.DarkSeaGreen,7),new Point(140,200),new Point(230,100));dc.DrawLine(new Pen(Brushes.DarkSeaGreen,7),new Point(230,100),new Point(350,200)); } var bitmap=new RenderTargetBitmap(480,300,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);Save(bitmap,Path.Combine(directory,$"step-{i+1}.png")); }
        }
        var result=new VisualTestResult(profile,model,kind,variant,version,DateTimeOffset.Parse("2025-01-01T12:00:00Z").AddDays(day),directory,steps,complete,cancelled,"fixture saved result");
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(result)); File.WriteAllText(Path.Combine(Path.GetDirectoryName(directory)!,"latest.json"),JsonSerializer.Serialize(result));return result;
    }
    private static SafetyBufferingObservation Safety(bool value)
    {
        var collector = new SafetyBufferingCollector();
        var created = ChunsonSpace.Verification.SafetyBufferingFixtures.Created(); created["safety_buffering"] = value;
        collector.Observe(created);
        collector.Observe(ChunsonSpace.Verification.SafetyBufferingFixtures.Completed());
        return collector.Snapshot(true);
    }
    private static string Pixels(BitmapSource bitmap) { var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return Convert.ToHexString(SHA256.HashData(bytes)); }
    private static string SpeedLabel(BitmapSource image) { var scale=image.PixelWidth/960;return Pixels(new CroppedBitmap(image,new Int32Rect(196*scale,36*scale,108*scale,26*scale))); }
    private static string MotionArea(BitmapSource image) { var scale=image.PixelWidth/960;return Pixels(new CroppedBitmap(image,new Int32Rect(0,100*scale,960*scale,500*scale))); }
    private static bool ThumbnailPainted(Image image) { var bitmap=new RenderTargetBitmap((int)Math.Ceiling(image.ActualWidth),(int)Math.Ceiling(image.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(image);return Painted(bitmap); }
    private static bool Painted(BitmapSource bitmap) { var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);var colors=new HashSet<int>();for(var i=0;i<bytes.Length;i+=64)colors.Add(BitConverter.ToInt32(bytes,i));return colors.Count>30; }
    private static void Save(BitmapSource bitmap,string path) { var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream); }
    private static async Task Until(Func<bool> condition) { var watch=Stopwatch.StartNew();while(!condition()) { if(watch.Elapsed>TimeSpan.FromSeconds(25)) throw new TimeoutException("gallery fixture wait");await Task.Delay(90); } }
}
