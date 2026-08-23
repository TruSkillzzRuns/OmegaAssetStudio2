using System.Text;

namespace OmegaAssetStudio2.Core.Icons;

/// <summary>
/// Readable names for the run-together subjects icon names use.
/// </summary>
/// <remarks>
/// An icon calls its subject <c>blackpanther</c>, all one word and all lower
/// case, which is not something to show a person. The client's own package
/// files spell the same subjects with their capitals intact, so the capitals
/// are read from there and the word is split where they fall.
///
/// This keeps every name the tool displays sourced from the installed client
/// rather than written into the program, so a client carrying subjects this one
/// does not know about still reads correctly.
/// </remarks>
public sealed class ClientDisplayNames : IDisplayNames
{
    private readonly Dictionary<string, string> _cased;
    private readonly Dictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);

    private ClientDisplayNames(Dictionary<string, string> cased) => _cased = cased;

    /// <summary>
    /// Collects the capitalised spellings out of a cooked folder's file names.
    /// Never throws: with no folder to read, subjects simply keep their own
    /// spelling with a leading capital.
    /// </summary>
    public static ClientDisplayNames FromCookedFolder(string? cookedPath)
    {
        var cased = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(cookedPath) && Directory.Exists(cookedPath))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(cookedPath, "*.upk"))
                {
                    foreach (string token in Path.GetFileNameWithoutExtension(file).Split('_'))
                    {
                        if (token.Length < 3) continue;
                        if (!token.All(char.IsLetter)) continue;

                        // A token written all in lower case teaches nothing
                        // about capitals, and taking it would undo a proper
                        // spelling learned elsewhere.
                        if (!char.IsUpper(token[0])) continue;

                        // The client spells some subjects more than one way -
                        // one file writes the two words joined, another keeps
                        // the capital between them. The spelling that keeps it
                        // is the one that can still be split, so the richer
                        // spelling wins.
                        if (cased.TryGetValue(token, out string? held)
                            && CountInnerCapitals(held) >= CountInnerCapitals(token))
                            continue;

                        cased[token] = token;
                    }
                }
            }
            catch (IOException)
            {
                // A folder that cannot be listed just means no capitals to learn.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new ClientDisplayNames(cased);
    }

    public bool IsSpelledByClient(string subject) => _cased.ContainsKey(subject);

    public string For(string subject)
    {
        if (_resolved.TryGetValue(subject, out string? done)) return done;

        string result = _cased.TryGetValue(subject, out string? cased)
            ? SplitOnCapitals(cased)
            : IconTaxonomy.Capitalise(subject);

        _resolved[subject] = result;
        return result;
    }

    private static int CountInnerCapitals(string token)
    {
        int count = 0;

        for (int i = 1; i < token.Length; i++)
            if (char.IsUpper(token[i])) count++;

        return count;
    }

    /// <summary>Puts a space where a lower-case letter is followed by a capital.</summary>
    private static string SplitOnCapitals(string token)
    {
        var text = new StringBuilder(token.Length + 4);

        text.Append(char.ToUpperInvariant(token[0]));

        for (int i = 1; i < token.Length; i++)
        {
            if (char.IsUpper(token[i]) && char.IsLower(token[i - 1])) text.Append(' ');
            text.Append(token[i]);
        }

        return text.ToString();
    }
}
