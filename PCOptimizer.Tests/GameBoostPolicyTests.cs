using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava as regras que impedem o Game Boost de atrapalhar o próprio jogo —
/// a causa das travadas e da queda de FPS no Dota 2.
/// </summary>
public sealed class GameBoostPolicyTests
{
    [Theory]
    [InlineData("steam")]              // o jogo espera respostas dele e nasce dele
    [InlineData("STEAM")]
    [InlineData("steamwebhelper")]     // overlay novo do Steam
    [InlineData("EpicGamesLauncher")]
    [InlineData("Discord")]
    [InlineData("RTSS")]
    [InlineData("RTSSHooksLoader64")]
    [InlineData("voicemeeterpro")]     // o som do jogo passa por ele
    [InlineData("NVIDIA Share")]
    [InlineData("GameBar")]
    public void PlataformasOverlaysEVozNuncaSaoPresos(string name) =>
        Assert.True(GameBoostPolicy.IsKeepOut(name));

    [Theory]
    [InlineData("chrome")]
    [InlineData("Spotify")]
    [InlineData("OneDrive")]
    [InlineData("")]
    [InlineData(null)]
    public void ProgramasComunsContinuamSendoMovidos(string? name) =>
        Assert.False(GameBoostPolicy.IsKeepOut(name));

    [Theory]
    [InlineData("dota2", true)]
    [InlineData("cs2", true)]
    [InlineData("chrome", false)]      // vídeo em tela cheia
    [InlineData("steamwebhelper", false)]  // Big Picture
    [InlineData("steam", false)]
    [InlineData("Discord", false)]
    [InlineData("", false)]
    public void AtivacaoAutomaticaSoEmJogo(string name, bool expected) =>
        Assert.Equal(expected, GameBoostPolicy.IsPlausibleAutoTarget(name));

    private const uint Normal = 0x20, High = 0x80;

    [Fact]
    public void JogoNormalNaoEstaPreso()
    {
        // Xeon 12 núcleos/24 threads: fundo = 2 últimos núcleos (0xF00000)
        Assert.False(GameBoostPolicy.GameLooksConfined(0xFFFFFF, Normal, 0xF00000));
        Assert.False(GameBoostPolicy.GameLooksConfined(0xFFFFFF, High, 0xF00000));
    }

    [Fact]
    public void JogoQueHerdouOsNucleosDeFundoEstaPreso()
    {
        Assert.True(GameBoostPolicy.GameLooksConfined(0xF00000, Normal, 0xF00000));
        Assert.True(GameBoostPolicy.GameLooksConfined(0x300000, Normal, 0xF00000));
    }

    [Theory]
    [InlineData(0x4000u)]   // abaixo do normal
    [InlineData(0x40u)]     // ociosa
    public void JogoComPrioridadeBaixaEstaPreso(uint priority) =>
        Assert.True(GameBoostPolicy.GameLooksConfined(0xFFFFFF, priority, 0xF00000));

    [Fact]
    public void SemMascaraOuSemLeituraNaoAcusa()
    {
        Assert.False(GameBoostPolicy.GameLooksConfined(0xFFFFFF, Normal, 0));
        Assert.False(GameBoostPolicy.GameLooksConfined(0, Normal, 0xF00000));
    }
}
