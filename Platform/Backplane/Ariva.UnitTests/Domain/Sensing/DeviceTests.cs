using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Sensing;

/// <summary>
/// ARV-021: a device starts in Commissioning, goes Online only after a passed calibration (95 percent by default) for a
/// published zone, goes back to Commissioning when moved, takes health changes only once commissioned, and when retired
/// keeps no credential. Every change raises DeviceRegistryChanged without credential material.
/// </summary>
public sealed class DeviceTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private static Level NewLevel(string site = "DMO")
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Dubai").AddTerminal("T1", "T1", site).AddLevel("ARR", "Arrivals", 0, 100, 60);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
        return level;
    }

    private static Device NewDevice(DeviceFamily family = DeviceFamily.StereoVision, double height = 5, Level level = null) =>
        new("S-17", family, "PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, level ?? NewLevel(), 30, 20, height, 0,
            CoverageFootprint.Assumed(family, height), "A-VIS", Now);

    [Fact]
    public void Register_Should_StartInCommissioningWithTheAssumedFootprint()
    {
        var device = NewDevice();

        device.State.Should().Be(DeviceState.Commissioning);
        device.SiteCode.Should().Be("DMO");
        device.Id.Should().NotBeNull();
        device.FootprintSource.Should().Be(FootprintSource.AssumedFromBoq);
        device.Footprint.Text.Should().Be("10 x 10 m");
        device.Footprint.Note.Should().Contain("Assumed");
        device.DomainEvents.OfType<DeviceRegistryChanged>().Should().ContainSingle(e => e.Change == "Registered" && e.SiteCode == "DMO" && e.State == "Commissioning");
    }

    [Theory]
    [InlineData("s-17")]
    [InlineData("")]
    [InlineData("S 17")]
    [InlineData("S-17-TOO-LONG-CODE")]
    [InlineData("S-17\n")]
    public void Register_Should_RefuseTheCode_When_NotATopologyCode(string code)
    {
        var act = () => new Device(code, DeviceFamily.StereoVision, "PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, NewLevel(), 30, 20, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-1, 20, 5, 0)]
    [InlineData(101, 20, 5, 0)]
    [InlineData(30, 61, 5, 0)]
    [InlineData(30, 20, 1.9, 0)]
    [InlineData(30, 20, 20.1, 0)]
    [InlineData(30, 20, 5, 360)]
    [InlineData(30, 20, 5, -1)]
    [InlineData(double.NaN, 20, 5, 0)]
    public void Register_Should_RefuseThePlacement_When_OutOfRange(double x, double y, double height, double orientation)
    {
        var act = () => new Device("S-1", DeviceFamily.StereoVision, "PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, NewLevel(), x, y, height,
            orientation, CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("PC2SE‮")]
    [InlineData("PC2SE​")]
    [InlineData("PC2\nSE")]
    [InlineData("split surrogate")] // attribute data cannot carry a lone surrogate; the test builds it
    public void Register_Should_RefuseTheModel_When_ItHidesCharacters(string model)
    {
        if (model == "split surrogate")
            model = "PC" + (char)0xD800;
        var act = () => new Device("S-1", DeviceFamily.StereoVision, model, DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, NewLevel(), 30, 20, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_Should_RequireARadius_When_TheDeviceIsLidar()
    {
        var act = () => new Device("L-1", DeviceFamily.Lidar, "Gemini", DeviceTransport.WebSocket, DeviceDialect.Canonical, ClockSource.Ptp, NewLevel(), 30, 20, 8, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 8), "A-VIS", Now);

        act.Should().Throw<ArgumentException>().WithMessage("*radius*");
        NewDevice(DeviceFamily.Lidar, 8).Footprint.Text.Should().Be("10 m radius");
    }

    [Fact]
    public void Calibration_Should_SetOnline_When_AccuracyReachesTheDefaultThreshold()
    {
        var device = NewDevice();

        var calibration = device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 95, 0.4, DeviceCalibration.DefaultThresholdPercent, "Morning peak", zonePublished: true, Now);

        calibration.Passed.Should().BeTrue();
        calibration.ThresholdPercent.Should().Be(95);
        device.State.Should().Be(DeviceState.Online);
        device.IsCommissioned.Should().BeTrue();
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Change.Should().Be("CalibrationPassed");
    }

    [Fact]
    public void Calibration_Should_KeepTheDeviceOutOfService_When_BelowTheThreshold()
    {
        var device = NewDevice();
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 97, 0.4, 95, null, true, Now);

        var failed = device.RecordCalibration(CalibrationMethod.ManualCountTwoObservers, 300, 94.99, 1.2, 95, null, true, Now.AddDays(90));

        failed.Passed.Should().BeFalse();
        device.State.Should().Be(DeviceState.Commissioning, "a failed recalibration takes the device out of counting");
        device.Calibrations.Should().HaveCount(2);
    }

    [Fact]
    public void Calibration_Should_BeRefused_When_TheZoneIsNotPublished()
    {
        var device = NewDevice();

        var act = () => device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 99, 0.2, 95, null, zonePublished: false, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*published*");
        device.State.Should().Be(DeviceState.Commissioning);
        device.Calibrations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(49, 97, 0.5, 95)]
    [InlineData(5001, 97, 0.5, 95)]
    [InlineData(200, 100.1, 0.5, 95)]
    [InlineData(200, double.NaN, 0.5, 95)]
    [InlineData(200, 97, 31, 95)]
    [InlineData(200, 97, 0.5, 49)]
    [InlineData(200, 97, 0.5, 100.5)]
    public void Calibration_Should_RefuseTheNumbers_When_OutOfRange(int sample, double accuracy, double waitError, double threshold)
    {
        var act = () => NewDevice().RecordCalibration(CalibrationMethod.ManualCountTally, sample, accuracy, waitError, threshold, null, true, Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Move_Should_SendTheDeviceBackToCommissioning_When_ItWasOnline()
    {
        var device = NewDevice();
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);

        var recommissioned = device.Move(device.Level, 31, 20, 5, 0, device.Footprint, "A-VIS", Now);

        recommissioned.Should().BeTrue();
        device.State.Should().Be(DeviceState.Commissioning);
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Change.Should().Be("Moved");
    }

    [Fact]
    public void Move_Should_ChangeNothing_When_ThePlacementIsTheSame()
    {
        var device = NewDevice();
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);
        var events = device.DomainEvents.Count;

        device.Move(device.Level, 30, 20, 5, 0, CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now).Should().BeFalse();

        device.State.Should().Be(DeviceState.Online);
        device.DomainEvents.Should().HaveCount(events);
    }

    [Fact]
    public void Move_Should_LeaveTheDeviceAsItWas_When_TheZoneNameIsRefused()
    {
        var device = NewDevice();

        var act = () => device.Move(device.Level, 40, 30, 6, 90, device.Footprint, "Bad‮name", Now);

        act.Should().Throw<ArgumentException>();
        (device.X, device.Y, device.MountingHeightMetres, device.OrientationDegrees, device.QueueZoneName).Should().Be((30d, 20d, 5d, 0d, "A-VIS"));
    }

    [Fact]
    public void Move_Should_Refuse_When_TheLevelIsInAnotherSite()
    {
        var device = NewDevice();

        var act = () => device.Move(NewLevel("OTHER"), 30, 20, 5, 0, device.Footprint, "A-VIS", Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*stays in its site*");
    }

    [Fact]
    public void UpdateDetails_Should_Recommission_When_WhatTheCalibrationMeasuredWithChanges()
    {
        var device = NewDevice();
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);

        device.UpdateDetails("PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ptp, Now).Should().BeFalse("a clock source change keeps the calibration");
        device.State.Should().Be(DeviceState.Online);
        device.UpdateDetails("PC2SE", DeviceTransport.Mqtt, DeviceDialect.Xovis, ClockSource.Ptp, Now).Should().BeTrue("another transport needs a new calibration");
        device.State.Should().Be(DeviceState.Commissioning);
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Change.Should().Be("Updated");

        var refused = () => device.UpdateDetails("PC3\u202E", DeviceTransport.Mqtt, DeviceDialect.Canonical, ClockSource.Ntp, Now);
        refused.Should().Throw<ArgumentException>();
        (device.Model, device.Dialect, device.ClockSource).Should().Be(("PC2SE", DeviceDialect.Xovis, ClockSource.Ptp), "a refused update changes nothing");
    }

    [Fact]
    public void Mapping_Should_BeNamedExactlyForTheDeclarativeDialect()
    {
        Device Declarative(string mapping) => new("L-24", DeviceFamily.Lidar, "Gemini", DeviceTransport.Mqtt, DeviceDialect.Declarative, ClockSource.Ntp, NewLevel(), 30, 20, 6, 0,
            CoverageFootprint.Assumed(DeviceFamily.Lidar, 6), "A-VIS", Now, mapping);

        Declarative("ouster-detect-v1").MappingName.Should().Be("ouster-detect-v1");
        foreach (var bad in new[] { null, "", "Ouster", "ouster detect", "-ouster", "ouster/../x", new string('a', 65) })
            ((Action)(() => Declarative(bad))).Should().Throw<ArgumentException>($"'{bad}' is not a mapping name");
        var xovis = () => new Device("S-17", DeviceFamily.StereoVision, "PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, NewLevel(), 30, 20, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now, "ouster-detect-v1");
        xovis.Should().Throw<ArgumentException>().WithMessage("*only for the declarative dialect*");

        var device = Declarative("ouster-detect-v1");
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);
        device.UpdateDetails("Gemini", DeviceTransport.Mqtt, DeviceDialect.Declarative, ClockSource.Ntp, Now, "ouster-detect-v1").Should().BeFalse();
        device.State.Should().Be(DeviceState.Online);
        device.UpdateDetails("Gemini", DeviceTransport.Mqtt, DeviceDialect.Declarative, ClockSource.Ntp, Now, "ouster-detect-v2").Should().BeTrue("another mapping reads the payload differently");
        device.State.Should().Be(DeviceState.Commissioning);
        device.UpdateDetails("Gemini", DeviceTransport.Mqtt, DeviceDialect.Canonical, ClockSource.Ntp, Now);
        device.MappingName.Should().BeNull("a dialect change drops the mapping");
    }

    [Fact]
    public void SetAccess_Should_NormaliseNetworksAndTheCertificatePin()
    {
        var device = NewDevice();

        device.SetAccess([" 10.20.0.0/24 ", "10.20.1.17/32", "", "2001:db8::/48", "10.20.0.0/24"], "AB:CD" + new string('0', 60), Now);

        device.AllowedSources.Should().Be("10.20.0.0/24,10.20.1.17/32,2001:db8::/48");
        device.AllowedNetworks.Should().HaveCount(3);
        device.ClientCertificateSha256.Should().Be("abcd" + new string('0', 60));
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Change.Should().Be("AccessChanged");

        device.SetAccess([], null, Now);
        device.AllowedSources.Should().BeNull();
        device.ClientCertificateSha256.Should().BeNull();
    }

    [Theory]
    [InlineData("10.20.0.1/24")]
    [InlineData("10.20.0.0")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.20.0.0/33")]
    [InlineData("example.com/24")]
    public void SetAccess_Should_RefuseANetwork_When_ItIsNotAUsefulCidrBlock(string block)
    {
        var device = NewDevice();

        var act = () => device.SetAccess([block], null, Now);

        act.Should().Throw<ArgumentException>();
        device.AllowedSources.Should().BeNull();
    }

    [Fact]
    public void SetAccess_Should_RefuseTooManyNetworksOrABadPin()
    {
        var tooMany = () => NewDevice().SetAccess([.. Enumerable.Range(1, 17).Select(i => $"10.{i}.0.0/16")], null, Now);
        var badPin = () => NewDevice().SetAccess([], "not-a-fingerprint", Now);

        tooMany.Should().Throw<ArgumentException>();
        badPin.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Health_Should_OnlyMoveACommissionedDevice()
    {
        var device = NewDevice();
        var early = () => device.SetHealth(DeviceState.Offline, Now);
        early.Should().Throw<InvalidOperationException>();

        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);
        device.SetHealth(DeviceState.Degraded, Now);
        device.State.Should().Be(DeviceState.Degraded);
        device.SetHealth(DeviceState.Online, Now);

        var commissioning = () => device.SetHealth(DeviceState.Commissioning, Now);
        commissioning.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Retire_Should_RevokeTheCredentialAndRefuseEveryLaterChange()
    {
        var device = NewDevice();
        var issued = DeviceCredentials.New();
        device.IssueCredential(issued.Prefix, issued.Hash, Now);

        device.Retire(Now);

        device.State.Should().Be(DeviceState.Retired);
        device.CredentialHash.Should().BeNull();
        device.RetiredOn.Should().Be(Now);
        var actions = new Action[]
        {
            () => device.Retire(Now),
            () => device.IssueCredential(issued.Prefix, issued.Hash, Now),
            () => device.UpdateDetails("PC3", DeviceTransport.Mqtt, DeviceDialect.Xovis, ClockSource.Ptp),
            () => device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 99, 0.1, 95, null, true, Now)
        };
        foreach (var action in actions)
            action.Should().Throw<InvalidOperationException>().WithMessage("*retired*");
    }

    [Fact]
    public void Remove_Should_OnlyAllowADeviceThatWasNeverCalibrated()
    {
        var calibrated = NewDevice();
        calibrated.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 80, 3, 95, null, true, Now);
        var refused = () => calibrated.Remove("it", Now);
        refused.Should().Throw<InvalidOperationException>();

        var fresh = NewDevice();
        fresh.Remove("it", Now);
        fresh.IsDeleted.Should().BeTrue();
        fresh.CredentialHash.Should().BeNull();
    }

    [Fact]
    public void Credential_Should_BeStoredAsPrefixAndHashOnly()
    {
        var issued = DeviceCredentials.New();
        var device = NewDevice();

        device.IssueCredential(issued.Prefix, issued.Hash, Now);

        issued.Credential.Should().HaveLength(48).And.StartWith("ardk_");
        device.CredentialPrefix.Should().Be(issued.Credential[..13]);
        device.CredentialHash.Should().Be(DeviceCredentials.Hash(issued.Credential)).And.NotContain(issued.Credential[13..]);
        DeviceCredentials.Matches(issued.Credential, device.CredentialHash).Should().BeTrue();
        DeviceCredentials.Matches(issued.Credential[..47] + (issued.Credential[47] == 'A' ? 'B' : 'A'), device.CredentialHash).Should().BeFalse();
        DeviceCredentials.Matches(issued.Credential, null).Should().BeFalse();
        DeviceCredentials.New().Credential.Should().NotBe(issued.Credential);
        device.DomainEvents.OfType<DeviceRegistryChanged>().Should().NotContain(e => e.Change == "CredentialRotated", "the first credential is part of registration");

        device.IssueCredential(DeviceCredentials.New().Prefix, DeviceCredentials.New().Hash, Now);
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Change.Should().Be("CredentialRotated");
    }

    [Theory]
    [InlineData("ardk_short", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ardk_ABCDEFGH", "ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("xxxx_ABCDEFGH", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void Credential_Should_RefuseMalformedPrefixOrHash(string prefix, string hash)
    {
        var act = () => NewDevice().IssueCredential(prefix, hash, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ardk_")]
    [InlineData("ardk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("ARDK_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Credential_Should_NotBeWellFormed_When_ItIsNotADeviceKey(string presented) =>
        DeviceCredentials.IsWellFormed(presented).Should().BeFalse();
}
