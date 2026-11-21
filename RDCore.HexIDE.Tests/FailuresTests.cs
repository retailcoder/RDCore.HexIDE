using System.Net;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class FailuresTests
{
    [TestMethod]
    public void A_full_disk_is_told_as_one()
    {
        // ERROR_DISK_FULL as an HResult on Windows; ENOSPC elsewhere.
        var full = new IOException("There is not enough space on the disk.", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);

        Assert.AreEqual(Strings.DiskFull, Failures.Reason(full));
    }

    [TestMethod]
    public void Any_other_file_problem_is_told_as_one_without_its_message()
    {
        var locked = new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));

        Assert.AreEqual(Strings.FileProblem, Failures.Reason(locked));
        Assert.AreEqual(Strings.AccessDenied, Failures.Reason(new UnauthorizedAccessException("Access to the path is denied.")));
    }

    [TestMethod]
    public void What_GitHub_answered_is_told_by_its_status_code_and_a_failed_secure_connection_as_one()
    {
        Assert.AreEqual(Strings.HttpStatus(403),
            Failures.Reason(new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).", null, HttpStatusCode.Forbidden)));
        Assert.AreEqual(Strings.SecureConnectionFailed,
            Failures.Reason(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.")));
    }

    [TestMethod]
    public void An_assembly_that_can_t_be_loaded_is_told_by_its_name_as_one_the_add_in_is_missing_wherever_it_was_needed()
    {
        const string assembly = "MediatR, Version=8.0.0.0, Culture=neutral, PublicKeyToken=bb9a41a5e8aaa7e2";
        var missing = new FileNotFoundException($"Could not load file or assembly '{assembly}'.", assembly);

        var told = Strings.AddinIncomplete("MediatR");

        Assert.AreEqual(told, Failures.Reason(missing));
        Assert.AreEqual(told, Failures.Reason(new TypeInitializationException("RDCore.SDK.Workspace.SupportedLanguages", missing)));
        Assert.AreEqual(told, Failures.Unusable(new FileLoadException($"Could not load file or assembly '{assembly}'.", assembly)));
    }

    [TestMethod]
    public void A_missing_file_that_isn_t_an_assembly_is_told_as_a_file_problem()
    {
        var missing = new FileNotFoundException("Could not find file.", Path.Combine(Path.GetTempPath(), "rdcore.json.part"));

        Assert.AreEqual(Strings.FileProblem, Failures.Reason(missing));
    }
}
