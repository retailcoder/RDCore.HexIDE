using RDCore.HexIDE.Platform;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Tests;

/// <summary>The add-in's questions and messages, answered as a test says and recorded.</summary>
internal sealed class Prompts : IUserPrompts
{
    public bool Answer { get; set; }
    public TaskCompletionSource<bool>? Pending { get; set; }
    public List<PlatformRelease> Asked { get; } = [];
    public List<string> Told { get; } = [];
    public bool InstallShown { get; private set; }

    /// <summary>The threads the installation reported its progress on.</summary>
    public List<int> ReportedOn { get; } = [];

    public Task<bool> ConfirmDownloadAsync(PlatformRelease release, string target)
    {
        Asked.Add(release);
        return Pending?.Task ?? Task.FromResult(Answer);
    }

    public Task TellAsync(string message)
    {
        Told.Add(message);
        return Task.CompletedTask;
    }

    public IInstallView ShowInstall(PlatformRelease release)
    {
        InstallShown = true;
        return new View(this);
    }

    private sealed class View(Prompts prompts) : IInstallView
    {
        public void Report(InstallProgress value)
        {
            lock (prompts.ReportedOn)
            {
                prompts.ReportedOn.Add(Environment.CurrentManagedThreadId);
            }
        }

        public void Dispose()
        {
        }
    }
}
