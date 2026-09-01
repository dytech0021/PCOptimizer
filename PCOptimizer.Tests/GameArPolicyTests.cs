using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava a regra do botão "Jogar em 16:9 (barras pretas)". Ela já falhou em
/// silêncio uma vez: a checagem passou a receber a chave de identidade do
/// monitor no lugar do device GDI, a leitura da resolução falhou, e o botão
/// desapareceu de TODOS os monitores sem nenhum erro aparecer.
/// </summary>
public sealed class GameArPolicyTests
{
    [Fact]
    public void UltrawideNaResolucaoCheiaOfereceAplicar()
    {
        var action = GameArPolicy.Decide((2560, 1080), (2560, 1080), hasSavedState: false);

        Assert.Equal(GameArAction.OfferApply, action);
        Assert.True(GameArPolicy.ShouldOffer(action));   // a regressão: sumia daqui
    }

    [Fact]
    public void RegistroVelhoComOMonitorJaEmUltrawideNaoViraReverter()
    {
        // Sobra de uma reversão feita fora do programa (painel do Windows). O
        // botão tem que continuar dizendo "aplicar" — dizer "voltar" com a tela
        // já no ultrawide não faz sentido e não faria nada.
        Assert.Equal(GameArAction.OfferApply,
            GameArPolicy.Decide((2560, 1080), (2560, 1080), hasSavedState: true));
    }

    [Fact]
    public void UltrawideEmDezesseisPorNoveComRegistroVoltaAoSalvo()
    {
        var action = GameArPolicy.Decide((3440, 1440), (1920, 1080), hasSavedState: true);

        Assert.Equal(GameArAction.OfferRevertToSaved, action);
        Assert.True(GameArPolicy.IsRevert(action));
    }

    [Fact]
    public void UltrawideEmDezesseisPorNoveSemRegistroVoltaAoNativo()
    {
        // Sem esta saída, clicar gravaria o próprio 1920×1080 como "resolução
        // anterior" e o monitor ficaria preso em 16:9 para sempre.
        var action = GameArPolicy.Decide((2560, 1080), (1920, 1080), hasSavedState: false);

        Assert.Equal(GameArAction.OfferRevertToNative, action);
        Assert.True(GameArPolicy.IsRevert(action));
    }

    [Fact]
    public void PainelDezesseisPorNoveNaoOfereceNada()
    {
        Assert.Equal(GameArAction.Hide,
            GameArPolicy.Decide((1920, 1080), (1920, 1080), hasSavedState: false));
    }

    [Fact]
    public void SemLeituraDoPainelNaoOfereceNada()
    {
        Assert.Equal(GameArAction.Hide,
            GameArPolicy.Decide(null, null, hasSavedState: false));
        Assert.Equal(GameArAction.Hide,
            GameArPolicy.Decide((0, 0), null, hasSavedState: false));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", true)]
    [InlineData(@"\\.\DISPLAY12", true)]
    [InlineData("ddc_MG900_a1b2c3d4e5f60718", false)]   // a chave que quebrou tudo
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SoDeviceGdiEAceitoPelasApisDeResolucao(string? device, bool expected)
    {
        Assert.Equal(expected, DisplayResolutionService.IsGdiDeviceName(device));
    }
}
