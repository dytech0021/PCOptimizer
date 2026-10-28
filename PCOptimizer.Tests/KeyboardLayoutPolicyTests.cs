using System;
using System.Collections.Generic;
using System.Linq;
using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava como o troca-teclado identifica teclados e monta os comandos do
/// PowerShell — um erro aqui troca para o teclado errado ou mexe no idioma
/// errado do Windows.
/// </summary>
public sealed class KeyboardLayoutPolicyTests
{
    private static string? NoLayoutIds(int id) => id == 0x0001 ? "00020409" : null;   // EUA Internacional

    [Theory]
    [InlineData(0x04160416L, "0416:00000416")]           // Português (Brasil) ABNT2
    [InlineData(0x04090409L, "0409:00000409")]           // Inglês (EUA)
    [InlineData(unchecked((long)0xFFFFFFFFF0010416), "0416:00020409")]   // PT-BR com EUA Internacional (HKL 0xF001…, estendido em 64 bits)
    [InlineData(0xE0010411L, "0411:E0010411")]           // IME antigo
    public void TipDeUmHklAtivo(long hkl, string expected) =>
        Assert.Equal(expected, KeyboardLayoutPolicy.TipFor(hkl, NoLayoutIds));

    [Fact]
    public void AchaOHklDoTecladoPedido()
    {
        var active = new[] { 0x04160416L, unchecked((long)0xFFFFFFFFF0010416), 0x04090409L };
        Assert.Equal(active[1], KeyboardLayoutPolicy.FindHkl("0416:00020409", active, NoLayoutIds));
        Assert.Equal(active[2], KeyboardLayoutPolicy.FindHkl("0409:00000409", active, NoLayoutIds));
        Assert.Null(KeyboardLayoutPolicy.FindHkl("0407:00000407", active, NoLayoutIds));
    }

    [Fact]
    public void ImeComGuidUsaOIdioma()
    {
        var active = new[] { 0x04110411L };
        const string japanese = "0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}";
        Assert.Equal(0x04110411L, KeyboardLayoutPolicy.FindHkl(japanese, active, NoLayoutIds));
    }

    [Theory]
    [InlineData("pt-BR", true)]
    [InlineData("en-US", true)]
    [InlineData("sr-Latn-RS", true)]
    [InlineData("pt-BR'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("", false)]
    public void SoIdiomaValidoChegaAoPowerShell(string tag, bool ok) =>
        Assert.Equal(ok, KeyboardLayoutPolicy.IsValidTag(tag));

    [Theory]
    [InlineData("0416:00000416", true)]
    [InlineData("0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}", true)]
    [InlineData("0416:0000'; exit", false)]
    [InlineData("0416", false)]
    public void SoTecladoValidoChegaAoPowerShell(string tip, bool ok) =>
        Assert.Equal(ok, KeyboardLayoutPolicy.IsValidTip(tip));

    [Fact]
    public void ScriptDeAdicionarUsaOIdiomaEOTeclado()
    {
        string s = KeyboardLayoutPolicy.BuildAddScript("pt-BR", "0416:00020409");
        Assert.Contains("'pt-BR'", s);
        Assert.Contains("'0416:00020409'", s);
        Assert.Contains("Set-WinUserLanguageList $l -Force", s);
        Assert.Throws<ArgumentException>(() => KeyboardLayoutPolicy.BuildAddScript("pt-BR'; x", "0416:00020409"));
    }

    [Fact]
    public void ScriptDeRemoverNuncaDeixaOWindowsSemTeclado()
    {
        string s = KeyboardLayoutPolicy.BuildRemoveScript("0409:00000409");
        Assert.Contains("'0409:00000409'", s);
        Assert.Contains("if ($l.Count -eq 0) { exit 3 }", s);
    }

    [Fact]
    public void NaoRemoveOUltimoTeclado()
    {
        var one = new List<KeyboardEntry> { new("pt-BR", "Português (Brasil)", "0416:00000416", "ABNT2") };
        Assert.False(KeyboardLayoutPolicy.CanRemove(one, "0416:00000416"));

        var two = new List<KeyboardEntry>(one) { new("en-US", "Inglês (EUA)", "0409:00000409", "EUA") };
        Assert.True(KeyboardLayoutPolicy.CanRemove(two, "0409:00000409"));
        Assert.False(KeyboardLayoutPolicy.CanRemove(two, "0407:00000407"));   // não instalado
    }

    [Fact]
    public void ListaDeAdicionarEscondeOQueJaEstaInstalado()
    {
        var installed = new[] { new KeyboardEntry("pt-BR", "Português (Brasil)", "0416:00000416", "ABNT2") };
        var options = KeyboardLayoutPolicy.AvailableToAdd(installed);
        Assert.DoesNotContain(options, o => o.Tip == "0416:00000416");
        Assert.Contains(options, o => o.Tip == "0409:00000409");
    }

    [Fact]
    public void OpcoesComunsSaoValidasEUnicas()
    {
        foreach (var o in KeyboardLayoutPolicy.CommonOptions)
        {
            Assert.True(KeyboardLayoutPolicy.IsValidTag(o.LanguageTag), o.Label);
            Assert.True(KeyboardLayoutPolicy.IsValidTip(o.Tip), o.Label);
        }
        Assert.Equal(KeyboardLayoutPolicy.CommonOptions.Count,
                     KeyboardLayoutPolicy.CommonOptions.Select(o => o.Tip.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void NomeNaTelaJuntaIdiomaELayout()
    {
        Assert.Equal("Português (Brasil) — EUA Internacional",
            new KeyboardEntry("pt-BR", "Português (Brasil)", "0416:00020409", "EUA Internacional").DisplayName);
        Assert.Equal("Japonês", new KeyboardEntry("ja-JP", "Japonês", "0411:{X}", "").DisplayName);
    }
}
