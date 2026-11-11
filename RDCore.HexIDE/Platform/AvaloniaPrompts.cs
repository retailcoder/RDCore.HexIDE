using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>The prompts as small windows of the add-in's own, owned by HexIDE's main window and themed as it is.</summary>
internal sealed class AvaloniaPrompts : IUserPrompts
{
    /// <inheritdoc/>
    public Task<PlatformAnswer> ConfirmDownloadAsync(PlatformRelease release, string target, bool canLocate) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var question = Strings.DownloadQuestion(release.Version.Text, Strings.Megabytes(release.ArchiveSize / 1_000_000d),
                "github.com/rubberduck-vba/RDCore", target);
            return canLocate
                ? AskAsync(question + Environment.NewLine + Environment.NewLine + Strings.LocateInstead,
                    (Strings.DownloadYes, PlatformAnswer.Install), (Strings.Locate, PlatformAnswer.Locate))
                : AskAsync(question, (Strings.DownloadYes, PlatformAnswer.Install));
        });

    /// <inheritdoc/>
    public Task<PlatformAnswer> ChooseFoundAsync(PlatformFolder found, string target) => Dispatcher.UIThread.InvokeAsync(() =>
        AskAsync(Strings.FoundQuestion(found.Version, found.Root, target),
            (Strings.UseFound, PlatformAnswer.UseFound), (Strings.InstallInstead, PlatformAnswer.Install), (Strings.Locate, PlatformAnswer.Locate)));

    /// <inheritdoc/>
    public Task<string?> PickFolderAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (MainWindow is not { } owner)
        {
            return null;
        }

        var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.LocateTitle,
            AllowMultiple = false,
        });
        return picked.Count == 1 ? picked[0].TryGetLocalPath() : null;
    });

    // The question, with a button for each answer, the first the default, and "Not now" last, which closing it answers too.
    private static async Task<PlatformAnswer> AskAsync(string question, params (string Label, PlatformAnswer Answer)[] answers)
    {
        var answer = PlatformAnswer.NotNow;
        var window = Dialog(question);
        var buttons = answers.Select((choice, index) =>
        {
            var button = new Button { Content = choice.Label, IsDefault = index == 0 };
            button.Click += (_, _) =>
            {
                answer = choice.Answer;
                window.Close();
            };
            return button;
        }).ToList();
        var later = new Button { Content = Strings.DownloadNo, IsCancel = true };
        later.Click += (_, _) => window.Close();
        buttons.Add(later);
        AddButtons(window, [.. buttons]);
        await ShowAsync(window);
        return answer;
    }

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
