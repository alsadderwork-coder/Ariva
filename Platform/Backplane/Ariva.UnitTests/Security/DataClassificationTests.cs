using System.Reflection;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Displays;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using SignInResult = Ariva.Core.Domain.ViewModels.SignInResult;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-094 (ASVS 5.0 V14.2.4, docs/security/data-classification.md section 6, item 4): no answer carries a stored L4
/// value. Over every public view model of Ariva.Core and every type its properties reach (records, lists, dictionaries,
/// arrays): no member holds a salt, a protected or encrypted value or a stored hash; only the answers that hand a credential
/// to its holder, once, have a member named for a credential; and the two records that hold a credential's hash or a refresh
/// token, which are never sent, are no declared answer of a controller of the five hosts. A new type with such a member
/// fails here until it is reviewed and the page is updated.
/// </summary>
public sealed class DataClassificationTests
{
    #region Fields

    private static readonly Assembly Core = typeof(Ariva.Core._IAssemblyMark).Assembly;

    /// <summary>Ariva.Api.Main, Ingest, Stream, Cronz and Integration: the hosts that answer Ariva's clients (the simulator aside).</summary>
    private static readonly IReadOnlyList<Assembly> FiveHosts =
    [
        typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
        typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
    ];

    /// <summary>The endings of a stored secret's member: a salt, a value under Data Protection, an encrypted value.</summary>
    private static readonly string[] StoredSuffixes = ["Salt", "Protected", "Encrypted"];

    /// <summary>The stored hashes of a credential (passwords, client secrets, refresh tokens, recovery codes, device and display credentials).</summary>
    private static readonly string[] StoredNames = ["PasswordHash", "SecretHash", "TokenHash", "CodeHash", "CredentialHash"];

    /// <summary>The names of a member that hands a credential over.</summary>
    private static readonly string[] IssuedNames = ["AccessToken", "RefreshToken", "Credential", "Secret", "ClientSecret", "TotpSecret", "TemporaryPassword", "RecoveryCodes"];

    /// <summary>The answers that hand a credential to its holder, each shown once (section 5.6, L4).</summary>
    private static readonly string[] IssuingAnswers =
    [
        nameof(TokenViewModel),
        nameof(IntegrationTokenViewModel),
        nameof(TotpEnrolmentViewModel),
        nameof(TotpConfirmedViewModel),
        nameof(RecoveryCodesViewModel),
        nameof(UserCreatedViewModel),
        nameof(TemporaryPasswordViewModel),
        nameof(IntegrationClientCredentialsViewModel),
        nameof(DeviceCredentialViewModel),
        nameof(DisplayIssuedViewModel),
        nameof(SiteConfigurationDeviceCredentialViewModel),
        nameof(ImportedDeviceCredentialViewModel)
    ];

    /// <summary>What the hosts hold and never send: the device credential's hash, and sign-in's refresh token.</summary>
    private static readonly Type[] NeverSent = [typeof(DeviceCredentialRecord), typeof(SignInResult)];

    #endregion

    #region Tests

    [Fact]
    public void ViewModels_Should_ExposeNoStoredSecret_When_EveryTypeTheyReachIsRead()
    {
        var reached = Reach(ViewModels());

        var stored = reached
            .SelectMany(type => PropertiesOf(type).Select(property => (Type: type, Name: property.Name)))
            .Where(member => StoredSuffixes.Any(suffix => member.Name.EndsWith(suffix, StringComparison.Ordinal)) || StoredNames.Contains(member.Name, StringComparer.Ordinal))
            .Select(member => $"{member.Type.FullName}.{member.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        stored.Should().BeEmpty("no answer returns a stored L4 value: no salt, protected or encrypted value, and no stored hash (data classification, section 4, L4 access)");
    }

    [Fact]
    public void ViewModels_Should_CarryAnIssuedCredentialOnlyInTheAnswersThatHandItToItsHolder_When_Read()
    {
        var reached = Reach(ViewModels());

        var carriers = reached
            .Where(type => PropertiesOf(type).Any(property => IssuedNames.Contains(property.Name, StringComparer.Ordinal)))
            .Select(type => type.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        carriers.Should().BeEquivalentTo(IssuingAnswers,
            "only the documented answers hand a credential to its holder, once (data classification, section 5.6, L4); " +
            $"not documented: {string.Join(", ", carriers.Except(IssuingAnswers, StringComparer.Ordinal))}; " +
            $"documented but not found: {string.Join(", ", IssuingAnswers.Except(carriers, StringComparer.Ordinal))}");
    }

    [Fact]
    public void Controllers_Should_DeclareNoAnswerThatHoldsAStoredHashOrARefreshToken_When_TheFiveHostsAreRead()
    {
        var declared = DeclaredAnswers().ToList();
        var reached = Reach(declared.Select(d => d.Type));

        declared.Should().Contain(d => d.Type == typeof(TokenViewModel), "the scan reads the [ProducesResponseType] of sign-in, so it sees the hosts' answers");
        reached.Should().NotIntersectWith(NeverSent,
            "DeviceCredentialRecord holds the credential hash and SignInResult the refresh token: neither is ever sent; declared by " +
            string.Join(", ", declared.Where(d => Reach([d.Type]).Intersect(NeverSent).Any()).Select(d => d.Where)));
    }

    [Fact]
    public void Reach_Should_FollowRecordsListsDictionariesAndArrays_When_AViewModelNestsThem()
    {
        var reached = Reach([typeof(OuterViewModel)]);

        reached.Should().Contain([typeof(OuterViewModel), typeof(InListRecord), typeof(InDictionaryRecord), typeof(InArrayRecord), typeof(NestedRecord), typeof(InNullableStruct)]);
        reached.Should().NotContain(typeof(string), "only Ariva's own types are read for members");
        ViewModels().Should().Contain([typeof(TokenViewModel), typeof(DeviceCredentialViewModel)]).And.NotContain(typeof(SignInResult));
    }

    #endregion

    #region Helpers

    /// <summary>Every public type of Ariva.Core whose name ends in ViewModel (a generic one by its name without the arity).</summary>
    private static List<Type> ViewModels() =>
        [.. Core.GetExportedTypes().Where(type => NameOf(type).EndsWith("ViewModel", StringComparison.Ordinal))];

    private static string NameOf(Type type) => type.IsGenericTypeDefinition ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] : type.Name;

    private static PropertyInfo[] PropertiesOf(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// The types reachable from the roots: each root and, for every Ariva type, the types of its public properties, through
    /// arrays and the type arguments of generic types (lists, dictionaries, nullable values, tuples). Only Ariva's own types
    /// are read for members.
    /// </summary>
    private static HashSet<Type> Reach(IEnumerable<Type> roots)
    {
        var reached = new HashSet<Type>();
        var pending = new Stack<Type>(roots);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type.IsGenericParameter)
                continue;
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                pending.Push(type.GetElementType()!);
                continue;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                    pending.Push(argument);
                if (!IsAriva(type))
                    continue;
                type = type.GetGenericTypeDefinition();
            }

            if (!IsAriva(type) || !reached.Add(type))
                continue;
            foreach (var property in PropertiesOf(type))
                pending.Push(property.PropertyType);
        }

        return reached;
    }

    private static bool IsAriva(Type type) => type.Namespace?.StartsWith("Ariva", StringComparison.Ordinal) == true;

    /// <summary>Every answer type a controller of the five hosts declares, on the class or an action, with where it is declared.</summary>
    private static IEnumerable<(Type Type, string Where)> DeclaredAnswers()
    {
        foreach (var controller in FiveHosts.SelectMany(assembly => assembly.GetTypes()).Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            foreach (var answer in controller.GetCustomAttributes(inherit: true).OfType<IApiResponseMetadataProvider>().Where(a => a.Type is not null))
                yield return (answer.Type, controller.FullName);
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var answer in action.GetCustomAttributes(inherit: true).OfType<IApiResponseMetadataProvider>().Where(a => a.Type is not null))
                    yield return (answer.Type, $"{controller.FullName}.{action.Name}");
            }
        }
    }

    #endregion

    #region Fixtures of the walker

    public sealed record NestedRecord(string Value);

    public sealed record InListRecord(NestedRecord Nested);

    public sealed record InDictionaryRecord(int Value);

    public sealed record InArrayRecord(string Value);

    public readonly record struct InNullableStruct(int Value);

    public sealed record OuterViewModel(
        IReadOnlyList<InListRecord> List,
        IReadOnlyDictionary<string, InDictionaryRecord> Map,
        InArrayRecord[] Array,
        InNullableStruct? Optional,
        string Text);

    #endregion
}
