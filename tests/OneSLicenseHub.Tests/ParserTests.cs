using OneSLicenseHub.Core.Services;
using Xunit;

namespace OneSLicenseHub.Tests;

public class ParserTests
{
    [Fact]
    public void SlkParser_ShouldExtractSeriesAndLicenses()
    {
        const string sampleHtml = """
        <ul class="series">
            <li id="1C4E" class="serie li" storable>
                <div class="serie-header">
                    <h2><span class="serie-keyid">1C4E</span> 1С:Управление микрофинансовой организацией, КОРП</h2>
                </div>
                <div class="info-block">
                    <ul class="licenses">
                        <li class="license enabled virtual" id="1000001">
                            <div class="license-keyno">С/Н 1000001 <span class="license-regno">(рег.№ 10000001)</span></div>
                            <div class="license-keytype">Основной 1 лиц.</div>
                            <div class="license-itn">ИНН 7700000000</div>
                            <div class="license-iec">КПП 770001001</div>
                            <div class="license-status">Доступен</div>
                        </li>
                    </ul>
                </div>
            </li>
        </ul>
        """;

        var result = SlkInspectorService.ParseSlkHtml(sampleHtml, "192.168.1.20:9099");

        Assert.NotEmpty(result);
        var lic = result[0];
        Assert.Equal("1C:СЛК 3.0", lic.Category);
        Assert.Contains("1С:Управление микрофинансовой организацией", lic.ProductName);
        Assert.Equal("1000001", lic.DongleId);
        Assert.Equal("10000001", lic.RegistrationNumber);
        Assert.Equal(1, lic.TotalCapacity);
        Assert.Equal("192.168.1.20:9099", lic.SourceServer);
        Assert.Contains("Сервер: 192.168.1.20:9099", lic.DetailsSummary);
    }

    [Fact]
    public void SlkParser_ShouldKeepDisabledSeriesAndExpiredKeys()
    {
        const string sampleHtml = """
        <ul class="series">
            <li id="EA49" class="serie li disabled" storable>
                <div class="serie-header">
                    <h2><span class="serie-keyid">EA49</span> Отраслевое решение</h2>
                </div>
                <div class="info-block">
                    <ul class="licences">
                        <li class="licence disabled virtual" id="2000001"><div class="licence-keyno">С/Н 2000001</div><div class="licence-keytype">Основной 5 лиц.</div><div class="licence-itn">ИНН 7700000000</div><div class="licence-status">Срок действия истёк 03.05.2026</div></li>
                    </ul>
                </div>
            </li>
        </ul>
        """;

        var result = SlkInspectorService.ParseSlkHtml(sampleHtml, "192.168.1.20:9099");

        var lic = Assert.Single(result);
        Assert.Equal("EA49", lic.ProgramNumber);
        Assert.Equal("2000001", lic.DongleId);
        Assert.Equal(string.Empty, lic.RegistrationNumber);
        Assert.Equal(5, lic.TotalCapacity);
        Assert.False(lic.IsActive);
        Assert.Equal("Программная", lic.Medium);
        Assert.Equal("ИНН: 7700000000", lic.Organization);
        Assert.Contains("истёк", lic.LicenseStatus);
    }

    [Fact]
    public void GuardantParser_ShouldExtractYuversKeys()
    {
        const string sampleHtml = """
        <a href="objparams.htm?code=1000000001">YUVERS+</a>
        <a href="objparams.htm?dongle_id=439041025">Guardant Net II (0x1A2B3C01)</a>
        <a href="objparams.htm?dongle_id=439041025&lms_id=-1">Общий ресурс ключа  (0/110)</a>
        <ol><a href="objparams.htm?lic_id=0">rphost.exe (app-server-01.corp.local)</a></ol>
        <ol><a href="objparams.htm?lic_id=1">rphost.exe (app-server-01.corp.local)</a></ol>
        """;

        var result = GuardantInspectorService.ParseGuardantHtml(sampleHtml, "192.168.1.30:3185");

        Assert.NotEmpty(result);
        var lic = result[0];
        Assert.Contains("Ювелирсофт", lic.ProductName);
        Assert.Equal("0x1A2B3C01", lic.DongleId);
        Assert.Equal(110, lic.TotalCapacity);
        Assert.Equal(2, lic.InUseCount);
    }

    [Fact]
    public void GuardantParser_ShouldSplitFullyUsedDongles()
    {
        // A dongle with all seats taken is drawn with nousbdon.gif instead of usbdong.gif
        const string sampleHtml = """
        <ol><img align="middle" src="code.gif">&nbsp;<a href="objparams.htm?code=1000000001">YUVERS+</a>
        <ol><img align="middle" src="nousbdon.gif">&nbsp;<a href="objparams.htm?dongle_id=439041025">Guardant Net II (0x1A2B3C01)</a>
        <ol><img align="middle" src="license.gif">&nbsp;<a href="objparams.htm?dongle_id=439041025&lms_id=-1">Общий ресурс ключа  (2/2)</a>
        <ol><a href="objparams.htm?lic_id=0">rphost.exe (app-server-01.corp.local)</a></ol>
        <ol><a href="objparams.htm?lic_id=1">rphost.exe (app-server-01.corp.local)</a></ol></ol></ol>
        <ol><img align="middle" src="nousbdon.gif">&nbsp;<a href="objparams.htm?dongle_id=439041026">Guardant Net II (0x1A2B3C02)</a>
        <ol><img align="middle" src="license.gif">&nbsp;<a href="objparams.htm?dongle_id=439041026&lms_id=-1">Общий ресурс ключа  (1/31)</a>
        <ol><a href="objparams.htm?lic_id=2">rphost.exe (app-server-02.corp.local)</a></ol></ol></ol></ol>
        </body>
        """;

        var result = GuardantInspectorService.ParseGuardantHtml(sampleHtml, "192.168.1.30:3185");

        Assert.Equal(2, result.Count);
        Assert.Equal("0x1A2B3C01", result[0].DongleId);
        Assert.Equal(2, result[0].TotalCapacity);
        Assert.Equal(2, result[0].InUseCount);
        Assert.Equal("0x1A2B3C02", result[1].DongleId);
        Assert.Equal(31, result[1].TotalCapacity);
        Assert.Equal(1, result[1].InUseCount);
    }

    [Fact]
    public void SentinelParser_ShouldExtractFeatures()
    {
        const string sampleHtml = """
        /*JSON:features*/
        {
        "ndx":"1",
        "ven":"DWRDA",
        "haspname":"100000000000000001",
        "haspid":"100000000000000001",
        "file":"0x0001",
        "ip":"Local",
        "fid":"99",
        "fn":"Format",
        "loc":"Local",
        "isloc":"1",
        "typ":"HASP-SL-AdminMode"
        }
        """;

        var result = SentinelInspectorService.ParseSentinelFeatures(sampleHtml, "192.168.1.40:1947");

        Assert.NotEmpty(result);
        var lic = result[0];
        Assert.Contains("Далион", lic.ProductName);
        Assert.Equal("100000000000000001", lic.DongleId);
        Assert.Equal("99", lic.ProgramNumber);
        Assert.Equal("Sentinel / Далион", lic.Category);
        Assert.True(lic.IsLocalKey);
        Assert.Equal("Программная", lic.Medium); // HASP-SL-* is a software licence
    }
}
