using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaptionOverlay.App.Hotkeys;
using CaptionOverlay.App.Infrastructure;

namespace CaptionOverlay.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly ApiKeyField _keyField;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _keyField = new ApiKeyField(ApiKeyBox);
        _keyField.Show(vm.HasStoredApiKey);
        vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelChanged;
            vm.Dispose();
        };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.HasStoredApiKey))
        {
            _keyField.Show(_vm.HasStoredApiKey); // other provider, or the key was saved/removed
        }
    }

    /// <summary>Selects a tab by its English name ("Models"), matching the TabItem's x:Name ("ModelsTab"); headers are localized.</summary>
    public void SelectTab(string? name)
    {
        if (name is null)
        {
            return;
        }
        foreach (TabItem tab in Tabs.Items)
        {
            if (string.Equals(tab.Name, name + "Tab", StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tab;
            }
        }
    }

    private void ManageModels_OnClick(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ModelsTab;

    private void OpenApiTab_OnClick(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ApiTab;

    private void SaveKey_OnClick(object sender, RoutedEventArgs e)
    {
        if (_keyField.EnteredKey is { } key)
        {
            _vm.SetApiKey(key); // empty removes the key
        }
    }

    /// <summary>Captures a key combination into the hotkey text box.</summary>
    private void Hotkey_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            box.Text = "";
        }
        else if (key is Key.Tab)
        {
            e.Handled = false;
            return;
        }
        else if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // wait for the actual key
        }
        else if (Keyboard.Modifiers != ModifierKeys.None)
        {
            box.Text = new Hotkey(Keyboard.Modifiers, key).ToString();
        }
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
