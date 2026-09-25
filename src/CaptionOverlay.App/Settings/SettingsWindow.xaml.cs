using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaptionOverlay.App.Hotkeys;

namespace CaptionOverlay.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    public void SelectTab(string? name)
    {
        if (name is null)
        {
            return;
        }
        foreach (TabItem tab in Tabs.Items)
        {
            if (string.Equals(tab.Header as string, name, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tab;
            }
        }
    }

    private void ManageModels_OnClick(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ModelsTab;

    private void OpenApiTab_OnClick(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ApiTab;

    private void SaveKey_OnClick(object sender, RoutedEventArgs e)
    {
        _vm.SetApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
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
