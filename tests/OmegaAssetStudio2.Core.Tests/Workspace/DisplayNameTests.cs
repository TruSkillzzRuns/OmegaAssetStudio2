using OmegaAssetStudio2.Core.Workspace;
using Xunit;

namespace OmegaAssetStudio2.Core.Tests.Workspace;

/// <summary>
/// Checks that file-name tokens read as the names people know.
/// </summary>
public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("WinterPatrol", "Winter Patrol")]
    [InlineData("NightSentry", "Night Sentry")]
    [InlineData("RapidShot", "Rapid Shot")]
    [InlineData("RapidShot_MissileEffect", "Rapid Shot Missile Effect")]
    [InlineData("FieldMarshal_WinterPatrol_NoHelmet", "Field Marshal Winter Patrol No Helmet")]
    [InlineData("DeathFromAbove", "Death from Above")]
    [InlineData("KingOfTheDead", "King of the Dead")]
    public void WordsAreSeparatedAndSmallWordsStayLowercase(string token, string expected)
        => Assert.Equal(expected, DisplayNames.Humanise(token));

    [Theory]
    [InlineData("OfDeath", "Of Death")]          // never lowered when it leads
    [InlineData("TheHand", "The Hand")]
    public void TheFirstWordIsNeverLowered(string token, string expected)
        => Assert.Equal(expected, DisplayNames.Humanise(token));

    [Theory]
    [InlineData("UltVfx", "Ultimate Effect")]
    [InlineData("ProjDmg", "Projectile Damage")]
    [InlineData("AnimNotify", "Animation Notify")]
    public void ShorthandIsSpelledOut(string token, string expected)
        => Assert.Equal(expected, DisplayNames.Humanise(token));

    [Theory]
    [InlineData("AIMTrooper", "AIM Trooper")]
    [InlineData("UIPanel", "UI Panel")]
    public void RunsOfCapitalsStayTogether(string token, string expected)
        => Assert.Equal(expected, DisplayNames.Humanise(token));

    [Theory]
    [InlineData("Slot1", "Slot 1")]
    [InlineData("Combo2Hit", "Combo 2 Hit")]
    public void DigitsAreTheirOwnWord(string token, string expected)
        => Assert.Equal(expected, DisplayNames.Humanise(token));

    [Fact]
    public void SurnamePrefixesDoNotStartANewWord()
    {
        // "McCoy" and "DeathLok" are the same shape — a capital part-way
        // through — so only a named list of prefixes tells them apart.
        Assert.Equal("McCoy", DisplayNames.Humanise("McCoy"));
        Assert.Equal("MacTaggert", DisplayNames.Humanise("MacTaggert"));
        Assert.Equal("Death Lok", DisplayNames.Humanise("DeathLok"));
    }

    [Fact]
    public void NothingInMeansNothingOut()
    {
        Assert.Equal(string.Empty, DisplayNames.Humanise(string.Empty));
        Assert.Equal(string.Empty, DisplayNames.Humanise("   "));
    }

    [Fact]
    public void ACharacterAndCostumeReadAsOneName()
    {
        Assert.Equal("Night Sentry — Winter Patrol", DisplayNames.Humanise("NightSentry", "WinterPatrol"));
        Assert.Equal("Night Sentry", DisplayNames.Humanise("NightSentry", string.Empty));
    }
}
