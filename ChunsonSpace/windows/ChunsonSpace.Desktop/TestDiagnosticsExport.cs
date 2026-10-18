using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ChunsonSpace.Core;
using Microsoft.Win32;

namespace ChunsonSpace.Desktop;

internal static class TestDiagnosticsExport
{
    private static readonly string[] Names = ["wire-request.json", "response-headers.json", "response.sse", "trace-summary.json", "request.json"];
    private static IEnumerable<string> Files(string directory)
    {
        PhysicalFileStorage.RejectLinks(directory);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!Regex.IsMatch(Path.GetFileName(child), "^request-[0-9]+$")) continue;
            PhysicalFileStorage.RejectLinks(child);
            foreach (var name in Names)
            {
                var file = Path.Combine(child, name);
                if (!File.Exists(file)) continue;
                PhysicalFileStorage.RejectLinks(file);
                yield return file;
            }
        }
    }
    public static bool HasData(string? directory)
    {
        try { return directory is not null && Directory.Exists(directory) && Files(directory).Any(p => Path.GetFileName(p) == "wire-request.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or StorageException) { return false; }
    }
    internal static void Export(string directory, string target)
    {
        var files = Files(directory).ToArray();
        if (!files.Any(p => Path.GetFileName(p) == "wire-request.json")) throw new IOException("这条历史记录没有保存请求与响应诊断；新测试会自动记录。");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var file in files) archive.CreateEntryFromFile(file, Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Optimal);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public partial class MainWindow
{
    private void ExportTestDiagnostics(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directory } || !TestDiagnosticsExport.HasData(directory)) return;
        var dialog = new SaveFileDialog { Title = "导出请求与响应（已脱敏）", Filter = "ZIP 文件|*.zip", FileName = "Chunson-Test-Diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { TestDiagnosticsExport.Export(directory, dialog.FileName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or StorageException)
        { MessageBox.Show(this, "诊断导出未完成：" + error.Message, "Chunson Space", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
