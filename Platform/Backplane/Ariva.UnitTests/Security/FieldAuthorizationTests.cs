using System.Text.RegularExpressions;
using Ariva.Core.Domain.ViewModels;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-095 (ASVS 5.0 V8.1.2): docs/security/field-authorization.md gives the field-level read and write rules per entity and
/// role, each section opening with the answer types it covers on a line that starts "View models:" or "Other answer types:".
/// Every public type of Ariva.Core whose name ends in ViewModel must be named on such a line (the paging envelope in the
/// page's "Not placed" section), so an answer type cannot arrive without its section; and every view model named there must
/// still exist. Types are matched by name (a generic one without its arity); two types of one name are covered by one entry.
/// </summary>
public sealed partial class FieldAuthorizationTests
{
    #region Fields

    private const string Page = "docs/security/field-authorization.md";

    private static readonly System.Reflection.Assembly Core = typeof(TokenViewModel).Assembly;

    #endregion

    #region Parsers

    [GeneratedRegex(@"^(?:View models|Other answer types):", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    private static partial Regex CoverageLine();

    [GeneratedRegex(@"`([A-Za-z][A-Za-z0-9]*ViewModel)`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout.Infinite)]
    private static partial Regex NamedViewModel();

    #endregion

    #region Tests

    [Fact]
    public void Page_Should_PlaceEveryViewModel_When_ArivaCoreIsRead()
    {
        var named = NamedViewModels();
        var exported = ViewModelNames();

        var missing = exported.Except(named, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        exported.Should().Contain([nameof(TokenViewModel), nameof(DeviceViewModel)], "the reflection reads Ariva.Core's answer types");
        missing.Should().BeEmpty($"every answer type needs its entity section in {Page} (a \"View models:\" or \"Other answer types:\" line); missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Page_Should_NameOnlyViewModelsThatExist_When_Read()
    {
        var exported = ViewModelNames();

        var stale = NamedViewModels().Except(exported, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty($"each view model {Page} names must be a public type of Ariva.Core; not found: {string.Join(", ", stale)}");
    }

    #endregion

    #region Helpers

    private static HashSet<string> NamedViewModels() =>
    [
        .. File.ReadAllLines(RepositoryPaths.Resolve(Page))
            .Where(line => CoverageLine().IsMatch(line))
            .SelectMany(line => NamedViewModel().Matches(line).Select(match => match.Groups[1].Value))
    ];

    /// <summary>Every public type of Ariva.Core whose name ends in ViewModel, by name (a generic one without its arity).</summary>
    private static HashSet<string> ViewModelNames() =>
    [
        .. Core.GetExportedTypes()
            .Select(type => type.IsGenericTypeDefinition ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] : type.Name)
            .Where(name => name.EndsWith("ViewModel", StringComparison.Ordinal))
    ];

    #endregion
}
