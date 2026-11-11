using RDCore.HexIDE.Platform;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Tests;

/// <summary>The add-in's questions and messages, answered as a test says and recorded.</summary>
internal sealed class Prompts : IUserPrompts
{
    /// <summary>The answer to a download question: download, or not now.</summary>
    public bool Answer { get; set; }

    /// <summary>When set, the answer to the next download question, given once the test sets its result.</summary>
    public TaskCompletionSource<bool>? Pending { get; set; }

    /// <summary>Answers to the download questions, in order, before <see cref="Answer"/>.</summary>
    public Queue<PlatformAnswer> DownloadAnswers { get; } = [];

    /// <summary>Answers to the questions about an RDCore found on the machine, in order; not now once there are none left.</summary>
    public Queue<PlatformAnswer> FoundAnswers { get; } = [];

    /// <summary>The folders the user picks, in order; none once there are none left.</summary>
    public Queue<string> Folders { get; } = [];

    public List<PlatformRelease> Asked { get; } = [];
    public List<bool> AskedWithLocate { get; } = [];
    public List<PlatformFolder> FoundAsked { get; } = [];
    public int Picked { get; private set; }
    public List<string> Told { get; } = [];
    public bool InstallShown { get; private set; }

    /// <summary>The threads the installation reported its progress on.</summary>
    public List<int> ReportedOn { get; } = [];

    public async Task<PlatformAnswer> ConfirmDownloadAsync(PlatformRelease release, string target, bool canLocate)
    {
        Asked.Add(release);
        AskedWithLocate.Add(canLocate);
        if (DownloadAnswers.TryDequeue(out var answer))
        {
            return answer;
        }

        return await (Pending?.Task ?? Task.FromResult(Answer)) ? PlatformAnswer.Install : PlatformAnswer.NotNow;
    }

    public Task<PlatformAnswer> ChooseFoundAsync(PlatformFolder found, string target)
    {
        FoundAsked.Add(found);
        return Task.FromResult(FoundAnswers.TryDequeue(out var answer) ? answer : PlatformAnswer.NotNow);
    }

    public Task<string?> PickFolderAsync()
    {
        Picked++;
        return Task.FromResult(Folders.TryDequeue(out var folder) ? folder : null);
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

/// <summary>A machine whose PATH a test sets, and whose processes are recorded, not run.</summary>
internal sealed class FakeMachine : IMachine
{
    public List<string> Path { get; } = [];

    /// <summary>The exit code each run gives.</summary>
    public int ExitCode { get; set; }

    /// <summary>When set, what each run throws instead.</summary>
    public Exception? Failure { get; set; }

    public List<(string Executable, string[] Arguments)> Runs { get; } = [];

    public IReadOnlyList<string> PathFolders => Path;

    public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Runs)
        {
            Runs.Add((executable, arguments.ToArray()));
        }

        return Failure is { } failure ? Task.FromException<int>(failure) : Task.FromResult(ExitCode);
    }
}
