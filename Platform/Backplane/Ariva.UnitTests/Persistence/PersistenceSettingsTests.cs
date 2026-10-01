using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Ariva.Core.Services;
using Ariva.Infra.NHibernate.Mapping;
using Ariva.Infra.Settings;
using FluentAssertions;
using Npgsql;

namespace Ariva.UnitTests.Persistence;

/// <summary>ARV-005: schema update gate, connection string safety, naming conventions and the SQL API shape.</summary>
public sealed class PersistenceSettingsTests
{
    public static TheoryData<string> EnvironmentFiles =>
    [
        "appsettings.base.json",
        "appsettings.base.k8s-dev.json",
        "appsettings.base.k8s-demo.json",
        "appsettings.base.k8s-prd.json"
    ];

    [Theory]
    [MemberData(nameof(EnvironmentFiles))]
    public void AllowSchemaUpdate_Should_BeFalseOrAbsent_When_EnvironmentIsNotVmLocal(string file)
    {
        var value = ReadAllowSchemaUpdate(file);

        value.Should().NotBe(true, $"{file} must get its schema from the versioned scripts, never from SchemaUpdate");
    }

    [Fact]
    public void AllowSchemaUpdate_Should_BeTrue_When_EnvironmentIsVmLocal()
    {
        ReadAllowSchemaUpdate("appsettings.base.vm-local.json").Should().BeTrue();
    }

    [Fact]
    public void BuildConnectionString_Should_KeepPasswordAsOneValue_When_PasswordContainsSeparators()
    {
        var settings = new DatabaseSettings { Password = "p;Include Error Detail=true;x=" };

        var parsed = new NpgsqlConnectionStringBuilder(settings.BuildConnectionString());

        parsed.Password.Should().Be("p;Include Error Detail=true;x=");
        parsed.IncludeErrorDetail.Should().BeFalse("a password must not be able to add connection options");
    }

    [Theory]
    [InlineData(true, SslMode.VerifyFull)]
    [InlineData(false, SslMode.Disable)]
    public void BuildConnectionString_Should_VerifyServerCertificate_When_EncryptionIsOn(bool useEncryption, SslMode expected)
    {
        var settings = new DatabaseSettings { UseEncryption = useEncryption };

        new NpgsqlConnectionStringBuilder(settings.BuildConnectionString()).SslMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("CreatedById", "created_by_id")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("ZoneProfile", "zone_profile")]
    [InlineData("Level2Name", "level2_name")]
    [InlineData("Id", "id")]
    public void ToSnakeCase_Should_SeparateWords_When_NameIsPascalCase(string name, string expected)
    {
        NHibernateMappingRules.ToSnakeCase(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("user", "`user`")]
    [InlineData("order", "`order`")]
    [InlineData("zone", "zone")]
    public void Quote_Should_QuoteOnlyReservedWords_When_NameIsGiven(string identifier, string expected)
    {
        NHibernateMappingRules.Quote(identifier).Should().Be(expected);
    }

    [Fact]
    public void ExecuteSqlAsync_Should_RequireConstantSql_When_DeclaredOnStorageProvider()
    {
        var methods = typeof(IStorageProvider).GetMethods().Where(m => m.Name == nameof(IStorageProvider.ExecuteSqlAsync)).ToList();

        methods.Should().ContainSingle("there is one SQL entry point and no FormattableString overload");
        var sql = methods[0].GetParameters()[0];
        sql.ParameterType.Should().Be<string>();
        sql.GetCustomAttribute<ConstantExpectedAttribute>().Should().NotBeNull("CA1857 then rejects interpolated SQL at compile time");
    }

    private static bool? ReadAllowSchemaUpdate(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
        return document.RootElement.TryGetProperty("Database", out var database) &&
               database.TryGetProperty("AllowSchemaUpdate", out var allow)
            ? allow.GetBoolean()
            : null;
    }
}
