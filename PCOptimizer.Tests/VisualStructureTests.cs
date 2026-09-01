using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PCOptimizer.Tests;

public sealed class VisualStructureTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindowExposesTheSixApprovedNavigationAreas()
    {
        XDocument document = LoadXaml("PCOptimizer", "MainWindow.xaml");
        string[] headers = document.Descendants(Presentation + "TabItem")
            .Select(element => (string?)element.Attribute("Header"))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        Assert.Equal(
            ["Visão geral", "Tela & cor", "Desempenho", "Sistema", "Segurança", "Expert"],
            headers);
    }

    [Fact]
    public void MainWindowPreservesEveryCodeBehindControlContract()
    {
        XDocument document = LoadXaml("PCOptimizer", "MainWindow.xaml");
        string[] required =
        [
            "TxtVersion", "BtnScreenshot", "BtnBrightness", "BtnThemeToggle",
            "ChkSelectAll", "TxtSelected", "BtnAutologon", "TxtAutologonStatus",
            "BtnWoL", "TxtWoLStatus", "BtnMalware", "BtnTaskbar", "TxtTaskbarStatus",
            "BtnGameBoost", "TxtGameBoostStatus", "BtnCompetitive", "TxtCompetitiveStatus",
            "BtnMaximizeDisplay", "TxtMaximizeStatus", "BtnDeepRepair",
            "ChkTemp", "StatusTemp", "ChkDisk", "StatusDisk", "ChkRecycleBin",
            "StatusRecycleBin", "ChkWinUpdateCache", "StatusWinUpdateCache",
            "ChkThumbnails", "StatusThumbnails", "ChkShaderCache", "StatusShaderCache",
            "ChkSsdTrim", "StatusSsdTrim", "ChkDefrag", "StatusDefrag",
            "ChkHibernation", "StatusHibernation", "ChkStartup", "StatusStartup",
            "ChkServices", "StatusServices", "ChkNetwork", "StatusNetwork",
            "ChkRegistry", "StatusRegistry", "ChkCortana", "StatusCortana",
            "ChkPowerPlan", "StatusPowerPlan", "ChkVisualEffects", "StatusVisualEffects",
            "ChkBackgroundApps", "StatusBackgroundApps", "ChkStandbyRam", "StatusStandbyRam",
            "ChkTelemetry", "StatusTelemetry", "ChkFastStartup", "StatusFastStartup",
            "ChkSystemRepair", "StatusSystemRepair", "ChkFixDate", "StatusFixDate",
            "ChkBloatware", "StatusBloatware", "ChkBootProcessors", "StatusBootProcessors",
            "ChkGameMode", "StatusGameMode", "ChkGpuScheduling", "StatusGpuScheduling",
            "ChkGameBar", "StatusGameBar", "ChkGamePriority", "StatusGamePriority",
            "ChkGameNetwork", "StatusGameNetwork", "ChkPowerThrottling", "StatusPowerThrottling",
            "ChkFullscreenOpt", "StatusFullscreenOpt", "ChkMousePrecision", "StatusMousePrecision",
            "ChkCoreIsolation", "StatusCoreIsolation", "TxtGpuName", "SldGpuCore",
            "SldGpuMem", "BtnGpuOcApply", "BtnGpuPowerMax", "TxtGpuOcStatus",
            "BtnUvLeve", "BtnUvMedio", "BtnUvAgressivo", "BtnGpuRevert", "TxtGpuUvStatus",
            "ChkExpertCpuMax", "StatusExpertCpuMax", "ChkExpertTimer", "StatusExpertTimer",
            "ChkExpertMsi", "StatusExpertMsi", "TxtCpuUvStatus", "BtnCpuUvTool",
            "LogScroller", "TxtLog", "Progress", "TxtProgress", "BtnRun"
        ];

        AssertNamedControls(document, required);
    }

    [Fact]
    public void FloatingBrightnessWindowPreservesItsCompactControlContract()
    {
        XDocument document = LoadXaml("PCOptimizer", "Views", "BrightnessWindow.xaml");
        string[] required =
        [
            "TxtMonitorCount", "PnlMonitors", "BtnPreset1", "BtnPreset2", "BtnPreset3",
            "TxtPreset1Icon", "TxtPreset1Name", "TxtPreset1Values", "TxtPreset2Icon",
            "TxtPreset2Name", "TxtPreset2Values", "TxtPreset3Icon", "TxtPreset3Name",
            "TxtPreset3Values", "NightSection", "ChkNightLight", "NightLightPanel",
            "SliderNightLight", "TxtNightLightValue", "ChkWinNightLight",
            "WinNightLightPanel", "SliderWinNightLight", "TxtWinNightLightValue",
            "AdvColorPanel", "SliderGamma", "TxtGammaValue", "SliderColorTemp",
            "TxtColorTempValue", "SaturationRow", "SliderSaturation", "TxtSaturationValue",
            "SliderGainR", "TxtGainRValue", "SliderGainG", "TxtGainGValue",
            "SliderGainB", "TxtGainBValue", "DisplaysPanel", "PnlDisplayToggles",
            "RemoteRow", "BtnRemoteMode", "BtnRemoteTune", "TimerPanel", "TxtTimerStatus",
            "TxtTimerCustom", "BtnTimerCancel", "TxtHotkey", "TxtStatus", "BtnSetHotkey"
        ];

        AssertNamedControls(document, required);
    }

    private static void AssertNamedControls(XDocument document, IEnumerable<string> required)
    {
        string[] names = document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        foreach (string name in required)
            Assert.Contains(name, names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static XDocument LoadXaml(params string[] segments)
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        string path = Path.Combine([repositoryRoot, .. segments]);
        return XDocument.Load(path, LoadOptions.SetLineInfo);
    }
}
