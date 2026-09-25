using System.Windows;

namespace CaptionOverlay.App.FirstRun;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;

    public FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Finished += Close;
        Closed += (_, _) => vm.Dispose();
    }

    private void SaveKey_OnClick(object sender, RoutedEventArgs e)
    {
        _vm.SetApiKey(KeyBox.Password);
        KeyBox.Clear();
    }
}
