using System.Text;

using OmegaAssetStudio.WinUI.Modules.CharacterSwap;

namespace OmegaAssetStudio2.CharacterSwap;

/// <summary>How a swap is to be done.</summary>
public sealed record SwapOptions
{
    /// <summary>
    /// Give the older game's objects the newer ones' contents where the two
    /// hold the same kind of thing under the same name and it has changed size.
    /// </summary>
    public bool TranslateMatchedSizeChanged { get; init; } = true;

    /// <summary>
    /// Where a newer value would name nothing, keep the older game's, and keep
    /// what only the older game has.
    /// </summary>
    public bool MergeWithTarget { get; init; } = true;

    /// <summary>
    /// Kinds to carry, if not the usual set. Given for narrowing a swap down
    /// while looking for what breaks it.
    /// </summary>
    public IReadOnlySet<string>? OnlyTheseClasses { get; init; }

    /// <summary>
    /// Carry only these objects and what hangs off them, by full path.
    /// </summary>
    /// <remarks>
    /// For taking one thing out of a package that holds many - a single base
    /// material out of a costume that owns two dozen. Without it the rest come
    /// too, and the ones whose references do not survive the move are what
    /// makes the game refuse the file.
    /// </remarks>
    public IReadOnlySet<string>? OnlyTheseObjects { get; init; }

    /// <summary>
    /// Put the result straight over the costume it replaces, in the game.
    /// </summary>
    /// <remarks>
    /// The original is kept first, as <c>&lt;name&gt;.bak</c> beside it, and
    /// every swap is built from that kept copy rather than from whatever is
    /// there now - so swapping twice does not build the second on top of the
    /// first. That keeping is done by the swapping code itself, which has its
    /// own rule for it, and is not done twice here.
    /// </remarks>
    public bool IntoTheGame { get; init; }

    /// <summary>
    /// Give the costume being replaced this costume's name, so the game finds
    /// this one rather than the one it was built on. Needed where a costume has
    /// no counterpart of its own name in the older game.
    /// </summary>
    public bool RenameChassis { get; init; }
}

/// <summary>What a swap produced.</summary>
public sealed record SwapOutcome
{
    public required bool Succeeded { get; init; }
    public string? Refused { get; init; }

    /// <summary>Where it was written, when it was.</summary>
    public string? WrittenTo { get; init; }

    /// <summary>What is worth the reader's attention, in order.</summary>
    public IReadOnlyList<string> Report { get; init; } = [];
}

/// <summary>
/// Takes a costume from a newer game and makes it load in an older one.
/// </summary>
/// <remarks>
/// This file is the only part of the swapping that is written for this
/// application. Everything it calls - the transplant itself, the reader it
/// reads packages with, the walkers, the merger - is the code from the tool
/// whose costumes load, copied and not rewritten, under
/// <c>OmegaAssetStudio2.CharacterSwap</c>.
/// <para>
/// It exists because that tool is driven from a page with three text boxes and
/// this one is driven from a page with a costume list and a game to put it in.
/// So this turns the one into the other, and does nothing else: it decides no
/// policy, translates no bytes, and keeps no rules of its own. Everything it
/// does in what order is what that tool's page does, in the same order - the
/// transplant, then the pass over sibling packages the costume borrows from.
/// </para>
/// <para>
/// Anything that looks like it belongs here and is missing is missing on
/// purpose. If the swap needs to behave differently, the change belongs in the
/// copied code, and the copied code should then be recopied - not adjusted
/// here, where it cannot be checked against the original.
/// </para>
/// </remarks>
public static class Swap
{
    /// <summary>
    /// What is taken from a costume being borrowed from: what draws a surface,
    /// and nothing that would put a second body in the package.
    /// </summary>
    private static readonly HashSet<string> ShadersAndTextures =
        new(StringComparer.OrdinalIgnoreCase) { "materialinstanceconstant", "texture2d", "package" };

    /// <summary>
    /// Builds the swap and writes it, or says why it cannot.
    /// </summary>
    /// <param name="takeFrom">The newer game's costume.</param>
    /// <param name="replace">The older game's costume, which the result stands in for.</param>
    /// <param name="writeTo">
    /// Where to put the result. May be the costume it replaces, which is the
    /// usual case, and never the one it is taken from.
    /// </param>
    public static async Task<SwapOutcome> RunAsync(
        string takeFrom,
        string replace,
        string writeTo,
        SwapOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(takeFrom);
        ArgumentException.ThrowIfNullOrWhiteSpace(replace);
        ArgumentException.ThrowIfNullOrWhiteSpace(writeTo);

        options ??= new SwapOptions();

        string from = Path.GetFullPath(takeFrom);
        string over = Path.GetFullPath(replace);
        string into = Path.GetFullPath(writeTo);

        if (!File.Exists(from)) return Refuse($"There is no costume at {from}.");
        if (!File.Exists(over)) return Refuse($"There is nothing to replace at {over}.");

        // The costume being taken from is never written to: it is the only
        // copy of what is being brought across.
        if (into.Equals(from, StringComparison.OrdinalIgnoreCase))
            return Refuse("Write it somewhere other than the costume it is taken from.");

        bool overTheGame = into.Equals(over, StringComparison.OrdinalIgnoreCase);

        if (overTheGame && !options.IntoTheGame)
        {
            return Refuse(
                "Write it somewhere other than the costume it replaces, or say that it is to go " +
                "into the game.");
        }

        var said = new List<string>();

        try
        {
            return await Task.Run(
                () => Build(from, over, into, overTheGame, options, said),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new SwapOutcome
            {
                Succeeded = false,
                Refused = $"{error.GetType().Name}: {error.Message}",
                Report = [$"{error.GetType().Name}: {error.Message}", .. said],
            };
        }
    }

    private static async Task<SwapOutcome> Build(
        string from,
        string over,
        string into,
        bool overTheGame,
        SwapOptions options,
        List<string> said)
    {
        // The transplant refuses to write over the costume it is replacing,
        // and rightly - it reads that costume while it works. So it is asked
        // for the result beside it, and the result is moved into place after.
        string built = into + ".building";

        if (File.Exists(built)) File.Delete(built);

        void Note(string line) => said.Add(line);

        // A body-only costume borrows its shaders from another costume, and
        // arrives with nothing to draw itself with unless they come first. So
        // the costume it borrows from is transplanted onto the chassis, shaders
        // and textures only, and the body then goes onto the result of that.
        string standsOn = over;
        string? lent = null;

        BorrowedShaders.Lender? lender = await BorrowedShaders.LenderForAsync(from).ConfigureAwait(false);

        if (lender is not null)
        {
            Note($"This costume carries no shader of its own; it wears {Path.GetFileName(lender.Package)}'s "
                + $"({string.Join(", ", lender.Shaders)}).");

            lent = built + ".lent";

            if (File.Exists(lent)) File.Delete(lent);

            var lending = new Phase2MaterialExtender();

            await lending.ExecuteAsync(
                lender.Package,
                over,
                lent,
                Note,
                classAllowlist: ShadersAndTextures,
                aliases: null,
                translateMatchedSizeChanged: options.TranslateMatchedSizeChanged,
                mergeMatchedWithTarget: options.MergeWithTarget).ConfigureAwait(false);

            if (File.Exists(lent)) standsOn = lent;
            else Note("The shaders it borrows could not be brought over; the body goes on as it is.");
        }

        var transplant = new Phase2MaterialExtender();

        Phase2MaterialExtender.Result result = await transplant.ExecuteAsync(
            from,
            standsOn,
            built,
            Note,
            classAllowlist: options.OnlyTheseClasses is null ? null : new HashSet<string>(options.OnlyTheseClasses, StringComparer.OrdinalIgnoreCase),
            onlyTheseRoots: options.OnlyTheseObjects,
            aliases: null,
            translateMatchedSizeChanged: options.TranslateMatchedSizeChanged,
            mergeMatchedWithTarget: options.MergeWithTarget).ConfigureAwait(false);

        // And the packages the costume borrows from, which are patched
        // alongside it. A costume whose shaders live in a sibling package
        // rather than in itself is drawn with nothing unless those come too.
        string siblings = await Siblings(from, over, into, options, Note).ConfigureAwait(false);

        if (!File.Exists(built))
            return Refuse("The transplant reported no error and wrote nothing.");

        File.Copy(built, into, overwrite: true);
        File.Delete(built);

        if (lent is not null && File.Exists(lent)) File.Delete(lent);

        var report = new List<string> { $"Written to {into} - {new FileInfo(into).Length:N0} bytes." };

        if (overTheGame && result.BackupPath is not null)
            report.Add($"The costume that was there is kept at {result.BackupPath}.");

        report.Add("");
        report.AddRange(result.Summary.Replace("\r\n", "\n").Split('\n'));

        if (siblings.Length > 0)
            report.AddRange(siblings.Replace("\r\n", "\n").Split('\n'));

        return new SwapOutcome
        {
            Succeeded = true,
            WrittenTo = into,
            Report = report,
        };
    }

    /// <summary>
    /// The pass over the packages a costume borrows its shaders from, done
    /// exactly as the tool this comes from does it.
    /// </summary>
    private static async Task<string> Siblings(
        string from,
        string over,
        string into,
        SwapOptions options,
        Action<string> note)
    {
        var wrote = new StringBuilder();

        try
        {
            var repo = new UpkManager.Repository.UpkFileRepository();
            var chassis = await repo.LoadUpkFile(over).ConfigureAwait(false);

            await chassis.ReadHeaderAsync(null).ConfigureAwait(false);

            string chassisFolder = Path.GetDirectoryName(over) ?? string.Empty;
            string costumeFolder = Path.GetDirectoryName(from) ?? string.Empty;
            string intoFolder = Path.GetDirectoryName(into) ?? string.Empty;

            var found = CrossUpkSiblingDiscovery.Discover(chassis, chassisFolder, costumeFolder, note);

            wrote.AppendLine();
            wrote.AppendLine("=== Packages it borrows from ===");
            wrote.AppendLine($"Pairs found: {found.Pairs.Count}");

            if (found.UnresolvedImports.Count > 0)
            {
                wrote.AppendLine($"Borrowings left alone: {found.UnresolvedImports.Count}");

                foreach (string one in found.UnresolvedImports.Take(10)) wrote.AppendLine($"  - {one}");
            }

            foreach (var pair in found.Pairs)
            {
                string called = Path.GetFileName(pair.TargetSiblingPath);
                string beside = Path.Combine(intoFolder, called);
                string building = beside + ".building";

                note($"The package it borrows from: {called}");

                try
                {
                    if (File.Exists(building)) File.Delete(building);

                    var also = new Phase2MaterialExtender();

                    var alsoResult = await also.ExecuteAsync(
                        pair.SourceSiblingPath,
                        pair.TargetSiblingPath,
                        building,
                        note,
                        classAllowlist: null,
                        aliases: null,
                        translateMatchedSizeChanged: options.TranslateMatchedSizeChanged,
                        mergeMatchedWithTarget: options.MergeWithTarget).ConfigureAwait(false);

                    if (File.Exists(building))
                    {
                        File.Copy(building, beside, overwrite: true);
                        File.Delete(building);
                    }

                    wrote.AppendLine();
                    wrote.AppendLine($"--- {called} ---");
                    wrote.AppendLine(alsoResult.Summary);
                }
                catch (Exception error)
                {
                    wrote.AppendLine();
                    wrote.AppendLine($"--- {called} could not be done ---");
                    wrote.AppendLine($"{error.GetType().Name}: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            wrote.AppendLine();
            wrote.AppendLine($"The packages it borrows from could not be looked for: {error.Message}");
        }

        return wrote.ToString();
    }

    private static SwapOutcome Refuse(string why) => new()
    {
        Succeeded = false,
        Refused = why,
        Report = [why],
    };
}
