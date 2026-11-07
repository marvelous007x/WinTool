using System.Windows;

namespace WinTool;

public partial class OptionsWindow : Window
{
    readonly bool _ready;

    public OptionsWindow()
    {
        InitializeComponent();
        StartupBox.IsChecked = StartupManager.IsEnabled;
        _ready = true;
    }

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (StartupManager.SetEnabled(StartupBox.IsChecked == true)) return;

        MessageBox.Show(this, @"设置开机自启失败，详情见程序目录 data\error.log。", "软件设置");
        StartupBox.IsChecked = StartupManager.IsEnabled;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
