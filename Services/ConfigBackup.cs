using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.Core;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Reframe.Services;

/// <summary>
/// Config import/export (called by the Settings page "Config backup" buttons per the M5 contract).
///
/// Export: FileSavePicker chooses the target → serialize the current <see cref="ConfigService"/>
/// config via <see cref="ConfigJsonContext"/> and write it out.
/// Import: FileOpenPicker chooses a .json → read → validate by deserializing with
/// <see cref="ConfigJsonContext"/> (on failure show an "invalid file" dialog) → land it via
/// <see cref="ConfigService"/>. ConfigService has no "whole-config replace" entry point, so we take
/// the least-invasive path: write config.json directly (equivalent to an external manual edit), and
/// let the FileSystemWatcher hot reload take over → Reload → Changed → UI refresh.
///
/// On WinUI 3 desktop, a Picker must be InitializeWithWindow'd against the main window handle (via
/// App.Main), otherwise it throws a COM exception. Everything is wrapped in try/catch; a user
/// cancellation returns false without an error.
/// </summary>
public static class ConfigBackup
{
    /// <summary>Export the current config to a user-chosen .json file. True on success; false on cancel/failure.</summary>
    public static async Task<bool> ExportAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"Reframe-config-{DateTime.Now:yyyyMMdd}",
            };
            picker.FileTypeChoices.Add(Loc.T("Services/BackupFileTypeLabel"), new List<string> { ".json" });

            if (!TryInitWithWindow(picker)) return false;

            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;   // user cancelled

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

    /// <summary>Import config from a user-chosen .json: validate → write → hot reload takes over.
    /// True on success; false on cancel/invalid/failure.</summary>
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
            if (file is null) return false;   // user cancelled

            string json = await FileIO.ReadTextAsync(file);

            // Validate by deserializing with the same source-gen context used for writing. null / throw → invalid file.
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

            // Land it: ConfigService has no "whole-config replace" entry point, so write config.json
            // directly (equivalent to an external manual edit) and let the FileSystemWatcher hot
            // reload take over (Reload → atomic reference swap → Changed → UI refresh).
            ConfigStore.Save(imported);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // —— A Picker must be bound to the main window handle (WinUI 3 desktop); if no handle is available, give up (return false without an error).
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
                Title = Loc.T("Services/ImportInvalidTitle"),
                Content = Loc.T("Services/ImportInvalidContent"),
                CloseButtonText = Loc.T("Common/Ok"),
                XamlRoot = root,
            };
            await dialog.ShowAsync();
        }
        catch { /* the dialog itself failing (no XamlRoot, etc.): ignore */ }
    }
}
