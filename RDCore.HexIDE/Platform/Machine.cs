using System.Diagnostics;

namespace RDCore.HexIDE.Platform;

/// <summary>What the add-in reads of the machine and runs on it, apart from files: the PATH, and processes.</summary>
internal interface IMachine
{
    /// <summary>The folders of the user's PATH, in order, each once.</summary>
    IReadOnlyList<string> PathFolders { get; }

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/>, without a window, and gives its exit code. One still
    /// running after <paramref name="timeout"/> is stopped, and the run fails with <see cref="TimeoutException"/>.
    /// </summary>
    /// <param name="executable">The executable's full path.</param>
    /// <param name="arguments">Its arguments, one each.</param>
    /// <param name="timeout">The longest it may run.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The machine the add-in runs on.</summary>
internal sealed class Machine : IMachine
{
    /// <inheritdoc/>
    /// <remarks>
    /// On Windows, the user's and the machine's PATH as they are stored, after the process's own: an installation that adds a
    /// folder to them while HexIDE runs is found without a restart.
    /// </remarks>
    public IReadOnlyList<string> PathFolders
    {
        get
        {
            var values = OperatingSystem.IsWindows()
                ? new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
                    .Select(target => Environment.GetEnvironmentVariable("PATH", target))
                : [Environment.GetEnvironmentVariable("PATH")];
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return values.OfType<string>()
                .SelectMany(value => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(Environment.ExpandEnvironmentVariables)
                .Distinct(comparer)
                .ToList();
        }
    }

    /// <inheritdoc/>
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} didn't start.");
        // Read whole, so a process that writes a lot never waits on a full pipe.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{executable} was still running after {timeout.TotalSeconds} seconds, and was stopped.");
        }

        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
        {
            Trace.TraceWarning($"RDCore for HexIDE: {executable} {string.Join(' ', arguments)} exited with {process.ExitCode}: {error.Result.Trim()}");
        }
        return process.ExitCode;
    }
}
