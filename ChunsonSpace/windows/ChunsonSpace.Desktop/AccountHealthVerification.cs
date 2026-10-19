using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Verification;

namespace ChunsonSpace.Desktop;

internal static class AccountHealthVerification
{
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    internal static async Task RunStandalone(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Fresh verification directory required");
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var renders = new List<object>(); var passed = false;
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        string Sse(string kind, string model) => SafetyBufferingFixtures.Stream(kind, "SYNTHETIC raw ciallo reply").Replace("fixture-declared", model);
        try
        {
            foreach (var dark in new[] { false, true })
            foreach (var (width, height) in new[] { (1040, 670) })
            {
                var key = $"{(dark ? "dark" : "light")}-{width}x{height}";
                var w = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, key));
                w.Profiles.Rename(new(IdentityKind.Account,"A"),"SYNTHETIC-A"); w.Profiles.Rename(new(IdentityKind.Account,"B"),"SYNTHETIC-B");
                w.Profiles.CreateBoundAccount("SYNTHETIC-FROZEN",new("http://127.0.0.1:7903","America/New_York","en-US"));
                w.Profiles.SetTestsFrozen("SYNTHETIC-FROZEN",true);
                w.Profiles.SetAccountMarks("SYNTHETIC-A",AccountQuality.SuspectedDegraded,false,true);
                var marksBefore=w.Profiles.ReadAccountMarks("SYNTHETIC-A");
                var transport=w.Accounts!.OfflineTransport!; transport.ResponsesAreSse=true;
                var hold=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var fast=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int active=0,maximum=0;
                transport.ResponseFactory=async (req,token)=>
                {
                    var current=Interlocked.Increment(ref active); AccountHealthParallelVerification.Max(ref maximum,current);
                    var isA=req.Headers.GetValueOrDefault("chatgpt-account-id")=="fixture-id-A";
                    try { await (isA?hold:fast).Task.WaitAsync(token); return Sse(isA ? "false" : "missing",JsonNode.Parse(req.Body!)!["model"]!.GetValue<string>()); }
                    finally { Interlocked.Decrement(ref active); }
                };
                var model=new ShellViewModel(w) { DarkTheme=dark }; var window=UiVerification.CreateWindow(model); var root=window.RenderRoot;
                void Layout()=>UiVerification.ArrangeForCapture(root,width,height);
                void Capture(string suffix)
                {
                    Layout(); UiVerification.AssertWindowsOutsideDesktop();
                    Check(key+" theme", WorkspaceTheme.Current==(dark?"dark":"light"));
                    var path=Path.Combine(directory,key+"-"+suffix+"-SYNTHETIC.png");
                    var bmp=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bmp.Render(root);
                    var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var f=File.Create(path); png.Save(f);
                    renders.Add(new {path,width,height,synthetic=true});
                }
                try
                {
                    model.OpenModelTestCommand.Execute(null); Layout(); Capture("entry");
                    var button=(Button)window.FindName("TestAllAccountsButton");
                    Check(key+" bound entry visible",UiVerification.FullyVisible(button,root)&&button.Command==model.TestAllAccountsCommand&&button.IsEnabled);
                    Check(key+" visible quota",UiVerification.FullyVisible((TextBlock)window.FindName("AccountHealthQuotaText"),root));
                    Check(key+" no request before click",transport.RequestCount==0);
                    button.Command.Execute(null); var panel=model.AccountHealth!;
                    await BackgroundAccountTestsVerification.Until(()=>transport.RequestCount==2);
                    var uiThread=Environment.CurrentManagedThreadId; var wrongThread=0;
                    panel.PropertyChanged+=(_,_)=> { if(Environment.CurrentManagedThreadId!=uiThread) Interlocked.Increment(ref wrongThread); };
                    foreach(var row in panel.Rows) row.PropertyChanged+=(_,_)=> { if(Environment.CurrentManagedThreadId!=uiThread) Interlocked.Increment(ref wrongThread); };
                    button.Command.Execute(null); Check(key+" duplicate blocked",!button.Command.CanExecute(null)&&transport.RequestCount==2);
                    Layout(); Capture("running");
                    Check(key+" cancel visible",UiVerification.FullyVisible((Button)window.FindName("HealthCancelButton"),root));
                    Check(key+" lifecycle protects global mutations",model.IsModelTestRunning&&!model.RebuildCommand.CanExecute(null));
                    panel.CloseCommand.Execute(null); Check(key+" background running",!model.IsAccountHealthOpen&&model.IsModelTestRunning);
                    model.OpenBackgroundModelCommand.Execute(null); Check(key+" recover same panel",ReferenceEquals(panel,model.AccountHealth)&&transport.RequestCount==2);
                    fast.SetResult(); await BackgroundAccountTestsVerification.Until(()=>panel.Rows.Single(r=>r.Profile=="SYNTHETIC-B").Finished);
                    Check(key+" faster account visible while slow account held",panel.IsBusy&&!panel.Rows.Single(r=>r.Profile=="SYNTHETIC-A").Finished&&panel.ProgressSummary.Contains("已结束 2/3"));
                    Capture("partial");
                    hold.SetResult(); await panel.CurrentOperation; Layout(); Capture("results");
                    Check(key+" actual account overlap",transport.RequestCount==4&&maximum==2);
                    Check(key+" UI notifications stay on dispatcher",wrongThread==0);
                    Check(key+" current results include manual",panel.Rows.Single(r=>r.Profile=="SYNTHETIC-A").Result is {State:"normal",Mode:AccountMarkMode.Manual,MarkOutcome:"保留手动标记"});
                    Check(key+" manual history preserved",w.Profiles.ReadAccountMarks("SYNTHETIC-A")==marksBefore);
                    Check(key+" automatic abnormal persisted",w.Profiles.ReadAccountMarks("SYNTHETIC-B").Quality==AccountQuality.SuspectedDegraded);
                    Check(key+" frozen summary",panel.Rows.Single(r=>r.Profile=="SYNTHETIC-FROZEN").Result?.State=="frozen"&&panel.Summary.Contains("跳过冻结 1"));
                    Check(key+" frozen model rows terminal",panel.Rows.Single(r=>r.Profile=="SYNTHETIC-FROZEN").Models.All(m=>m.Finished&&m.State=="未请求"&&m.Text.Contains("冻结")));
                    Check(key+" original prompt and fixed models",transport.Requests.All(req=> { var body=JsonNode.Parse(req.Body!); return CialloTest.Models.Contains(body!["model"]!.GetValue<string>())&&body["reasoning"]?["effort"]?.ToString()=="high"&&body.ToJsonString().Contains("ciallo"); }));
                    var scroll=(ScrollViewer)window.FindName("HealthResultsScroll");
                    foreach(var exp in UiVerification.VisualDescendants(scroll).OfType<Expander>()) exp.IsExpanded=true;
                    Layout(); scroll.ScrollToEnd(); Layout(); Capture("details-bottom");
                    Check(key+" details actual scroll",scroll.ScrollableHeight>0&&scroll.VerticalOffset>0&&Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)<2);
                    Check(key+" footer remains visible",UiVerification.FullyVisible((Button)window.FindName("HealthCancelButton"),root));
                    Check(key+" escape closes only panel",window.HandleShortcut(System.Windows.Input.Key.Escape,System.Windows.Input.ModifierKeys.None)&&!model.IsAccountHealthOpen);
                    foreach(var a in w.Profiles.ListAccounts()) w.Profiles.SetTestsFrozen(a.Name,true);
                    model.RefreshCommand.Execute(null); Check(key+" all frozen disables",!model.TestAllAccountsCommand.CanExecute(null));
                }
                finally { window.Close(); await w.Accounts.DisposeAsync(); }
            }
            // Boundary cases go through the production batch, Ciallo, SSE parser and shared persistence.
            foreach(var kind in new[]{"false","missing","true","null","conflict","truncated","missing-incomplete","mismatch","model-mismatch","network","models-disagree"})
            {
                var w=BackgroundAccountTestsVerification.Fixture(Path.Combine(directory,"case-"+kind));
                w.Profiles.SetTestsFrozen("B",true); var t=w.Accounts!.OfflineTransport!; t.ResponsesAreSse=true;
                w.Profiles.SetAccountMarks("A",AccountQuality.SuspectedDegraded,false,false,mode:AccountMarkMode.Automatic);
                t.ResponseFactory=(req,token)=>
                {
                    if(kind=="network") throw new AccountException(AccountError.Network,"SYNTHETIC transport failure");
                    var m=JsonNode.Parse(req.Body!)!["model"]!.GetValue<string>();
                    return Task.FromResult(Sse(kind=="models-disagree"?(m==CialloTest.Models[0]?"false":"missing"):kind,m));
                };
                var r=await w.Accounts.Accounts.TestAccountHealthBatchAsync();
                Check(kind+" verdict",r.Results.Single(x=>x.Profile=="A").State==(kind=="false"?"normal":kind=="missing"?"abnormal":"inconclusive"));
                Check(kind+" no retries",t.RequestCount==2);
                Check(kind+" preservation",w.Profiles.ReadAccountMarks("A").Quality==(kind=="false"?AccountQuality.Normal:AccountQuality.SuspectedDegraded));
                await w.Accounts.DisposeAsync();
            }
            foreach(var mode in new[]{"queued-freeze","queued-cancel","identity-change","between-model-freeze"})
            {
                var w=BackgroundAccountTestsVerification.Fixture(Path.Combine(directory,mode)); w.Profiles.SetTestsFrozen("B",true);
                var t=w.Accounts!.OfflineTransport!; t.ResponsesAreSse=true; var q=new AccountTestQueue(); using var c=new CancellationTokenSource();
                var hold=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var prior=mode=="between-model-freeze"?Task.CompletedTask:q.RunAsync("A",async()=>{await hold.Task;return 0;},default);
                t.ResponseFactory=(req,token)=> { if(mode=="between-model-freeze") w.Profiles.SetTestsFrozen("A",true); return Task.FromResult(Sse("false",JsonNode.Parse(req.Body!)!["model"]!.GetValue<string>())); };
                var queued=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task=w.Accounts.Accounts.TestAccountHealthBatchAsync(new InlineProgress<AccountHealthProgress>(p=>{if(p.Profile=="A"&&p.Stage=="queued")queued.TrySetResult();}),c.Token,(name,run,token)=>q.RunAsync(name,run,token));
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if(mode=="queued-freeze") w.Profiles.SetTestsFrozen("A",true);
                if(mode=="queued-cancel") c.Cancel();
                if(mode=="identity-change") { var auth=w.Profiles.ReadAccountCredentials("A"); auth["tokens"]!["account_id"]="SYNTHETIC-changed"; w.Profiles.SaveAccount("A",auth,overwrite:true); }
                hold.SetResult(); await prior; var r=await task;
                Check(mode+" dispatch bound",t.RequestCount==(mode=="between-model-freeze"?1:0));
                Check(mode+" no new conclusion",r.Results.All(x=>x.State is "frozen" or "cancelled" or "inconclusive")&&w.Profiles.ReadAccountMarks("A").Quality==AccountQuality.Unmarked);
                await w.Accounts.DisposeAsync();
            }
            {
                var w=BackgroundAccountTestsVerification.Fixture(Path.Combine(directory,"exit")); var t=w.Accounts!.OfflineTransport!;
                t.ResponseFactory=async(_,token)=>{await Task.Delay(Timeout.Infinite,token);return "";};
                var model=new ShellViewModel(w); model.TestAllAccountsCommand.Execute(null);
                await BackgroundAccountTestsVerification.Until(()=>t.RequestCount==2);
                Check("exit drains active batch",await model.DrainTasksForExitAsync()&&!model.IsModelTestRunning&&t.RequestCount==2);
                Check("exit terminates unsent model rows",model.AccountHealth!.Rows.All(r=>r.Finished&&r.Models.All(m=>m.Finished)));
                await w.Accounts.DisposeAsync();
            }
            await AccountHealthParallelVerification.RunAsync(Path.Combine(directory,"parallel"),Check);
            await BackgroundAccountTestsVerification.RunAsync(Path.Combine(directory,"existing-background"),Check);
            Check("no binding errors",listener.Errors.Count==0); passed=true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory,"ui-verification.json"),JsonSerializer.Serialize(new{passed,checks,bindingErrors=listener.Errors,syntheticOnly=true,actualAccountOperations=false,normalStartup=false},new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(directory,"renders.json"),JsonSerializer.Serialize(renders,new JsonSerializerOptions{WriteIndented=true}));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
