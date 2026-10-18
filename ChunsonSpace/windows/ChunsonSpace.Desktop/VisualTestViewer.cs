using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class VisualTestViewer
{
    public static void OpenExternal(VisualTestResult result, int? stepIndex = null)
    {
        var row = SavedResultRow.FromVisual(result).FirstOrDefault(r => r.HasArtwork && (stepIndex is null || r.StepIndex == stepIndex))
            ?? throw new IOException("这一步没有保存的作品。");
        var content = VisualTestArtifact.Wrap(ArtworkFiles.ReadArtwork(row));
        var cache = Path.Combine(Path.GetTempPath(), "ChunsonSpace-artwork-export");
        PhysicalFileStorage.RejectLinks(cache); Directory.CreateDirectory(cache);
        var page = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) + ".html");
        new PhysicalFileStorage().WriteAtomic(page, Encoding.UTF8.GetBytes(content));
        var browser = InstalledBrowsers.Find().FirstOrDefault()
            ?? throw new IOException("未找到 Chrome、Edge 或 Brave；作品已保留在测试历史中。");
        Process.Start(StartInfo(browser.Executable, page));
    }

    internal static ProcessStartInfo StartInfo(string browser, string page, string? cacheDirectory = null)
    {
        var requestedPath = Path.GetFullPath(page);
        if (!File.Exists(requestedPath)) throw new FileNotFoundException("作品页面不存在，可重新从测试历史打开。", requestedPath);
        // A packaged parent can redirect LocalAppData. An already-running external
        // browser does not share that namespace, so pass the physical file path.
        var path = WindowsDesktopLaunchSystem.ResolveRealPath(requestedPath);
        if (!File.Exists(path)) throw new FileNotFoundException("无法定位作品的实际文件，请重新从测试历史打开。", path);
        if (path.Length >= 260)
        {
            // These generated pages are self-contained (sandboxed srcdoc). Chrome
            // cannot reliably open long physical paths, even when .NET can read them.
            var cache = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonSwitch", "visual-previews");
            Directory.CreateDirectory(cache);
            cache = WindowsDesktopLaunchSystem.ResolveRealPath(cache);
            PhysicalFileStorage.RejectLinks(cache);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".html";
            var shortPage = Path.Combine(cache, name);
            if (shortPage.Length >= 260) throw new IOException("本机预览目录路径过长；作品仍保留在测试历史中。");
            new PhysicalFileStorage().WriteAtomic(shortPage, File.ReadAllBytes(path));
            path = shortPage;
        }
        // Windows process startup rejects some long working directories even when
        // Chromium can read the same long file URI. Use the browser installation.
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(browser))! };
        // Pass one explicit file URL; do not send a raw Windows path through the HTML association.
        info.ArgumentList.Add(new Uri(path).AbsoluteUri);
        return info;
    }

    internal static string PreparePage(VisualTestResult result, int? stepIndex = null)
    {
        if (stepIndex is { } selectedIndex && (selectedIndex < 0 || selectedIndex >= result.Steps.Count))
            throw new IOException("这一步没有保存的作品。");
        if (!Path.IsPathFullyQualified(result.Directory) || !Directory.Exists(result.Directory)
            || !File.Exists(Path.Combine(result.Directory, "result.json")))
            throw new IOException("这条历史记录的作品目录已移动或删除；请重新选择仍存在的测试记录。");
        var page = Path.Combine(result.Directory, stepIndex is { } index ? $"step-{index + 1}-preview.html" : "gallery.html");
        if (File.Exists(page)) return page;
        string content;
        if (stepIndex is { } selected)
        {
            var artwork = VisualTestArtifact.Extract(result.Steps[selected].Reply, result.Kind == VisualTestKind.Animation)
                ?? throw new IOException("这一步没有有效作品；原始回复仍可在历史记录中查看。");
            content = VisualTestArtifact.Wrap(artwork);
        }
        else content = VisualTestArtifact.Gallery(result);
        File.WriteAllText(page, content);
        return page;
    }
}
