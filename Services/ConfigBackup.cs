using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.Core;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Reframe.Services;

/// <summary>
/// 配置的导入/导出(设置页「配置备份」按钮按 M5 契约签名调用)。
///
/// 导出:FileSavePicker 选目标 → 把当前 <see cref="ConfigService"/> 配置用 <see cref="ConfigJsonContext"/> 序列化写出。
/// 导入:FileOpenPicker 选 .json → 读取 → 用 <see cref="ConfigJsonContext"/> 反序列化校验(失败弹「文件无效」)→
///       经 <see cref="ConfigService"/> 落地。ConfigService 无「整体替换」入口,故走侵入最小的路:
///       直接写盘到 config.json(等同外部手改),由 FileSystemWatcher 热重载接手 → Reload → Changed → UI 刷新。
///
/// WinUI3 桌面下 Picker 必须 InitializeWithWindow 绑主窗口句柄(经 App.Main),否则抛 COM 异常。
/// 全程 try/catch;用户取消选择返回 false 且不报错。
/// </summary>
public static class ConfigBackup
{
    /// <summary>导出当前配置到用户选择的 .json 文件。成功 true;取消/失败 false。</summary>
    public static async Task<bool> ExportAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"Reframe-config-{DateTime.Now:yyyyMMdd}",
            };
            picker.FileTypeChoices.Add("Reframe 配置", new List<string> { ".json" });

            if (!TryInitWithWindow(picker)) return false;

            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;   // 用户取消

            string json = JsonSerializer.Serialize(
                ConfigService.Instance.Config, ConfigJsonContext.Default.AppConfig);
            await FileIO.WriteTextAsync(file, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从用户选择的 .json 导入配置:校验 → 写盘 → 热重载接手。成功 true;取消/无效/失败 false。</summary>
    public static async Task<bool> ImportAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");

            if (!TryInitWithWindow(picker)) return false;

            var file = await picker.PickSingleFileAsync();
            if (file is null) return false;   // 用户取消

            string json = await FileIO.ReadTextAsync(file);

            // 用与写盘一致的源生成上下文反序列化校验。null / 抛异常 → 文件无效。
            AppConfig? imported;
            try
            {
                imported = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
            }
            catch
            {
                imported = null;
            }

            if (imported is null)
            {
                await ShowInvalidDialog(root);
                return false;
            }

            // 落地:ConfigService 无「整体替换」入口,直接写 config.json(等同外部手改),
            // 由 FileSystemWatcher 热重载接手(Reload → 原子替换引用 → Changed → UI 刷新)。
            ConfigStore.Save(imported);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // —— Picker 必须绑主窗口句柄(WinUI3 桌面);拿不到句柄则放弃(返回 false 不报错)。
    private static bool TryInitWithWindow(object picker)
    {
        if (App.Main is not { } w) return false;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
        if (hwnd == IntPtr.Zero) return false;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return true;
    }

    private static async Task ShowInvalidDialog(Microsoft.UI.Xaml.XamlRoot root)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "导入失败",
                Content = "文件无效:不是有效的 Reframe 配置文件。",
                CloseButtonText = "确定",
                XamlRoot = root,
            };
            await dialog.ShowAsync();
        }
        catch { /* 弹窗本身失败(无 XamlRoot 等):忽略 */ }
    }
}
