using System.Collections.Generic;
using System.Linq;
using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava o aprendizado do HDR do menu do monitor. Um erro aqui escreve no
/// monitor o código errado — no pior caso, energia ou entrada de vídeo.
/// </summary>
public sealed class MonitorHdrDdcPolicyTests
{
    private const string Caps =
        "(prot(monitor)type(lcd)model(MG900)cmds(01 02 03 07 0C E3 F3)" +
        "vcp(02 04 05 08 10 12 14(05 06 08 0B) 16 18 1A 52 60(0F 11 12) 62 AC AE B2 B6 C6 C8 CA " +
        "D6(01 04 05) DC(00 01 02 03 05) DF E9 F0)mswhql(1)asset_eep(40)mccs_ver(2.2))";

    [Fact]
    public void LeOsCodigosSemAsListasDeValores()
    {
        var codes = MonitorHdrDdcPolicy.ParseVcpCodes(Caps);
        Assert.Contains((byte)0xDC, codes);
        Assert.Contains((byte)0xF0, codes);
        Assert.Contains((byte)0x14, codes);
        Assert.DoesNotContain((byte)0x0B, codes);   // valor de 14(...), não código
        Assert.DoesNotContain((byte)0x11, codes);   // valor de 60(...)
        Assert.DoesNotContain((byte)0xE3, codes);   // está em cmds, não em vcp
    }

    [Fact]
    public void AceitaCodigosColados()
    {
        Assert.Equal(new byte[] { 0x02, 0x10, 0xDC }, MonitorHdrDdcPolicy.ParseVcpCodes("vcp(0210DC)"));
    }

    [Fact]
    public void NuncaLeEnergiaEntradaNemBrilho()
    {
        var read = MonitorHdrDdcPolicy.CodesToRead(Caps);
        foreach (byte b in new byte[] { 0xD6, 0x60, 0x04, 0x10, 0x12, 0x52, 0xAC, 0xDF, 0x62 })
            Assert.DoesNotContain(b, read);
        Assert.Contains((byte)0xDC, read);
    }

    [Fact]
    public void SemCapacidadesUsaAFaixaDosFabricantes()
    {
        var read = MonitorHdrDdcPolicy.CodesToRead("");
        Assert.Contains((byte)0xDC, read);
        Assert.Contains((byte)0xE0, read);
        Assert.Contains((byte)0xFF, read);
        Assert.DoesNotContain((byte)0xD6, read);
    }

    [Fact]
    public void AprendeOCodigoQueMudou()
    {
        var on  = new Dictionary<byte, uint> { [0x14] = 5, [0xDC] = 5, [0xF0] = 0 };
        var off = new Dictionary<byte, uint> { [0x14] = 5, [0xDC] = 0, [0xF0] = 0 };
        var learned = MonitorHdrDdcPolicy.Learn(on, off);
        Assert.Equal(new[] { new HdrDdcCode(0xDC, 5, 0) }, learned);
    }

    [Fact]
    public void PreferenciaPeloModoSobreOsAjustesDeImagem()
    {
        // O HDR do menu arrastou a predefinição de cor junto: desligar não pode
        // sobrescrever a cor do usuário quando há um código de modo.
        var on  = new Dictionary<byte, uint> { [0x14] = 11, [0xE9] = 1 };
        var off = new Dictionary<byte, uint> { [0x14] = 5,  [0xE9] = 0 };
        Assert.Equal(new[] { new HdrDdcCode(0xE9, 1, 0) }, MonitorHdrDdcPolicy.Learn(on, off));

        // Sem código de modo, o de imagem é o que existe.
        var on2  = new Dictionary<byte, uint> { [0x14] = 11 };
        var off2 = new Dictionary<byte, uint> { [0x14] = 5 };
        Assert.Equal(new[] { new HdrDdcCode(0x14, 11, 5) }, MonitorHdrDdcPolicy.Learn(on2, off2));
    }

    [Fact]
    public void IgnoraOQueOscilaSozinhoEOsProibidos()
    {
        var on   = new Dictionary<byte, uint> { [0xF0] = 3, [0xD6] = 1, [0x10] = 100, [0xDC] = 2 };
        var on2  = new Dictionary<byte, uint> { [0xF0] = 4, [0xD6] = 1, [0x10] = 100, [0xDC] = 2 };
        var off  = new Dictionary<byte, uint> { [0xF0] = 9, [0xD6] = 4, [0x10] = 50,  [0xDC] = 0 };
        var learned = MonitorHdrDdcPolicy.Learn(on, off, MonitorHdrDdcPolicy.Unstable(on, on2));
        Assert.Equal(new[] { new HdrDdcCode(0xDC, 2, 0) }, learned);
    }

    [Fact]
    public void NadaMudouOuMudouDemaisNaoAprende()
    {
        var same = new Dictionary<byte, uint> { [0xDC] = 1 };
        Assert.Empty(MonitorHdrDdcPolicy.Learn(same, same));

        var on  = Enumerable.Range(0xE0, 6).ToDictionary(i => (byte)i, _ => 1u);
        var off = Enumerable.Range(0xE0, 6).ToDictionary(i => (byte)i, _ => 0u);
        Assert.Empty(MonitorHdrDdcPolicy.Learn(on, off));   // usuário mexeu em tudo
    }

    [Fact]
    public void GravaELeDeVolta()
    {
        var codes = new[] { new HdrDdcCode(0xDC, 5, 0), new HdrDdcCode(0xF0, 1, 0) };
        string s = MonitorHdrDdcPolicy.Serialize(codes);
        Assert.Equal("DC:5>0;F0:1>0", s);
        Assert.Equal(codes, MonitorHdrDdcPolicy.Parse(s));
    }

    [Theory]
    [InlineData("D6:1>4")]          // energia — nunca
    [InlineData("DC:5>0;lixo")]
    [InlineData("ZZ:1>0")]
    [InlineData("")]
    public void ValorRuimOuProibidoNaoViraComando(string value) =>
        Assert.Empty(MonitorHdrDdcPolicy.Parse(value));

    [Fact]
    public void AchaAChaveMesmoComSufixoMudado()
    {
        var keys = new[] { "ddc_MG900_a1b2c3d4" };
        Assert.Equal("ddc_MG900_a1b2c3d4", MonitorHdrDdcPolicy.FindKey(keys, "ddc_MG900_a1b2c3d4#2"));
        Assert.Equal("ddc_MG900_a1b2c3d4", MonitorHdrDdcPolicy.FindKey(keys, "ddc_MG900_ffffffffffff"));
        Assert.Null(MonitorHdrDdcPolicy.FindKey(keys, "ddc_MG800_a1b2c3d4"));
    }

    [Fact]
    public void DoisMonitoresIguaisNaoDividemOAprendizado()
    {
        var keys = new[] { "ddc_MG900_aaaaaaaa", "ddc_MG900_bbbbbbbb" };
        Assert.Null(MonitorHdrDdcPolicy.FindKey(keys, "ddc_MG900_cccccccc"));
    }
}
