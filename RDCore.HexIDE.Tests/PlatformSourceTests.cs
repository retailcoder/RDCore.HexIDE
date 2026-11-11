using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class PlatformSourceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rdcore-source-" + Guid.NewGuid().ToString("N"));
    private readonly Prompts prompts = new() { Answer = true };

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private PlatformSource Source(HttpMessageHandler github, string? platformRoot = null) =>
        new(new AddinSettings { PlatformRoot = platformRoot },
            new PlatformInstaller.PlatformInstaller(
                new PlatformInstallerOptions { Root = Path.Combine(folder, "installed"), Channel = ReleaseChannel.Preview, RuntimeIdentifier = "win-x64" },
                new HttpClient(github)),
            prompts);

    [TestMethod]
    public async Task A_deployed_version_gone_before_its_server_starts_is_that_version_deployed_again_once_the_user_agrees()
    {
        var source = Source(new FakeGitHub().Release("v0.1.0"));
        var choice = await source.GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(choice?.Installed);
        Directory.Delete(choice.Root, recursive: true);

        // A newer release came out meanwhile: the server's launch still names 0.1.0, so 0.1.0 is what comes back.
        await Source(new FakeGitHub().Release("v0.1.0").Release("v0.2.0")).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(Path.Combine(choice.Root, "rdcore.json")));
        Assert.AreEqual(2, prompts.Asked.Count, "asked again before downloading it again");
        Assert.AreEqual("0.1.0", prompts.Asked[1].Version.Text);
    }

    [TestMethod]
    public async Task An_answer_is_never_reused_for_a_later_question()
    {
        var source = Source(new FakeGitHub().Release("v0.1.0"));
        var choice = await source.GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(choice);
        Directory.Delete(choice.Root, recursive: true);
        prompts.Answer = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => source.EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));

        Assert.AreEqual(2, prompts.Asked.Count, "the user was asked again, and said no this time");
        Assert.IsFalse(Directory.Exists(choice.Root));
    }

    [TestMethod]
    public async Task A_deployed_version_gone_and_not_had_again_says_why_for_HexIDE_to_show()
    {
        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(choice);
        Directory.Delete(choice.Root, recursive: true);

        var taken = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Source(new FakeGitHub().Release("v0.2.0")).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));
        Assert.AreEqual(Strings.VersionGone("0.1.0"), taken.Message);

        prompts.Answer = false;
        var declined = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Source(new FakeGitHub().Release("v0.1.0")).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));
        Assert.AreEqual(Strings.PlatformGone("0.1.0"), declined.Message);
    }

    [TestMethod]
    public async Task A_development_root_gone_says_so_and_downloads_nothing()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        var missing = Path.Combine(folder, "rdcore-dev");

        var choice = new PlatformChoice(missing, null);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Source(github, missing).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));

        Assert.AreEqual(0, github.Requests.Count);
    }

    [TestMethod]
    public async Task A_deployed_version_that_lost_its_language_server_is_deployed_again_once_the_user_agrees()
    {
        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(choice);
        File.Delete(LanguageServer(choice));

        await Source(new FakeGitHub().Release("v0.1.0")).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(LanguageServer(choice)));
        Assert.AreEqual(2, prompts.Asked.Count, "asked again before downloading it again");
    }

    [TestMethod]
    public async Task A_development_root_that_lost_its_language_server_says_so()
    {
        var root = Path.Combine(folder, "rdcore-dev");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "rdcore.json"), """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""");
        var choice = new PlatformChoice(root, null);

        var failed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Source(new FakeGitHub(), root).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));

        Assert.AreEqual(Strings.PlatformRootUnusable(root, Strings.LanguageServerMissing), failed.Message);
    }

    // Where a platform's zip has its language server, which is where the server's launch names it.
    private static string LanguageServer(PlatformChoice choice) =>
        Path.Combine(choice.Root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe");

    [TestMethod]
    public async Task A_request_GitHub_doesn_t_answer_in_time_is_told_to_the_user()
    {
        Assert.IsNull(await Source(new TimingOut()).GetAsync(TestContext.CancellationToken));

        Assert.AreEqual(Strings.TimedOut, prompts.Told.Single());
    }

    [TestMethod]
    public async Task A_refused_installation_is_told_to_the_user_in_their_language()
    {
        Assert.IsNull(await Source(new FakeGitHub().Release("v0.1.0", digest: new string('0', 64))).GetAsync(TestContext.CancellationToken));

        Assert.AreEqual(Strings.ChecksumMismatch, prompts.Told.Single());
    }

    [TestMethod]
    public async Task The_installation_runs_off_the_thread_that_asked_for_it()
    {
        var source = Source(new FakeGitHub().Release("v0.1.0"));

        // As HexIDE's UI thread would: one thread, with a context that runs its continuations on it.
        using var ui = new SingleThreadContext();
        var choice = await ui.RunAsync(() => source.GetAsync(TestContext.CancellationToken));

        Assert.IsNotNull(choice);
        Assert.IsTrue(prompts.ReportedOn.Count > 0);
        Assert.IsFalse(prompts.ReportedOn.Contains(ui.ThreadId), "the installation reported from the UI thread");
    }

    [TestMethod]
    public async Task A_failure_nothing_names_is_still_told_to_the_user()
    {
        Assert.IsNull(await Source(new Throwing(new NotSupportedException("Specified method is not supported."))).GetAsync(TestContext.CancellationToken));

        Assert.AreEqual(Strings.InstallFailed(Strings.UnexpectedFailure(nameof(NotSupportedException))), prompts.Told.Single());
    }

    [TestMethod]
    public async Task A_failure_nothing_names_before_a_server_starts_says_why_for_HexIDE_to_show()
    {
        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(choice);
        Directory.Delete(choice.Root, recursive: true);

        var failed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Source(new Throwing(new InvalidOperationException("Sequence contains no elements."))).EnsureStillThereAsync(choice, LanguageServer(choice), TestContext.CancellationToken));

        Assert.AreEqual(Strings.InstallFailed(Strings.UnexpectedFailure(nameof(InvalidOperationException))), failed.Message);
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("fr-CA")]
    public async Task Why_GitHub_couldn_t_be_reached_is_told_in_the_user_s_language_and_not_in_the_exception_s(string culture)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            var unreachable = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (api.github.com:443)");
            Assert.IsNull(await Source(new Throwing(unreachable)).GetAsync(TestContext.CancellationToken));
            Assert.IsNull(await Source(new Answering(HttpStatusCode.ServiceUnavailable)).GetAsync(TestContext.CancellationToken));

            CollectionAssert.AreEqual(
                new[] { Strings.ReleasesUnreachable(Strings.NoConnection), Strings.ReleasesUnreachable(Strings.HttpStatus(503)) },
                prompts.Told);
            Assert.IsFalse(prompts.Told.Any(told => told.Contains("No such host") || told.Contains("Service Unavailable")));
            Assert.AreEqual(culture == "en", prompts.Told[0].StartsWith("RDCore's releases"), "told in the culture's language");
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [TestMethod]
    [DataRow("no release")]
    [DataRow("timed out")]
    [DataRow("unreachable")]
    [DataRow("HTTP status")]
    [DataRow("checksum mismatch")]
    [DataRow("unexpected")]
    public async Task A_run_without_a_platform_fails_with_the_reason_and_tells_the_user_nothing(string failure)
    {
        var (github, reason) = failure switch
        {
            "no release" => ((HttpMessageHandler)new FakeGitHub(), Strings.NoRelease),
            "timed out" => (new TimingOut(), Strings.TimedOut),
            "unreachable" => (new Throwing(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.")),
                Strings.ReleasesUnreachable(Strings.NoConnection)),
            "HTTP status" => (new Answering(HttpStatusCode.ServiceUnavailable), Strings.ReleasesUnreachable(Strings.HttpStatus(503))),
            "checksum mismatch" => (new FakeGitHub().Release("v0.1.0", digest: new string('0', 64)), Strings.ChecksumMismatch),
            _ => (new Throwing(new NotSupportedException("Specified method is not supported.")),
                Strings.InstallFailed(Strings.UnexpectedFailure(nameof(NotSupportedException)))),
        };

        var refused = await Assert.ThrowsExactlyAsync<PlatformUnavailableException>(() => Source(github).GetToRunAsync(TestContext.CancellationToken));

        Assert.AreEqual(reason, refused.Message);
        Assert.AreEqual(0, prompts.Told.Count, "HexIDE shows the run's message");
    }

    private sealed class Throwing(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure;
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class TimingOut : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
    }

    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly Thread thread;

        public SingleThreadContext()
        {
            thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }) { IsBackground = true };
            thread.Start();
        }

        public int ThreadId => thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    done.SetResult(await work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }, null);
            return done.Task;
        }

        public void Dispose() => queue.CompleteAdding();
    }
}
