using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>The prompts as small windows of the add-in's own, owned by HexIDE's main window and themed as it is.</summary>
internal sealed class AvaloniaPrompts : IUserPrompts
{
    /// <inheritdoc/>
    public Task<bool> ConfirmDownloadAsync(PlatformRelease release, string target) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var agreed = false;
        var window = Dialog(Strings.DownloadQuestion(release.Version.Text, Strings.Megabytes(release.ArchiveSize / 1_000_000d),
            "github.com/rubberduck-vba/RDCore", target));
        var download = new Button { Content = Strings.DownloadYes, IsDefault = true };
        var later = new Button { Content = Strings.DownloadNo, IsCancel = true };
        download.Click += (_, _) =>
        {
            agreed = true;
            window.Close();
        };
        later.Click += (_, _) => window.Close();
        AddButtons(window, download, later);
        await ShowAsync(window);
        return agreed;
    });

    /// <inheritdoc/>
    public Task TellAsync(string message) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var window = Dialog(message);
        var ok = new Button { Content = Strings.Ok, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => window.Close();
        AddButtons(window, ok);
        await ShowAsync(window);
    });

    /// <inheritdoc/>
    public IInstallView ShowInstall(PlatformRelease release) => Dispatcher.UIThread.Invoke(() => new InstallWindow(release));

    private static Window Dialog(string message) => new()
    {
        Title = Strings.DownloadTitle,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 } },
        },
    };

    private static void AddButtons(Window window, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.AddRange(buttons);
        ((StackPanel)window.Content!).Children.Add(row);
    }

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static async Task ShowAsync(Window window)
    {
        if (MainWindow is { } owner)
        {
            await window.ShowDialog(owner);
            return;
        }
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closed.Task;
    }

    // Not modal: HexIDE stays usable while the platform downloads, which on a slow link takes a while.
    private sealed class InstallWindow : IInstallView
    {
        private readonly Window window;
        private readonly TextBlock stage;
        private readonly ProgressBar bar;
        private readonly string version;

        public InstallWindow(PlatformRelease release)
        {
            version = release.Version.Text;
            stage = new TextBlock { Text = Strings.Downloading(version) };
            bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 360, IsIndeterminate = true };
            window = Dialog("");
            var panel = (StackPanel)window.Content!;
            panel.Children.Clear();
            panel.Children.Add(stage);
            panel.Children.Add(bar);
            if (MainWindow is { } owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        public void Report(InstallProgress value) => Dispatcher.UIThread.Post(() =>
        {
            stage.Text = value.Stage switch
            {
                InstallStage.Downloading => Strings.Downloading(version),
                InstallStage.Verifying => Strings.Verifying,
                InstallStage.Extracting => Strings.Extracting,
                _ => Strings.Deploying,
            };
            bar.IsIndeterminate = value.Stage != InstallStage.Downloading || value.TotalBytes is not > 0;
            if (value.TotalBytes is > 0 and var total)
            {
                bar.Value = (double)value.Bytes / total;
            }
        });

        public void Dispose() => Dispatcher.UIThread.Post(window.Close);
    }
}
