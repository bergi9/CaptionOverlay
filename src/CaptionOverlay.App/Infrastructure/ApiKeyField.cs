using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CaptionOverlay.App.Infrastructure;

/// <summary>
/// Makes a <see cref="PasswordBox"/> show a stored API key as a row of dots without ever holding the key: the box is filled
/// with placeholder characters, and the first edit (typing, pasting, Backspace/Delete) replaces them. Saving the untouched
/// placeholder changes nothing; saving an empty box removes the key.
/// </summary>
public sealed class ApiKeyField
{
    private static readonly string Placeholder = new('•', 20);
    private readonly PasswordBox _box;
    private bool _showingStored;

    public ApiKeyField(PasswordBox box)
    {
        _box = box;
        box.PreviewTextInput += (_, _) => DropPlaceholder();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is (Key.Back or Key.Delete) && DropPlaceholder())
            {
                e.Handled = true; // the placeholder goes as a whole, nothing more to delete
            }
        };
        CommandManager.AddPreviewExecutedHandler(box, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Paste)
            {
                DropPlaceholder();
            }
        });
    }

    /// <summary>The text to save, or null when the box still shows the stored key (nothing to change).</summary>
    public string? EnteredKey => _showingStored ? null : _box.Password;

    /// <summary>Shows dots when a key is stored, an empty box otherwise.</summary>
    public void Show(bool keyStored)
    {
        _box.Password = keyStored ? Placeholder : "";
        _showingStored = keyStored;
    }

    /// <returns>Whether the placeholder was shown (and is now gone).</returns>
    private bool DropPlaceholder()
    {
        if (!_showingStored)
        {
            return false;
        }
        _showingStored = false;
        _box.Clear();
        return true;
    }
}
