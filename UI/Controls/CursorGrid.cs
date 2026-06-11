using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Reframe.UI.Controls;

/// <summary>
/// A minimal Grid subclass that exists only to expose cursor setting: UIElement.ProtectedCursor is
/// protected, so this subclass surfaces it as a public method for RegionPicker to set the crosshair cursor.
/// </summary>
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
