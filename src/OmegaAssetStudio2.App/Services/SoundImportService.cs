using OmegaAssetStudio2.Core.Audio;
using OmegaAssetStudio2.Core.Packages;
using UpkManager.Repository;

namespace OmegaAssetStudio2.App.Services;

/// <summary>
/// Brings a sound's name into a package that has not got it.
/// </summary>
/// <remarks>
/// The three entries that name a sound are built here rather than carried
/// across, because every number inside one is an index into the package it sits
/// in - which name, which class, which bank - and those differ between one
/// package and the next. Copying the bytes would carry the wrong indices and
/// name something else entirely, or nothing.
/// <para>
/// Their shape was read off the game's own packages, and all three are small:
/// </para>
/// <code>
/// group  (core.package)   12 bytes: -1, none, 0
/// bank   (engine.akbank)  12 bytes: -1, none, 0
/// event  (engine.akevent) 40 bytes: -1, requiredbank, 0, objectproperty, 0,
///                                   4, 0, the bank, none, 0
/// </code>
/// </remarks>
public static class SoundImportService
{
    /// <summary>What a package holds where it says nothing more.</summary>
    private const ulong GroupFlags = 0x0007000400000000;
    private const ulong EventFlags = 0x000f000400000000;

    /// <summary>
    /// Brings the named sounds across, and says what was done.
    /// </summary>
    public static async Task<SoundRestoreService.Outcome> ImportAsync(
        string targetPath,
        string sourcePath,
        IReadOnlyCollection<string> eventNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eventNames);

        if (!File.Exists(targetPath)) return new(false, "the package is not there");
        if (!File.Exists(sourcePath)) return new(false, "the package to take them from is not there");

        SoundImport.Plan plan;
        Package target, source;

        try
        {
            target = Package.Open(targetPath);
            source = Package.Open(sourcePath);

            plan = SoundImport.Work(target, source, eventNames);
        }
        catch (Exception ex)
        {
            return new(false, "the packages could not be read: " + ex.Message);
        }

        if (!plan.Worthwhile)
        {
            return new(false, plan.Trouble.Length > 0
                ? plan.Trouble
                : "every one of those is already named by this package.");
        }

        // What sort of thing each new entry is, taken from one the package
        // already holds rather than worked out.
        int groupClass = ClassOf(target, "package");
        int bankClass = ClassOf(target, "akbank");
        int eventClass = ClassOf(target, "akevent");

        if (bankClass == 0 || eventClass == 0)
            return new(false, "this package does not say what a sound entry is");

        if (groupClass == 0 && plan.Groups.Count > 0)
            return new(false, "this package has no group for a sound to hang under");

        string backup = targetPath + ".before-sound-restore";

        try { File.Copy(targetPath, backup, overwrite: true); }
        catch (Exception ex) { return new(false, "it could not be copied aside first: " + ex.Message); }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(targetPath, ct).ConfigureAwait(false);

            var repository = new UpkFileRepository();

            var header = await repository.LoadUpkFile(targetPath).ConfigureAwait(false);
            await header.ReadHeaderAsync(null).ConfigureAwait(false);

            // Names the package already knows, and the ones it will need.
            var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < header.NameTable.Count; i++)
            {
                string? one = header.NameTable[i].Name?.String;

                if (one is not null) known.TryAdd(one, i);
            }

            var adding = new List<string>();

            int NameFor(string what)
            {
                if (known.TryGetValue(what, out int at)) return at;

                int made = header.NameTable.Count + adding.Count;

                adding.Add(what);
                known[what] = made;

                return made;
            }

            int none = NameFor("None");
            int requiredBank = NameFor("RequiredBank");
            int objectProperty = NameFor("ObjectProperty");

            // Where each new entry will sit, worked out before any is made, so
            // one can point at another.
            var willSit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int next = header.ExportTable.Count;

            foreach (SoundImport.Coming one in plan.All) willSit[one.Name] = next++;

            // What is already there keeps its place.
            foreach ((string name, int at) in SoundImport.Where(target, "akbank")) willSit.TryAdd(name, at);
            foreach ((string name, int at) in SoundImport.Where(target, "package")) willSit.TryAdd(name, at);

            var made = new List<OmegaAssetStudio.UpkRepacker.NewExportSpec>();

            foreach (SoundImport.Coming one in plan.All)
            {
                int mine = NameFor(one.Name);

                bool isEvent = one.Kind.Equals("akevent", StringComparison.OrdinalIgnoreCase);
                bool isGroup = one.Kind.Equals("package", StringComparison.OrdinalIgnoreCase);

                // A group stands on its own; a bank and a sound hang under one.
                int outer = 0;

                if (!isGroup && one.Under.Length > 0 && willSit.TryGetValue(one.Under, out int under))
                    outer = under + 1;

                byte[] body;

                if (isEvent)
                {
                    string wants = SoundImport.BankOf(source, one.SourceAt);

                    if (!willSit.TryGetValue(wants, out int bankAt)) continue;

                    body = new byte[40];

                    Write(body, 0, -1);
                    Write(body, 4, requiredBank);
                    Write(body, 8, 0);
                    Write(body, 12, objectProperty);
                    Write(body, 16, 0);
                    Write(body, 20, 4);
                    Write(body, 24, 0);
                    Write(body, 28, bankAt + 1);
                    Write(body, 32, none);
                    Write(body, 36, 0);
                }
                else
                {
                    body = new byte[12];

                    Write(body, 0, -1);
                    Write(body, 4, none);
                    Write(body, 8, 0);
                }

                made.Add(new OmegaAssetStudio.UpkRepacker.NewExportSpec(
                    Data: body,
                    Patches: Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>(),
                    ClassRef: isEvent ? eventClass : isGroup ? groupClass : bankClass,
                    SuperRef: 0,
                    OuterRef: outer,
                    ArchetypeRef: 0,
                    ObjectNameTableIndex: mine,
                    ObjectNameNumeric: 0,
                    ObjectFlags: isEvent ? EventFlags : GroupFlags,
                    ExportFlags: 0,
                    NetObjects: Array.Empty<int>(),
                    PackageGuid: new byte[16],
                    PackageFlags: 0));
            }

            if (made.Count == 0) return new(false, "nothing could be made ready to bring across");

            var existing = header.ExportTable
                .Select(e => new OmegaAssetStudio.UpkRepacker.ExportBuffer(
                    e.UnrealObjectReader.GetBytes(),
                    Array.Empty<OmegaAssetStudio.UpkRepacker.BulkDataPatch>()))
                .ToList();

            bytes = header.CompressedChunks.Count > 0
                ? OmegaAssetStudio.UpkRepacker.RepackCompressedWithAddedExports(
                    bytes, header, existing, made, adding, out _, out _)
                : OmegaAssetStudio.UpkRepacker.RepackWithAddedExports(
                    bytes, header, existing, made, adding, out _, out _);

            string between = targetPath + ".omtmp";

            await File.WriteAllBytesAsync(between, bytes, ct).ConfigureAwait(false);
            File.Move(between, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Copy(backup, targetPath, overwrite: true); }
            catch (Exception) { }

            return new(false, "it could not be written, and the package was put back: " + ex.Message);
        }

        string had = plan.AlreadyThere.Count == 0
            ? string.Empty
            : $"  {plan.AlreadyThere.Count} were already named and left alone.";

        return new(true,
            $"brought across {plan.Events.Count} sounds"
            + (plan.Groups.Count + plan.Banks.Count > 0
                ? $", with {plan.Groups.Count} groups and {plan.Banks.Count} banks they need"
                : string.Empty)
            + "." + had
            + " They can be chosen for a moment now.",
            plan.Events.Count,
            backup);
    }

    private static void Write(byte[] into, int at, int what) =>
        BitConverter.GetBytes(what).CopyTo(into, at);

    /// <summary>What a package calls the class of things it already holds.</summary>
    private static int ClassOf(Package package, string className)
    {
        for (int i = 0; i < package.Exports.Count; i++)
        {
            string kind;
            try { kind = package.GetExportClassName(i); }
            catch (Exception) { continue; }

            if (kind.Equals(className, StringComparison.OrdinalIgnoreCase))
                return package.Exports[i].Class.Value;
        }

        return 0;
    }
}
