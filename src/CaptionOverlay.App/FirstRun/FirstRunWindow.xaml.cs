using System.Windows;
using CaptionOverlay.App.Infrastructure;

namespace CaptionOverlay.App.FirstRun;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;
    private readonly ApiKeyField _keyField;

    public FirstRunWindow(FirstRunViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _keyField = new ApiKeyField(KeyBox);
        _keyField.Show(vm.HasStoredApiKey);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunViewModel.HasStoredApiKey))
            {
                _keyField.Show(vm.HasStoredApiKey);
            }
        };
        vm.Finished += Close;
        Closed += (_, _) => vm.Dispose();
    }

    private void SaveKey_OnClick(object sender, RoutedEventArgs e)
    {
        if (_keyField.EnteredKey is { } key)
        {
            _vm.SetApiKey(key); // empty removes the key
        }
    }
}
