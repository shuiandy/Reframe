using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Reframe.UI.Controls;

/// <summary>
/// 仅为暴露光标设置而存在的最小 Grid 子类:UIElement.ProtectedCursor 是 protected,
/// 子类里转成公开方法,供 RegionPicker 设置十字光标。
/// </summary>
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
