using Lumio.Camera;
using Lumio.Core.Camera;
using Lumio.Core.Settings;

namespace Lumio.Tests;

public class DslrTests
{
    [Fact]
    public void DslrOptions_Normalizes_Iso_Correctly()
    {
        Assert.Equal("Auto", DslrOptions.NormalizeIso("Auto"));
        Assert.Equal("Auto", DslrOptions.NormalizeIso("Automatisch"));
        Assert.Equal("400", DslrOptions.NormalizeIso("ISO 400 (Blitz-Empfehlung)"));
        Assert.Equal("800", DslrOptions.NormalizeIso("800"));
    }

    [Fact]
    public void DslrOptions_Normalizes_Shutter_And_Aperture()
    {
        Assert.Equal("1/125", DslrOptions.NormalizeShutterSpeed("1/125s (Blitz-Standard)"));
        Assert.Equal("1/250", DslrOptions.NormalizeShutterSpeed("1/250 s"));

        Assert.Equal("5.6", DslrOptions.NormalizeAperture("f/5.6 (Empfohlen)"));
        Assert.Equal("2.8", DslrOptions.NormalizeAperture("f/2,8"));
        Assert.Equal("8", DslrOptions.NormalizeAperture("8"));
    }

    [Fact]
    public void DslrOptions_Normalizes_WhiteBalance_And_Ev()
    {
        Assert.Equal("Daylight", DslrOptions.NormalizeWhiteBalance("Tageslicht (5400K)"));
        Assert.Equal("Flash", DslrOptions.NormalizeWhiteBalance("Blitz (5600K)"));
        Assert.Equal("Auto", DslrOptions.NormalizeWhiteBalance("Auto"));

        Assert.Equal("0", DslrOptions.NormalizeExposureCompensation("0 EV (Standard)"));
        Assert.Equal("+1", DslrOptions.NormalizeExposureCompensation("+1.0 EV (Heller)"));
        Assert.Equal("-2", DslrOptions.NormalizeExposureCompensation("-2 EV"));
    }

    [Fact]
    public void MatchCameraValue_Finds_Vendor_Values()
    {
        var canonShutterChoices = new[] { "1/60", "1/100", "1/125", "1/160", "1/200" };
        var matchedShutter = DslrOptions.MatchCameraValue(canonShutterChoices, "1/125", DslrSetting.ShutterSpeed);
        Assert.Equal("1/125", matchedShutter);

        var nikonApertureChoices = new[] { "f/3.5", "f/4.0", "f/5.6", "f/8.0" };
        var matchedAperture = DslrOptions.MatchCameraValue(nikonApertureChoices, "5.6", DslrSetting.Aperture);
        Assert.Equal("f/5.6", matchedAperture);

        var sonyWbChoices = new[] { "Auto", "Daylight", "Incandescent", "Flash" };
        var matchedWb = DslrOptions.MatchCameraValue(sonyWbChoices, "Flash", DslrSetting.WhiteBalance);
        Assert.Equal("Flash", matchedWb);
    }

    [Fact]
    public void MjpegFrameSplitter_Extracts_Complete_Jpeg_Frames()
    {
        var splitter = new MjpegFrameSplitter();

        var fakeJpeg1 = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0x33, 0xFF, 0xD9 };
        var fakeJpeg2 = new byte[] { 0xFF, 0xD8, 0xAA, 0xBB, 0xFF, 0xD9 };

        // Push first frame
        var frames1 = splitter.Push(fakeJpeg1);
        Assert.Single(frames1);
        Assert.Equal(fakeJpeg1, frames1[0]);

        // Push second frame in two chunks
        var frames2A = splitter.Push(fakeJpeg2.AsSpan(0, 3));
        Assert.Empty(frames2A);

        var frames2B = splitter.Push(fakeJpeg2.AsSpan(3));
        Assert.Single(frames2B);
        Assert.Equal(fakeJpeg2, frames2B[0]);
    }

    [Fact]
    public void BoothSettings_Migrates_Legacy_Nikon_Backend_To_Dslr()
    {
        var settings = new BoothSettings
        {
            CameraBackend = CameraBackend.Nikon,
            DigiCamControlUrl = "  http://localhost:5513/  ",
        };

        settings.Normalize();

        Assert.Equal(CameraBackend.Dslr, settings.CameraBackend);
        Assert.Equal("http://localhost:5513", settings.DigiCamControlUrl);
    }

    [Fact]
    public void DigiCamControlManager_FindExecutable_Returns_Null_Or_Valid_Path()
    {
        var configuredCustomPath = "non_existent_folder/CameraControl.exe";
        var found = DigiCamControlManager.FindExecutable(configuredCustomPath);
        // Should ignore non-existent path and either find installed or return null
        if (found is not null)
        {
            Assert.True(System.IO.File.Exists(found));
        }
    }

    [Fact]
    public void DigiCamStatus_Record_Holds_Expected_Defaults()
    {
        var status = new DigiCamStatus(
            IsRunning: true,
            IsConnected: true,
            Message: "Active",
            IsReachable: true,
            ConnectedCamera: "Nikon D5600",
            IsProcessRunning: true);

        Assert.True(status.IsRunning);
        Assert.True(status.IsConnected);
        Assert.True(status.IsReachable);
        Assert.Equal("Nikon D5600", status.ConnectedCamera);
        Assert.True(status.IsProcessRunning);
    }
}
