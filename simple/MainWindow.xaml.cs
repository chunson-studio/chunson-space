using System.Windows;

namespace ChunsonLogin;

public partial class MainWindow : Window
{
    public LoginViewModel Model { get; }
    public MainWindow(LoginViewModel model)
    {
        InitializeComponent(); Model = model; DataContext = model;
        Closed += (_, _) => Model.Cancel();
    }
    private async void Detect_Click(object sender, RoutedEventArgs e) => await Model.DetectAsync();
    private async void Sync_Click(object sender, RoutedEventArgs e) => await Model.SyncAsync();
    private async void Open_Click(object sender, RoutedEventArgs e) => await Model.OpenAsync();
    private void Clock_Click(object sender, RoutedEventArgs e) => Model.ShowClock();
}
