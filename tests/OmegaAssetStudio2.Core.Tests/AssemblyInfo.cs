using Xunit;

// Several tests expand whole packages into memory — the largest are well over a
// gigabyte each. Running those concurrently put the test host under enough
// memory pressure to fail intermittently, in a different test each run, which
// looked like a decoder defect and was not: opening all 45,533 packages in the
// three installs sequentially succeeds without a single failure.
//
// Serialising the suite trades a slower run for a result that means something.
// A failure here should always indicate a real defect, never scheduling luck.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OmegaAssetStudio2.Core.Tests;

/// <summary>Settings that must be in place before any test runs.</summary>
internal static class TestRun
{
    /// <summary>Sends this run's backups somewhere of its own.</summary>
    /// <remarks>
    /// Runs before anything else in the assembly, which is the point: the vault
    /// location is read once, the first time anything asks for it. Without this
    /// a test that writes a package leaves a pristine backup in the vault the
    /// application shows the user — who is then looking at a list of files they
    /// have never opened, weighing whatever the tests happened to copy.
    /// </remarks>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Prepare()
    {
        if (System.Environment.GetEnvironmentVariable("OAS2_BACKUP_VAULT") is { Length: > 0 }) return;

        System.Environment.SetEnvironmentVariable("OAS2_BACKUP_VAULT", System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "OmegaAssetStudio2", "test-scratch", "vault"));
    }
}
