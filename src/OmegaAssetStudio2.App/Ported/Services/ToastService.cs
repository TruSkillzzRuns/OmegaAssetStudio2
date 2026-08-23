using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OmegaAssetStudio.WinUI.Services;

// Lightweight bottom-right toast notifier. Any service or page can call
// ToastService.Info / Success / Error and a translucent pill animates in,
// stays for a few seconds, then fades. Replaces buried-in-status-text
// async-op completions across the app.
public static class ToastService
{
    public enum ToastKind { Info, Success, Warning, Error }

    public sealed class ToastViewModel
    {
        public string Message { get; init; } = string.Empty;
        public ToastKind Kind { get; init; }
        public string Glyph => Kind switch
        {
            ToastKind.Success => "",
            ToastKind.Warning => "",
            ToastKind.Error => "",
            _ => "",
        };
        public Brush AccentBrush => Kind switch
        {
            ToastKind.Success => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 92, 203, 142)),
            ToastKind.Warning => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 179, 65)),
            ToastKind.Error => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 88, 76)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 163, 255)),
        };
    }

    public static readonly ObservableCollection<ToastViewModel> ActiveToasts = new();
    public static event EventHandler<ToastViewModel>? ToastPosted;

    public static void Info(string message) => Post(message, ToastKind.Info);
    public static void Success(string message) => Post(message, ToastKind.Success);
    public static void Warning(string message) => Post(message, ToastKind.Warning);
    public static void Error(string message) => Post(message, ToastKind.Error);

    public static void Post(string message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ToastViewModel toast = new() { Message = message, Kind = kind };
        try
        {
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                ActiveToasts.Add(toast);
                ToastPosted?.Invoke(null, toast);
                _ = AutoDismissAsync(toast);
            });
        }
        catch { }
    }

    private static async System.Threading.Tasks.Task AutoDismissAsync(ToastViewModel toast)
    {
        int dwellMs = toast.Kind == ToastKind.Error ? 6000 : 3500;
        await System.Threading.Tasks.Task.Delay(dwellMs).ConfigureAwait(false);
        try
        {
            App.MainWindow?.DispatcherQueue.TryEnqueue(() => ActiveToasts.Remove(toast));
        }
        catch { }
    }
}
